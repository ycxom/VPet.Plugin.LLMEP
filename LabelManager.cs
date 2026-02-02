using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VPet.Plugin.LLMEP.Utils;

namespace VPet.Plugin.LLMEP
{
    /// <summary>
    /// 表情包标签管理器
    /// </summary>
    public class LabelManager
    {
        private readonly string diyExpressionPath;
        private readonly string labelFilePath;
        private Dictionary<string, List<string>> imageLabels;

        public LabelManager(string pluginDirectory)
        {
            diyExpressionPath = Path.Combine(pluginDirectory, "DIY_Expression");
            labelFilePath = Path.Combine(pluginDirectory, "plugin", "data", "diy_labels.json");
            imageLabels = new Dictionary<string, List<string>>();
        }

        /// <summary>
        /// 扫描DIY_Expression目录下的所有图片
        /// </summary>
        /// <returns>按目录分组的图片文件列表</returns>
        public Dictionary<string, List<ImageInfo>> ScanImages()
        {
            var result = new Dictionary<string, List<ImageInfo>>();

            try
            {
                if (!Directory.Exists(diyExpressionPath))
                {
                    Directory.CreateDirectory(diyExpressionPath);
                    Logger.Info("LabelManager", $"创建DIY_Expression目录: {diyExpressionPath}");
                }

                var supportedExtensions = new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };

                // 扫描所有子目录
                var directories = Directory.GetDirectories(diyExpressionPath);

                foreach (var directory in directories)
                {
                    var dirName = Path.GetFileName(directory);
                    var imageFiles = new List<ImageInfo>();

                    var files = Directory.GetFiles(directory)
                        .Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLower()))
                        .OrderBy(f => Path.GetFileName(f));

                    foreach (var file in files)
                    {
                        var relativePath = GetRelativePath(file);
                        var imageInfo = new ImageInfo
                        {
                            FileName = Path.GetFileName(file),
                            FullPath = file,
                            RelativePath = relativePath,
                            Directory = dirName,
                            Size = new FileInfo(file).Length,
                            Tags = GetImageTags(relativePath)
                        };

                        imageFiles.Add(imageInfo);
                    }

                    if (imageFiles.Count > 0)
                    {
                        result[dirName] = imageFiles;
                    }
                }

