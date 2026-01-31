using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace VPet.Plugin.LLMEP
{
    /// <summary>
    /// 标签图片匹配器 - 根据标签精确匹配表情包
    /// </summary>
    public class LabelImageMatcher
    {
        private readonly ImageMgr _imageMgr;
        private Dictionary<string, List<string>> _imageLabels; // filename -> labels
        private Dictionary<string, BitmapImage> _imageCache; // filename -> BitmapImage
        private Random _random;

        public LabelImageMatcher(ImageMgr imageMgr)
        {
            _imageMgr = imageMgr;
            _imageLabels = new Dictionary<string, List<string>>();
            _imageCache = new Dictionary<string, BitmapImage>();
            _random = new Random();
        }

        /// <summary>
        /// 加载标签文件
        /// </summary>
        public void LoadLabels()
        {
            try
            {
                _imageLabels.Clear();
                _imageCache.Clear();

                string dllPath = _imageMgr.LoaddllPath();
                
                // 加载内置表情包标签
                if (_imageMgr.Settings.EnableBuiltInImages)
                {
                    string builtInLabelPath = Path.Combine(dllPath, "VPet_Expression", "label.json");
                    string builtInImagePath = Path.Combine(dllPath, "VPet_Expression");
                    LoadLabelsFromFile(builtInLabelPath, builtInImagePath);
                }

                // 加载DIY表情包标签
                if (_imageMgr.Settings.EnableDIYImages)
                {
                    // 新的标签系统：从 plugin/data/diy_labels.json 加载
                    string diyLabelPath = Path.Combine(dllPath, "plugin", "data", "diy_labels.json");
                    string diyImagePath = Path.Combine(dllPath, "DIY_Expression");
                    LoadDIYLabelsFromFile(diyLabelPath, diyImagePath);
                    
                    // 兼容旧的标签系统：从 DIY_Expression/label.json 加载
                    string oldDiyLabelPath = Path.Combine(dllPath, "DIY_Expression", "label.json");
                    LoadLabelsFromFile(oldDiyLabelPath, diyImagePath);
                }

                _imageMgr.LogDebug("LabelMatcher", $"已加载 {_imageLabels.Count} 个图片的标签信息");
            }
            catch (Exception ex)
            {
                _imageMgr.LogMessage($"加载标签文件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从文件加载标签
        /// </summary>
        private void LoadLabelsFromFile(string labelFilePath, string imageBasePath)
        {
            try
            {
                if (!File.Exists(labelFilePath))
                {
                    _imageMgr.LogMessage($"标签文件不存在: {labelFilePath}");
                    return;
                }

                _imageMgr.LogDebug("LabelMatcher", $"开始读取标签文件: {labelFilePath}");
                string jsonContent = File.ReadAllText(labelFilePath);
                _imageMgr.LogDebug("LabelMatcher", $"标签文件内容长度: {jsonContent.Length} 字符");
                
                // 显示文件内容的前200个字符用于调试
                string preview = jsonContent.Length > 200 ? jsonContent.Substring(0, 200) + "..." : jsonContent;
                _imageMgr.LogDebug("LabelMatcher", $"标签文件内容预览: {preview}");

                _imageMgr.LogDebug("LabelMatcher", "开始反序列化 JSON...");
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                var labelData = JsonSerializer.Deserialize<LabelData>(jsonContent, options);
                _imageMgr.LogDebug("LabelMatcher", $"反序列化完成，labelData 是否为空: {labelData == null}");
                
                if (labelData != null)
                {
                    _imageMgr.LogDebug("LabelMatcher", $"labelData.Images 是否为空: {labelData.Images == null}");
                    if (labelData.Images != null)
                    {
                        _imageMgr.LogDebug("LabelMatcher", $"labelData.Images 数量: {labelData.Images.Count}");
                    }
                }

                if (labelData?.Images == null)
                {
                    _imageMgr.LogWarning("LabelMatcher", $"标签文件格式错误: {labelFilePath}");
                    return;
                }

                foreach (var imageInfo in labelData.Images)
                {
                    if (string.IsNullOrEmpty(imageInfo.Filename) || imageInfo.Labels == null)
                        continue;

                    // 存储标签信息
                    _imageLabels[imageInfo.Filename] = imageInfo.Labels.ToList();

                    // 预加载图片
                    string fullImagePath = Path.Combine(imageBasePath, imageInfo.Filename);
                    if (File.Exists(fullImagePath))
                    {
                        try
                        {
                            var bitmapImage = new BitmapImage();
                            bitmapImage.BeginInit();
                            bitmapImage.UriSource = new Uri(fullImagePath, UriKind.Absolute);
                            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                            bitmapImage.EndInit();

                            _imageCache[imageInfo.Filename] = bitmapImage;
                        }
                        catch (Exception ex)
                        {
                            _imageMgr.LogWarning("LabelMatcher", $"预加载图片失败: {fullImagePath}, 错误: {ex.Message}");
                        }
                    }
                }

                _imageMgr.LogDebug("LabelMatcher", $"从 {labelFilePath} 加载了 {labelData.Images.Count} 个图片标签");
            }
            catch (Exception ex)
            {
                _imageMgr.LogError("LabelMatcher", $"加载标签文件失败: {labelFilePath}, 错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 从DIY标签文件加载标签（新格式：相对路径 -> 标签列表）
        /// </summary>
        private void LoadDIYLabelsFromFile(string labelFilePath, string imageBasePath)
        {
            try
            {
                if (!File.Exists(labelFilePath))
                {
                    _imageMgr.LogMessage($"DIY标签文件不存在: {labelFilePath}");
                    return;
                }

                _imageMgr.LogDebug("LabelMatcher", $"开始读取DIY标签文件: {labelFilePath}");
                string jsonContent = File.ReadAllText(labelFilePath);
                _imageMgr.LogDebug("LabelMatcher", $"DIY标签文件内容长度: {jsonContent.Length} 字符");

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                // DIY标签格式：{ "相对路径": ["标签1", "标签2"] }
                var diyLabels = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(jsonContent, options);
                
                if (diyLabels == null)
                {
                    _imageMgr.LogWarning("LabelMatcher", $"DIY标签文件格式错误: {labelFilePath}");
                    return;
                }

                int loadedCount = 0;
                foreach (var kvp in diyLabels)
                {
                    string relativePath = kvp.Key;
                    List<string> tags = kvp.Value;

                    if (string.IsNullOrEmpty(relativePath) || tags == null || tags.Count == 0)
                        continue;

                    // 从相对路径获取文件名
                    string filename = Path.GetFileName(relativePath);
                    
                    // 构建完整图片路径
                    string fullImagePath = Path.Combine(imageBasePath, relativePath);
                    
                    if (File.Exists(fullImagePath))
                    {
                        // 存储标签信息（使用文件名作为键，保持与内置标签一致）
                        string cacheKey = $"diy_{filename}"; // 添加前缀避免与内置图片冲突
                        _imageLabels[cacheKey] = tags.ToList();

                        // 预加载图片
                        try
                        {
                            var bitmapImage = new BitmapImage();
                            bitmapImage.BeginInit();
                            bitmapImage.UriSource = new Uri(fullImagePath, UriKind.Absolute);
                            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                            bitmapImage.EndInit();

                            _imageCache[cacheKey] = bitmapImage;
                            loadedCount++;
                            
                            _imageMgr.LogDebug("LabelMatcher", $"加载DIY图片: {relativePath} -> 标签: [{string.Join(", ", tags)}]");
                        }
                        catch (Exception ex)
                        {
                            _imageMgr.LogWarning("LabelMatcher", $"预加载DIY图片失败: {fullImagePath}, 错误: {ex.Message}");
                        }
                    }
                    else
                    {
                        _imageMgr.LogWarning("LabelMatcher", $"DIY图片文件不存在: {fullImagePath}");
                    }
                }

                _imageMgr.LogInfo("LabelMatcher", $"从 {labelFilePath} 加载了 {loadedCount} 个DIY图片标签");
            }
            catch (Exception ex)
            {
                _imageMgr.LogError("LabelMatcher", $"加载DIY标签文件失败: {labelFilePath}, 错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据标签匹配图片
        /// </summary>
        /// <param name="targetTags">目标标签列表</param>
        /// <returns>匹配的图片，如果没有匹配则返回null</returns>
        public BitmapImage MatchImageByTags(List<string> targetTags)
        {
            try
            {
                if (targetTags == null || targetTags.Count == 0)
                {
                    _imageMgr.LogWarning("LabelMatcher", "目标标签为空");
                    return null;
                }

                _imageMgr.LogInfo("LabelMatcher", $"开始匹配标签 [{string.Join(", ", targetTags)}]");

                // 查找匹配的图片
                var matchedImages = new List<string>();

                foreach (var kvp in _imageLabels)
                {
                    string filename = kvp.Key;
                    List<string> imageLabels = kvp.Value;

                    // 检查是否有标签匹配
                    bool hasMatch = false;
                    foreach (string targetTag in targetTags)
                    {
                        foreach (string imageLabel in imageLabels)
                        {
                            // 完全匹配或包含匹配
                            if (string.Equals(targetTag, imageLabel, StringComparison.OrdinalIgnoreCase) ||
                                imageLabel.Contains(targetTag, StringComparison.OrdinalIgnoreCase) ||
                                targetTag.Contains(imageLabel, StringComparison.OrdinalIgnoreCase))
                            {
                                hasMatch = true;
                                break;
                            }
                        }
                        if (hasMatch) break;
                    }

                    if (hasMatch)
                    {
                        matchedImages.Add(filename);
                        _imageMgr.LogDebug("LabelMatcher", $"找到匹配图片 {filename}, 标签: [{string.Join(", ", imageLabels)}]");
                    }
                }

                if (matchedImages.Count == 0)
                {
                    _imageMgr.LogWarning("LabelMatcher", "未找到匹配的图片");
                    return null;
                }

                // 随机选择一个匹配的图片
                string selectedFilename = matchedImages[_random.Next(matchedImages.Count)];
                _imageMgr.LogInfo("LabelMatcher", $"从 {matchedImages.Count} 个匹配图片中随机选择: {selectedFilename}");

                // 返回图片
                if (_imageCache.TryGetValue(selectedFilename, out BitmapImage image))
                {
                    return image;
                }
                else
                {
                    _imageMgr.LogWarning("LabelMatcher", $"图片缓存中未找到 {selectedFilename}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _imageMgr.LogError("LabelMatcher", $"标签匹配失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取所有可用的标签
        /// </summary>
        public List<string> GetAllTags()
        {
            var allTags = new HashSet<string>();
            foreach (var labels in _imageLabels.Values)
            {
                foreach (var label in labels)
                {
                    allTags.Add(label);
                }
            }
            return allTags.ToList();
        }

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public string GetStatistics()
        {
            int imageCount = _imageLabels.Count;
            int tagCount = GetAllTags().Count;
            return $"图片数量: {imageCount}, 标签数量: {tagCount}";
        }
    }

    /// <summary>
    /// 标签数据结构
    /// </summary>
    public class LabelData
    {
        public List<ImageLabelInfo> Images { get; set; }
    }

    /// <summary>
    /// 图片标签信息
    /// </summary>
    public class ImageLabelInfo
    {
        public string Filename { get; set; }
        public string[] Labels { get; set; }
    }
}