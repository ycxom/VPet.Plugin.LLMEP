using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VPet.Plugin.LLMEP.Utils;

namespace VPet.Plugin.LLMEP
{
    /// <summary>
    /// 表情包标签管理器。
    ///
    /// DIY 图片平铺在 文档\VPetLLM\Emotion\（见 <see cref="DiyStickerStorage"/>），
    /// 标签文件以文件名为键。标签分三类：
    ///   · 心情标签 happy / normal / poor / ill —— 决定图片在哪些心情下出现，可多选；一个都没有 = 泛用；
    ///   · 保留标签 general（等同于没有心情标签）、__llm_processed__（AI 已处理标记）—— 不参与匹配；
    ///   · 其余为普通标签，供情感分析做精确匹配。
    /// 设置窗口（UI 线程）和 AI 批量打标（后台线程）共用同一个实例，所有读写都加锁。
    /// </summary>
    public class LabelManager
    {
        public const string MoodHappy = "happy";
        public const string MoodNormal = "normal";
        public const string MoodPoor = "poor";
        public const string MoodIll = "ill";
        public const string GeneralTag = "general";
        public const string LlmProcessedTag = "__llm_processed__";

        /// <summary>全部心情标签，按界面显示顺序。</summary>
        public static readonly string[] MoodTags = { MoodHappy, MoodNormal, MoodPoor, MoodIll };

        private readonly string diyExpressionPath;
        private readonly string labelFilePath;
        private readonly object _sync = new object();
        private Dictionary<string, List<string>> imageLabels;

        public LabelManager()
        {
            diyExpressionPath = DiyStickerStorage.RootPath;
            labelFilePath = DiyStickerStorage.LabelsFilePath;
            imageLabels = new Dictionary<string, List<string>>();
        }

        public static bool IsMoodTag(string tag) =>
            tag != null && MoodTags.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase);