                Logger.Info("LabelManager", $"扫描完成，找到 {result.Values.Sum(list => list.Count)} 张图片，分布在 {result.Count} 个目录中");
            }
            catch (Exception ex)
            {
                Logger.Error("LabelManager", $"扫描图片时发生错误: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 获取图片的相对路径（相对于DIY_Expression目录）
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
            if (imageLabels.ContainsKey(relativePath))
            {
                return new List<string>(imageLabels[relativePath]);
            }
            return new List<string>();
        }

        /// <summary>
        /// 获取指定图片的心情标签
        /// </summary>
        public string GetImageEmotion(string relativePath)
        {
            var tags = GetImageTags(relativePath);
            var emotionTags = new[] { "happy", "normal", "poor", "ill" };
            var emotionTag = tags.FirstOrDefault(tag => emotionTags.Contains(tag.ToLower()));
            return emotionTag?.ToLower() ?? "general";
        }

        /// <summary>
        /// 获取指定图片的普通标签（排除心情标签）
        /// </summary>
        public List<string> GetImageNormalTags(string relativePath)
        {
            var tags = GetImageTags(relativePath);
            var emotionTags = new[] { "general", "happy", "normal", "poor", "ill" };
            return tags.Where(tag => !emotionTags.Contains(tag.ToLower())).ToList();
        }

        /// <summary>
        /// 设置指定图片的标签
        /// </summary>
        public void SetImageTags(string relativePath, List<string> tags)
        {
            // 清理标签：去除空白、去重、排序
            var cleanTags = tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct()
                .OrderBy(tag => tag)
                .ToList();

            if (cleanTags.Count > 0)
            {
                imageLabels[relativePath] = cleanTags;
            }
            else if (imageLabels.ContainsKey(relativePath))
            {
                imageLabels.Remove(relativePath);
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
                        imageLabels = loadedLabels;
                        Logger.Info("LabelManager", $"从文件加载了 {imageLabels.Count} 个图片的标签数据");
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
                imageLabels = new Dictionary<string, List<string>>();
            }
        }

        /// <summary>
        /// 保存标签数据到文件
        /// </summary>
        public void SaveLabels()
        {
            try
            {
                // 确保目录存在
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

                var json = JsonSerializer.Serialize(imageLabels, options);
                File.WriteAllText(labelFilePath, json);

                Logger.Info("LabelManager", $"保存了 {imageLabels.Count} 个图片的标签数据到文件: {labelFilePath}");
            }
            catch (Exception ex)
            {
                Logger.Error("LabelManager", $"保存标签文件时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取所有标签的统计信息
        /// </summary>
        public Dictionary<string, int> GetTagStatistics()
        {
            var tagCounts = new Dictionary<string, int>();

            foreach (var imageTags in imageLabels.Values)
            {
                foreach (var tag in imageTags)
                {
                    if (tagCounts.ContainsKey(tag))
                    {
                        tagCounts[tag]++;
                    }
                    else
                    {
                        tagCounts[tag] = 1;
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
            var tags = GetImageTags(relativePath);
            return tags.Contains("__llm_processed__");
        }

        /// <summary>
        /// 标记图片已被LLM处理
        /// </summary>
        public void MarkImageAsProcessedByLLM(string relativePath)
        {
            var tags = GetImageTags(relativePath);
            if (!tags.Contains("__llm_processed__"))
            {
                tags.Add("__llm_processed__");
                SetImageTags(relativePath, tags);
                Logger.Info("LabelManager", $"标记图片为已处理: {relativePath}");
            }
        }

        /// <summary>
        /// 创建空的label.json文件（如果不存在）
        /// </summary>
        public void CreateEmptyLabelFileIfNotExists()
        {
            try
            {
                if (!File.Exists(labelFilePath))
                {
                    var emptyLabels = new Dictionary<string, List<string>>();
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };

                    var json = JsonSerializer.Serialize(emptyLabels, options);
                    File.WriteAllText(labelFilePath, json);

                    Logger.Info("LabelManager", $"创建空的标签文件: {labelFilePath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("LabelManager", $"创建标签文件时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 创建示例目录结构（如果DIY_Expression为空）
        /// </summary>
        public void CreateExampleDirectories()
        {
            try
            {
                if (!Directory.Exists(diyExpressionPath))
                {
                    Directory.CreateDirectory(diyExpressionPath);
                }

                // 检查是否已有内容
                var existingDirs = Directory.GetDirectories(diyExpressionPath);
                var existingFiles = Directory.GetFiles(diyExpressionPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" }.Contains(Path.GetExtension(f).ToLower()))
                    .ToArray();

                if (existingDirs.Length > 0 || existingFiles.Length > 0)
                {
                    return; // 已有内容，不创建示例
                }

                // 只创建一个通用目录
                var generalDirPath = Path.Combine(diyExpressionPath, "General");
                if (!Directory.Exists(generalDirPath))
                {
                    Directory.CreateDirectory(generalDirPath);

                    // 创建说明文件
                    var readmePath = Path.Combine(generalDirPath, "README.txt");
                    var readmeContent = "通用表情包目录\n" +
                                      "请将表情包图片放在这里，支持格式：PNG、JPG、GIF、JPEG、BMP\n" +
                                      "可以通过标签管理面板为每张图片设置心情标签：\n" +
                                      "- happy: 开心\n" +
                                      "- normal: 正常\n" +
                                      "- poor: 状态不佳\n" +
                                      "- ill: 生病\n" +
                                      "- general: 泛用（默认）\n\n" +
                                      "如果图片没有设置心情标签，将作为泛用表情包使用。";

                    File.WriteAllText(readmePath, readmeContent);
                    Logger.Info("LabelManager", "创建了通用表情包目录: General");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("LabelManager", $"创建示例目录时发生错误: {ex.Message}");
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
        public string Directory { get; set; }
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