using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        private readonly Dictionary<string, float[]> _labelEmbeddings; // 标签 -> 向量
        private readonly Dictionary<string, List<string>> _imageLabels; // 图片文件名 -> 标签列表
        private readonly List<string> _allImages; // 所有图片文件名

        public VectorRetriever(ILLMClient llmClient)
        {
            _llmClient = llmClient;
            _labelEmbeddings = new Dictionary<string, float[]>();
            _imageLabels = new Dictionary<string, List<string>>();
            _allImages = new List<string>();
        }

        public void LoadLabels(string labelFilePath)
        {
            try
            {
                if (!File.Exists(labelFilePath))
                {
                    Console.WriteLine($"[VectorRetriever] Label file not found: {labelFilePath}");
                    return;
                }

                // 只支持JSON格式
                string jsonContent = File.ReadAllText(labelFilePath);
                var labelData = JsonSerializer.Deserialize<LabelFileFormat>(jsonContent);

                if (labelData?.images == null)
                {
                    Console.WriteLine($"[VectorRetriever] Invalid JSON format in {labelFilePath}");
                    return;
                }

                int loadedCount = 0;
                foreach (var imageLabel in labelData.images)
                {
                    if (string.IsNullOrWhiteSpace(imageLabel.filename) || imageLabel.labels == null || imageLabel.labels.Count == 0)
                        continue;

                    var labels = imageLabel.labels
                        .Select(l => l.Trim().ToLower())
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToList();

                    if (labels.Count > 0)
                    {
                        _imageLabels[imageLabel.filename] = labels;
                        _allImages.Add(imageLabel.filename);
                        loadedCount++;
                    }
                }

                Console.WriteLine($"[VectorRetriever] Loaded {loadedCount} labeled images from {labelFilePath}");

                // 预计算所有标签的向量嵌入
                _ = PrecomputeLabelEmbeddingsAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VectorRetriever] Error loading labels: {ex.Message}");
            }
        }

        /// <summary>
        /// 预计算所有标签的向量嵌入
        /// </summary>
        private async Task PrecomputeLabelEmbeddingsAsync()
        {
            try
            {
                var allLabels = _imageLabels.Values
                    .SelectMany(labels => labels)
                    .Distinct()
                    .ToList();

                Console.WriteLine($"[VectorRetriever] Precomputing embeddings for {allLabels.Count} unique labels...");

                foreach (var label in allLabels)
                {
                    if (!_labelEmbeddings.ContainsKey(label))
                    {
                        try
                        {
                            var embedding = await _llmClient.GetEmbeddingAsync(label);
                            _labelEmbeddings[label] = embedding;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[VectorRetriever] Failed to compute embedding for '{label}': {ex.Message}");
                        }
                    }
                }

                Console.WriteLine($"[VectorRetriever] Precomputed {_labelEmbeddings.Count} label embeddings");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VectorRetriever] Error precomputing embeddings: {ex.Message}");
            }
        }

        public async Task<List<string>> FindMatchingImagesAsync(List<string> emotions, int topK = 3)
        {
            try
            {
                // 如果没有标签，返回空列表（将使用随机选择）
                if (_imageLabels.Count == 0)
                {
                    Console.WriteLine("[VectorRetriever] No labels loaded, falling back to random selection");
                    return new List<string>();
                }

                // 计算情感关键词的向量嵌入
                var emotionEmbeddings = new List<float[]>();
                foreach (var emotion in emotions)
                {
                    try
                    {
                        var embedding = await _llmClient.GetEmbeddingAsync(emotion);
                        emotionEmbeddings.Add(embedding);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[VectorRetriever] Failed to get embedding for '{emotion}': {ex.Message}");
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

                Console.WriteLine($"[VectorRetriever] Found {topImages.Count} matching images for emotions: {string.Join(", ", emotions)}");
                return topImages;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VectorRetriever] Error finding matches: {ex.Message}");
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