        /// <summary>不参与匹配的标签（general、AI 处理标记），也不应出现在给模型的候选标签里。</summary>
        public static bool IsReservedTag(string tag) =>
            tag != null && (string.Equals(tag.Trim(), GeneralTag, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(tag.Trim(), LlmProcessedTag, StringComparison.Ordinal));

        /// <summary>心情标签的中文显示名。</summary>
        public static string MoodDisplayName(string mood) => mood?.ToLowerInvariant() switch
        {
            MoodHappy => "开心",
            MoodNormal => "正常",
            MoodPoor => "状态不佳",
            MoodIll => "生病",
            _ => "泛用"
        };

        /// <summary>
        /// 扫描 DIY 目录下的全部图片（单层）。
        /// </summary>
        public List<ImageInfo> ScanImages()
        {
            var result = new List<ImageInfo>();

            try
            {
                DiyStickerStorage.EnsureRoot();

                foreach (var file in DiyStickerStorage.EnumerateImages())
                {
                    var relativePath = GetRelativePath(file);
                    result.Add(new ImageInfo
                    {
                        FileName = Path.GetFileName(file),
                        FullPath = file,
                        RelativePath = relativePath,
                        Size = new FileInfo(file).Length,
                        Tags = GetImageTags(relativePath)
                    });
                }

                Logger.Info("LabelManager", $"扫描完成，找到 {result.Count} 张图片");
            }
            catch (Exception ex)
            {
                Logger.Error("LabelManager", $"扫描图片时发生错误: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 获取图片的相对路径（相对于DIY表情包目录；单层目录下即文件名）
        /// </summary>
        private string GetRelativePath(string fullPath)
        {
            return Path.GetRelativePath(diyExpressionPath, fullPath).Replace('\\', '/');
        }

        /// <summary>
        /// 获取指定图片的标签
        /// </summary>
        public List<string> GetImageTags(string relativePath)
        {
            lock (_sync)
            {
                return imageLabels.TryGetValue(relativePath, out var tags)
                    ? new List<string>(tags)
                    : new List<string>();
            }
        }

        /// <summary>
        /// 图片用于哪些心情（小写心情标签）。空集合 = 泛用。
        /// </summary>
        public List<string> GetImageMoods(string relativePath)
        {
            return GetImageTags(relativePath)
                .Where(IsMoodTag)
                .Select(t => t.Trim().ToLowerInvariant())
                .Distinct()
                .OrderBy(t => Array.IndexOf(MoodTags, t))
                .ToList();
        }

        /// <summary>
        /// 只改心情标签，普通标签和 AI 处理标记原样保留。
        /// </summary>
        public void SetImageMoods(string relativePath, IEnumerable<string> moods)
        {
            lock (_sync)
            {
                var kept = GetImageTags(relativePath).Where(t => !IsMoodTag(t) && !string.Equals(t, GeneralTag, StringComparison.OrdinalIgnoreCase));
                SetImageTags(relativePath, kept.Concat(moods.Where(IsMoodTag)).ToList());
            }
        }

        /// <summary>
        /// 获取指定图片的普通标签（排除心情标签和保留标签）
        /// </summary>
        public List<string> GetImageNormalTags(string relativePath)
        {
            return GetImageTags(relativePath).Where(tag => !IsMoodTag(tag) && !IsReservedTag(tag)).ToList();
        }

        /// <summary>
        /// 只改普通标签，心情标签和 AI 处理标记原样保留。
        /// </summary>
        public void SetImageNormalTags(string relativePath, IEnumerable<string> normalTags)
        {
            lock (_sync)
            {
                var kept = GetImageTags(relativePath).Where(t => IsMoodTag(t) || string.Equals(t, LlmProcessedTag, StringComparison.Ordinal));
                SetImageTags(relativePath, normalTags.Where(t => !IsMoodTag(t) && !IsReservedTag(t)).Concat(kept).ToList());
            }
        }

        /// <summary>
        /// 设置指定图片的标签
        /// </summary>
        public void SetImageTags(string relativePath, List<string> tags)
        {
            // 清理标签：去除空白、去重、排序；心情标签统一成小写
            var cleanTags = tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Select(tag => IsMoodTag(tag) ? tag.ToLowerInvariant() : tag)
                .Distinct()
                .OrderBy(tag => tag)
                .ToList();

            lock (_sync)
            {
                if (cleanTags.Count > 0)
                {
                    imageLabels[relativePath] = cleanTags;
                }
                else
                {
                    imageLabels.Remove(relativePath);
                }
            }

            Logger.Debug("LabelManager", $"设置图片标签: {relativePath} -> [{string.Join(", ", cleanTags)}]");
        }

        /// <summary>
        /// 从文件加载标签数据
        /// </summary>
        public void LoadLabels()
        {
            try
            {
                if (File.Exists(labelFilePath))
                {
                    var json = File.ReadAllText(labelFilePath);
                    var loadedLabels = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);

                    if (loadedLabels != null)
                    {
                        lock (_sync)
                        {
                            imageLabels = loadedLabels;
                        }
                        Logger.Info("LabelManager", $"从文件加载了 {loadedLabels.Count} 个图片的标签数据");
                    }
                }
                else
                {
                    Logger.Info("LabelManager", "标签文件不存在，将创建新的标签数据");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("LabelManager", $"加载标签文件时发生错误: {ex.Message}");
                lock (_sync)
                {
                    imageLabels = new Dictionary<string, List<string>>();
                }
            }
        }

        /// <summary>
        /// 保存标签数据到文件。先写临时文件再替换，写到一半崩溃也不会把标签文件弄坏。
        /// </summary>
        public void SaveLabels()
        {
            try
            {
                var directory = Path.GetDirectoryName(labelFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                string json;
                int count;
                lock (_sync)
                {
                    json = JsonSerializer.Serialize(imageLabels, options);
                    count = imageLabels.Count;
                }

                var tempPath = labelFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, labelFilePath, overwrite: true);

                Logger.Info("LabelManager", $"保存了 {count} 个图片的标签数据到文件: {labelFilePath}");
            }
            catch (Exception ex)
            {
                Logger.Error("LabelManager", $"保存标签文件时发生错误: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 获取所有标签的统计信息
        /// </summary>
        public Dictionary<string, int> GetTagStatistics()
        {
            var tagCounts = new Dictionary<string, int>();

            lock (_sync)
            {
                foreach (var imageTags in imageLabels.Values)
                {
                    foreach (var tag in imageTags)
                    {
                        tagCounts[tag] = tagCounts.TryGetValue(tag, out var c) ? c + 1 : 1;
                    }
                }
            }

            return tagCounts.OrderByDescending(kv => kv.Value)
                           .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        /// <summary>
        /// 检查图片是否已被LLM处理过
        /// </summary>
        public bool IsImageProcessedByLLM(string relativePath)
        {
            return GetImageTags(relativePath).Contains(LlmProcessedTag);
        }

        /// <summary>
        /// 标记图片已被LLM处理
        /// </summary>
        public void MarkImageAsProcessedByLLM(string relativePath)
        {
            lock (_sync)
            {
                var tags = GetImageTags(relativePath);
                if (!tags.Contains(LlmProcessedTag))
                {
                    tags.Add(LlmProcessedTag);
                    SetImageTags(relativePath, tags);
                    Logger.Info("LabelManager", $"标记图片为已处理: {relativePath}");
                }
            }
        }
    }

    /// <summary>
    /// 图片信息类
    /// </summary>
    public class ImageInfo
    {
        public string FileName { get; set; }
        public string FullPath { get; set; }
        public string RelativePath { get; set; }
        public long Size { get; set; }
        public List<string> Tags { get; set; } = new List<string>();

        public string FormattedSize
        {
            get
            {
                if (Size < 1024) return $"{Size} B";
                if (Size < 1024 * 1024) return $"{Size / 1024.0:F1} KB";
                return $"{Size / (1024.0 * 1024.0):F1} MB";
            }
        }
    }
}
