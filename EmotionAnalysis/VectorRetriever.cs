using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VPet.Plugin.LLMEP.EmotionAnalysis
{
    /// <summary>
    /// 标签文件JSON格式
    /// </summary>
    public class LabelFileFormat
    {
        public List<ImageLabel> images { get; set; }
    }

    public class ImageLabel
    {
        public string filename { get; set; }
        public List<string> labels { get; set; }
    }

    /// <summary>
    /// 向量检索器实现
    /// </summary>
    public class VectorRetriever : IVectorRetriever
    {
        private readonly ILLMClient _llmClient;
        // 预计算在后台写、匹配时读，所以用并发字典
        private readonly ConcurrentDictionary<string, float[]> _labelEmbeddings; // 标签 -> 向量
        private readonly Dictionary<string, List<string>> _imageLabels; // 图片文件名 -> 标签列表
        private readonly List<string> _allImages; // 所有图片文件名
        // 内置标签和 DIY 标签各触发一次预计算，两轮排队跑，第二轮跳过已算过的标签
        private readonly SemaphoreSlim _precomputeLock = new(1, 1);
        // 渠道没有嵌入接口（如 Free）。一旦确认就不再逐个标签去试，否则每次启动刷几百行日志
        private volatile bool _embeddingUnsupported;

        public VectorRetriever(ILLMClient llmClient)
        {
            _llmClient = llmClient;
            _labelEmbeddings = new ConcurrentDictionary<string, float[]>();
            _imageLabels = new Dictionary<string, List<string>>();
            _allImages = new List<string>();
        }

        private void MarkEmbeddingUnsupported(NotSupportedException ex)
        {
            if (_embeddingUnsupported) return;
            _embeddingUnsupported = true;
            Utils.Logger.Log($"[VectorRetriever] 当前渠道不支持嵌入（{ex.Message}），向量检索停用，改用标签匹配");
        }

        public void LoadLabels(string labelFilePath)
        {
            try
            {
                if (!File.Exists(labelFilePath))
                {
                    Utils.Logger.Log($"[VectorRetriever] Label file not found: {labelFilePath}");
                    return;
                }

                // 只支持JSON格式
                string jsonContent = File.ReadAllText(labelFilePath);
                var labelData = JsonSerializer.Deserialize<LabelFileFormat>(jsonContent);

                if (labelData?.images == null)
                {
                    Utils.Logger.Log($"[VectorRetriever] Invalid JSON format in {labelFilePath}");
                    return;
                }

                LoadLabels(labelData.images
                    .Where(i => !string.IsNullOrWhiteSpace(i.filename) && i.labels != null)
                    .GroupBy(i => i.filename)
                    .ToDictionary(g => g.Key, g => g.Last().labels), labelFilePath);
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"[VectorRetriever] Error loading labels: {ex.Message}");
            }
        }

        public void LoadLabels(IDictionary<string, List<string>> imageLabels, string source)
        {
            int loadedCount = 0;
            foreach (var entry in imageLabels)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value == null || entry.Value.Count == 0)
                    continue;

                var labels = entry.Value
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Trim().ToLower())
                    .ToList();

                if (labels.Count > 0)
                {
                    if (!_imageLabels.ContainsKey(entry.Key))
                        _allImages.Add(entry.Key);
                    _imageLabels[entry.Key] = labels;
                    loadedCount++;
                }
            }

            Utils.Logger.Log($"[VectorRetriever] Loaded {loadedCount} labeled images from {source}");

            // 预计算所有标签的向量嵌入
            _ = PrecomputeLabelEmbeddingsAsync();
        }

        /// <summary>
        /// 预计算所有标签的向量嵌入
        /// </summary>
        private async Task PrecomputeLabelEmbeddingsAsync()
        {
            // 在调用方线程上取快照：LoadLabels 返回后调用方可能接着再次写 _imageLabels
            var allLabels = _imageLabels.Values
                .SelectMany(labels => labels)
                .Distinct()
                .ToList();

            await _precomputeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_embeddingUnsupported)
                    return;

                var pending = allLabels.Where(l => !_labelEmbeddings.ContainsKey(l)).ToList();
                if (pending.Count == 0)
                    return;

                Utils.Logger.Log($"[VectorRetriever] Precomputing embeddings for {pending.Count} unique labels...");

                foreach (var label in pending)
                {
                    try
                    {
                        _labelEmbeddings[label] = await _llmClient.GetEmbeddingAsync(label).ConfigureAwait(false);
                    }
                    catch (NotSupportedException ex)
                    {
                        MarkEmbeddingUnsupported(ex);
                        return;
                    }
                    catch (Exception ex)
                    {
                        Utils.Logger.Log($"[VectorRetriever] Failed to compute embedding for '{label}': {ex.Message}");
                    }
                }

                Utils.Logger.Log($"[VectorRetriever] Precomputed {_labelEmbeddings.Count} label embeddings");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"[VectorRetriever] Error precomputing embeddings: {ex.Message}");
            }
            finally
            {
                _precomputeLock.Release();
            }
        }

        public async Task<List<string>> FindMatchingImagesAsync(List<string> emotions, int topK = 3)
        {
            try
            {
                // 如果没有标签，返回空列表（将使用随机选择）
                if (_imageLabels.Count == 0)
                {
                    Utils.Logger.Log("[VectorRetriever] No labels loaded, falling back to random selection");
                    return new List<string>();
                }

                if (_embeddingUnsupported)
                    return new List<string>();

                // 计算情感关键词的向量嵌入
                var emotionEmbeddings = new List<float[]>();
                foreach (var emotion in emotions)
                {
                    try
                    {
                        var embedding = await _llmClient.GetEmbeddingAsync(emotion);
                        emotionEmbeddings.Add(embedding);
                    }
                    catch (NotSupportedException ex)
                    {
                        MarkEmbeddingUnsupported(ex);
                        return new List<string>();
                    }
                    catch (Exception ex)
                    {
                        Utils.Logger.Log($"[VectorRetriever] Failed to get embedding for '{emotion}': {ex.Message}");
                    }
                }

                if (emotionEmbeddings.Count == 0)
                {
                    return new List<string>();
                }

                // 计算每个图片的相似度分数
                var imageScores = new Dictionary<string, float>();

                foreach (var kvp in _imageLabels)
                {
                    var filename = kvp.Key;
                    var labels = kvp.Value;

                    float maxSimilarity = 0;

                    // 对于每个标签，计算与所有情感的最大相似度
                    foreach (var label in labels)
                    {
                        if (_labelEmbeddings.TryGetValue(label, out var labelEmbedding))
                        {
                            foreach (var emotionEmbedding in emotionEmbeddings)
                            {
                                var similarity = CosineSimilarity(labelEmbedding, emotionEmbedding);
                                maxSimilarity = Math.Max(maxSimilarity, similarity);
                            }
                        }
                    }

                    imageScores[filename] = maxSimilarity;
                }

                // 返回Top K最相似的图片
                var topImages = imageScores
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(topK)
                    .Select(kvp => kvp.Key)
                    .ToList();

                Utils.Logger.Log($"[VectorRetriever] Found {topImages.Count} matching images for emotions: {string.Join(", ", emotions)}");
                return topImages;
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"[VectorRetriever] Error finding matches: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// 计算余弦相似度
        /// </summary>
        private float CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length)
                return 0;

            float dotProduct = 0;
            float magnitudeA = 0;
            float magnitudeB = 0;

            for (int i = 0; i < a.Length; i++)
            {
                dotProduct += a[i] * b[i];
                magnitudeA += a[i] * a[i];
                magnitudeB += b[i] * b[i];
            }

            magnitudeA = (float)Math.Sqrt(magnitudeA);
            magnitudeB = (float)Math.Sqrt(magnitudeB);

            if (magnitudeA == 0 || magnitudeB == 0)
                return 0;

            return dotProduct / (magnitudeA * magnitudeB);
        }
    }
}
