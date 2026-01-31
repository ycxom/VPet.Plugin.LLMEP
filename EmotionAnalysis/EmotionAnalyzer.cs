using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.LLMEP.EmotionAnalysis
{
    /// <summary>
    /// 情感分析器实现
    /// </summary>
    public class EmotionAnalyzer : IEmotionAnalyzer
    {
        private readonly ILLMClient _llmClient;
        private readonly CacheManager _cacheManager;
        private readonly IMainWindow _mainWindow;
        private readonly ImageMgr _imageMgr;
        private DateTime _lastRequestTime = DateTime.MinValue;
        private const int MIN_REQUEST_INTERVAL_MS = 10000; // 10秒
        private string _emotionLabelsPrompt = "";
        private string _imageTagsPrompt = "";

        public EmotionAnalyzer(ILLMClient llmClient, CacheManager cacheManager, IMainWindow mainWindow, ImageMgr imageMgr, string emotionLabelsPath = null)
        {
            _llmClient = llmClient;
            _cacheManager = cacheManager;
            _mainWindow = mainWindow;
            _imageMgr = imageMgr;

            // 加载情感标签提示词
            if (!string.IsNullOrEmpty(emotionLabelsPath) && File.Exists(emotionLabelsPath))
            {
                LoadEmotionLabelsPrompt(emotionLabelsPath);
            }

            // 构建图片标签提示词
            BuildImageTagsPrompt();
        }

        /// <summary>
        /// 构建图片标签提示词
        /// </summary>
        private void BuildImageTagsPrompt()
        {
            try
            {
                // 从label.json中提取所有可用的标签
                var allTags = new HashSet<string>();
                string dllPath = _imageMgr.LoaddllPath();

                // 读取内置表情包标签
                if (_imageMgr.Settings.EnableBuiltInImages)
                {
                    string builtInLabelPath = Path.Combine(dllPath, "VPet_Expression", "label.json");
                    ExtractTagsFromLabelFile(builtInLabelPath, allTags);
                }

                // 读取DIY表情包标签
                if (_imageMgr.Settings.EnableDIYImages)
                {
                    // 新的DIY标签系统
                    string diyLabelPath = Path.Combine(dllPath, "plugin", "data", "diy_labels.json");
                    ExtractTagsFromDIYLabelFile(diyLabelPath, allTags);
                    
                    // 兼容旧的DIY标签系统
                    string oldDiyLabelPath = Path.Combine(dllPath, "DIY_Expression", "label.json");
                    ExtractTagsFromLabelFile(oldDiyLabelPath, allTags);
                }

                if (allTags.Count > 0)
                {
                    var tagList = allTags.ToList();
                    tagList.Sort(); // 排序便于阅读

                    _imageTagsPrompt = $@"

请根据以下文本内容，从给定的标签列表中选择1-3个最相关的标签，用于匹配合适的表情包图片。

【重要】只能从以下标签列表中选择，不允许使用列表外的任何词汇：
{string.Join("、", tagList)}

严格要求：
1. 必须且只能从上述标签列表中选择
2. 不允许使用任何不在列表中的词汇
3. 多个标签用逗号分隔，不要空格
4. 只返回标签，不要任何解释或其他文字
5. 如果文本内容与标签列表完全不匹配，返回可爱

正确示例：睡觉,可爱
正确示例：开心,激动
正确示例：疑惑

文本内容：";

                    _imageMgr.LogDebug("EmotionAnalyzer", $"图片标签提示词已构建，包含 {allTags.Count} 个标签");
                }
                else
                {
                    _imageTagsPrompt = "";
                    _imageMgr.LogWarning("EmotionAnalyzer", "未找到可用的图片标签");
                }
            }
            catch (Exception ex)
            {
                _imageMgr.LogError("EmotionAnalyzer", $"构建图片标签提示词失败: {ex.Message}");
                _imageTagsPrompt = "";
            }
        }

        /// <summary>
        /// 从标签文件中提取标签
        /// </summary>
        private void ExtractTagsFromLabelFile(string labelFilePath, HashSet<string> allTags)
        {
            try
            {
                if (!File.Exists(labelFilePath))
                    return;

                string jsonContent = File.ReadAllText(labelFilePath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                var labelData = JsonSerializer.Deserialize<LabelData>(jsonContent, options);

                if (labelData?.Images != null)
                {
                    foreach (var imageInfo in labelData.Images)
                    {
                        if (imageInfo.Labels != null)
                        {
                            foreach (var label in imageInfo.Labels)
                            {
                                if (!string.IsNullOrWhiteSpace(label))
                                {
                                    allTags.Add(label.Trim());
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _imageMgr.LogError("EmotionAnalyzer", $"提取标签失败: {labelFilePath}, 错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 从DIY标签文件中提取标签（新格式：相对路径 -> 标签列表）
        /// </summary>
        private void ExtractTagsFromDIYLabelFile(string labelFilePath, HashSet<string> allTags)
        {
            try
            {
                if (!File.Exists(labelFilePath))
                {
                    _imageMgr.LogDebug("EmotionAnalyzer", $"DIY标签文件不存在: {labelFilePath}");
                    return;
                }

                string jsonContent = File.ReadAllText(labelFilePath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                // DIY标签格式：{ "相对路径": ["标签1", "标签2"] }
                var diyLabels = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(jsonContent, options);

                if (diyLabels != null)
                {
                    int tagCount = 0;
                    foreach (var kvp in diyLabels)
                    {
                        if (kvp.Value != null)
                        {
                            foreach (var label in kvp.Value)
                            {
                                if (!string.IsNullOrWhiteSpace(label))
                                {
                                    // 过滤掉心情标签，只保留普通标签用于LLM匹配
                                    var emotionTags = new[] { "general", "happy", "normal", "poor", "ill" };
                                    if (!emotionTags.Contains(label.ToLower()))
                                    {
                                        allTags.Add(label.Trim());
                                        tagCount++;
                                    }
                                }
                            }
                        }
                    }
                    _imageMgr.LogDebug("EmotionAnalyzer", $"从DIY标签文件提取了 {tagCount} 个标签");
                }
            }
            catch (Exception ex)
            {
                _imageMgr.LogError("EmotionAnalyzer", $"提取DIY标签失败: {labelFilePath}, 错误: {ex.Message}");
            }
        }
        /// <summary>
        /// 加载情感标签提示词
        /// </summary>
        private void LoadEmotionLabelsPrompt(string path)
        {
            try
            {
                var jsonContent = File.ReadAllText(path);
                using (JsonDocument doc = JsonDocument.Parse(jsonContent))
                {
                    var root = doc.RootElement;

                    // 提取所有标签
                    var allLabels = new List<string>();
                    if (root.TryGetProperty("categories", out var categories))
                    {
                        foreach (var category in categories.EnumerateObject())
                        {
                            if (category.Value.ValueKind == JsonValueKind.Array)
                            {
                                // 处理角色名称这种直接数组
                                foreach (var label in category.Value.EnumerateArray())
                                {
                                    allLabels.Add(label.GetString());
                                }
                            }
                            else if (category.Value.ValueKind == JsonValueKind.Object)
                            {
                                // 处理嵌套的子分类
                                foreach (var subCategory in category.Value.EnumerateObject())
                                {
                                    if (subCategory.Value.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var label in subCategory.Value.EnumerateArray())
                                        {
                                            allLabels.Add(label.GetString());
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // 构建提示词
                    if (allLabels.Count > 0)
                    {
                        _emotionLabelsPrompt = $"\n\n可用的情感标签列表：{string.Join("、", allLabels)}\n\n请从上述标签中选择1-3个最相关的标签。";
                        Console.WriteLine($"[EmotionAnalyzer] 已加载 {allLabels.Count} 个情感标签");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EmotionAnalyzer] 加载情感标签失败: {ex.Message}");
                _emotionLabelsPrompt = "";
            }
        }

        /// <summary>
        /// 分析情感并返回匹配的图片（支持精确标签匹配）
        /// </summary>
        /// <param name="text">要分析的文本</param>
        /// <returns>匹配的图片，如果没有匹配则返回null</returns>
        public async Task<BitmapImage> AnalyzeEmotionAndGetImageAsync(string text)
        {
            try
            {
                _imageMgr.LogDebug("EmotionAnalyzer", "=== AnalyzeEmotionAndGetImageAsync 开始 ===");
                _imageMgr.LogDebug("EmotionAnalyzer", $"接收到文本: {text}");
                _imageMgr.LogDebug("EmotionAnalyzer", $"精确匹配设置: {_imageMgr.Settings.UseAccurateImageMatching}");
                
                // 如果启用了精确图片匹配，使用标签匹配
                if (_imageMgr.Settings.UseAccurateImageMatching)
                {
                    _imageMgr.LogInfo("EmotionAnalyzer", "使用精确标签匹配模式");
                    
                    Utils.Logger.Debug("EmotionAnalyzer", "准备调用 AnalyzeEmotionAsync...");
                    // 获取标签
                    var tags = await AnalyzeEmotionAsync(text);
                    Utils.Logger.Debug("EmotionAnalyzer", "AnalyzeEmotionAsync 调用完成");
                    
                    if (tags != null && tags.Count > 0)
                    {
                        Utils.Logger.Info("EmotionAnalyzer", $"情感分析结果: {text} -> [{string.Join(", ", tags)}]");
                        
                        // 使用标签匹配图片
                        var labelMatcher = _imageMgr.GetLabelImageMatcher();
                        if (labelMatcher != null)
                        {
                            var matchedImage = labelMatcher.MatchImageByTags(tags);
                            if (matchedImage != null)
                            {
                                Utils.Logger.Debug("EmotionAnalyzer", "标签匹配成功，返回匹配的图片");
                                return matchedImage;
                            }
                            else
                            {
                                Utils.Logger.Debug("EmotionAnalyzer", "标签匹配失败，使用降级方案");
                            }
                        }
                        else
                        {
                            Utils.Logger.Warning("EmotionAnalyzer", "标签匹配器未初始化，使用降级方案");
                        }
                    }
                    else
                    {
                        Utils.Logger.Debug("EmotionAnalyzer", "未获得有效标签，使用降级方案");
                    }
                }
                else
                {
                    Utils.Logger.Debug("EmotionAnalyzer", "精确标签匹配未启用，使用向量匹配模式");
                    
                    // 使用LLM分析获取情感标签
                    Utils.Logger.Debug("EmotionAnalyzer", "准备调用 AnalyzeEmotionAsync...");
                    var emotions = await AnalyzeEmotionAsync(text);
                    Utils.Logger.Debug("EmotionAnalyzer", "AnalyzeEmotionAsync 调用完成");
                    
                    if (emotions != null && emotions.Count > 0)
                    {
                        Utils.Logger.Info("EmotionAnalyzer", $"情感分析结果: {text} -> [{string.Join(", ", emotions)}]");
                        
                        // 使用向量匹配查找图片
                        var imageSelector = _imageMgr.GetImageSelector();
                        if (imageSelector != null)
                        {
                            Utils.Logger.Debug("EmotionAnalyzer", "开始向量匹配查找图片");
                            
                            // 通过ImageSelector进行向量匹配（但不显示图片，只获取图片）
                            var matchedImage = await GetImageByVectorMatchingAsync(emotions);
                            if (matchedImage != null)
                            {
                                Utils.Logger.Debug("EmotionAnalyzer", "向量匹配成功，返回匹配的图片");
                                return matchedImage;
                            }
                            else
                            {
                                Utils.Logger.Debug("EmotionAnalyzer", "向量匹配失败，使用降级方案");
                            }
                        }
                        else
                        {
                            Utils.Logger.Warning("EmotionAnalyzer", "ImageSelector未初始化，使用降级方案");
                        }
                    }
                    else
                    {
                        Utils.Logger.Debug("EmotionAnalyzer", "未获得有效情感标签，使用降级方案");
                    }
                }

                // 降级方案：使用传统的心情匹配
                var currentMode = _mainWindow.Core.Save.CalMode();
                Utils.Logger.Debug("EmotionAnalyzer", $"使用传统心情匹配，当前心情: {currentMode}");
                return _imageMgr.GetCurrentMoodImagePublic();
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("EmotionAnalyzer", $"情感分析和图片匹配失败: {ex.Message}");
                return null;
            }
        }

        public async Task<List<string>> AnalyzeEmotionAsync(string text)
        {
            try
            {
                Utils.Logger.Debug("EmotionAnalyzer", "=== AnalyzeEmotionAsync 开始 ===");
                Utils.Logger.Debug("EmotionAnalyzer", $"接收到文本: {text}");
                
                // 检查版本并在需要时清空缓存（在分析前执行，给用户反悔时间）
                var currentVersion = _imageMgr.Settings.AccurateMatchingVersion;
                _cacheManager.CheckVersionAndClearIfNeeded(currentVersion);
                
                Utils.Logger.Debug("EmotionAnalyzer", "检查缓存...");
                
                // 检查缓存（无论是否启用精确匹配模式）
                if (_cacheManager.TryGetEmotion(text, out var cachedEmotions))
                {
                    Utils.Logger.Debug("EmotionAnalyzer", $"找到缓存结果: [{string.Join(", ", cachedEmotions)}]");
                    return cachedEmotions;
                }
                Utils.Logger.Debug("EmotionAnalyzer", "缓存中未找到结果，继续进行新的分析");

                // 限流检查
                Utils.Logger.Debug("EmotionAnalyzer", "检查限流...");
                var timeSinceLastRequest = DateTime.Now - _lastRequestTime;
                if (timeSinceLastRequest.TotalMilliseconds < MIN_REQUEST_INTERVAL_MS)
                {
                    Utils.Logger.Debug("EmotionAnalyzer", $"触发限流，距离上次请求 {timeSinceLastRequest.TotalMilliseconds}ms");
                    Console.WriteLine($"[EmotionAnalyzer] Rate limited, using fallback");
                    return GetFallbackEmotion();
                }

                // 根据设置选择不同的prompt
                string promptToUse;
                if (_imageMgr.Settings.UseAccurateImageMatching && !string.IsNullOrEmpty(_imageTagsPrompt))
                {
                    // 使用精确图片标签匹配模式
                    promptToUse = _imageTagsPrompt + text;
                    _imageMgr.LogInfo("EmotionAnalyzer", "使用精确图片标签匹配模式");
                }
                else
                {
                    // 使用传统情感分析模式
                    promptToUse = text + _emotionLabelsPrompt;
                    _imageMgr.LogInfo("EmotionAnalyzer", "使用传统情感分析模式");
                }

                // 记录发送给 LLM 的完整 prompt
                _imageMgr.LogDebug("EmotionAnalyzer", "=== LLM 请求内容开始 ===");
                _imageMgr.LogDebug("EmotionAnalyzer", $"Prompt 长度: {promptToUse.Length} 字符");
                _imageMgr.LogDebug("EmotionAnalyzer", "Prompt 内容:");
                _imageMgr.LogDebug("EmotionAnalyzer", promptToUse);
                _imageMgr.LogDebug("EmotionAnalyzer", "=== LLM 请求内容结束 ===");

                // 调用LLM分析
                _lastRequestTime = DateTime.Now;
                var response = await _llmClient.SendRequestAsync(promptToUse);

                // 记录 LLM 响应
                _imageMgr.LogDebug("EmotionAnalyzer", "=== LLM 响应内容开始 ===");
                _imageMgr.LogDebug("EmotionAnalyzer", $"响应长度: {response?.Length ?? 0} 字符");
                _imageMgr.LogDebug("EmotionAnalyzer", $"响应内容: {response ?? "null"}");
                _imageMgr.LogDebug("EmotionAnalyzer", "=== LLM 响应内容结束 ===");

                // 解析响应
                var emotions = ParseEmotions(response);

                if (emotions.Count == 0)
                {
                    emotions = GetFallbackEmotion();
                }

                // 缓存结果
                _cacheManager.CacheEmotion(text, emotions);

                _imageMgr.LogInfo("EmotionAnalyzer", $"情感分析结果: {text} -> [{string.Join(", ", emotions)}]");
                return emotions;
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("EmotionAnalyzer", $"情感分析失败: {ex.Message}");
                return GetFallbackEmotion();
            }
        }

        /// <summary>
        /// 解析LLM响应中的情感关键词
        /// </summary>
        private List<string> ParseEmotions(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return new List<string>();

            // 分割逗号分隔的关键词（支持中文和英文逗号）
            var emotions = response
                .Split(new[] { ',', ';', '\n', '，', '、' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Take(3) // 最多3个关键词
                .ToList();

            // 如果启用了精确匹配模式，验证标签是否在允许列表中
            if (_imageMgr.Settings.UseAccurateImageMatching)
            {
                var validTags = GetValidTags();
                var validEmotions = new List<string>();
                
                foreach (var emotion in emotions)
                {
                    if (validTags.Contains(emotion, StringComparer.OrdinalIgnoreCase))
                    {
                        validEmotions.Add(emotion);
                        Utils.Logger.Debug("EmotionAnalyzer", $"标签验证: '{emotion}' 是有效标签");
                    }
                    else
                    {
                        Utils.Logger.Debug("EmotionAnalyzer", $"标签验证: '{emotion}' 不在允许列表中，已忽略");
                    }
                }
                
                if (validEmotions.Count == 0)
                {
                    Utils.Logger.Debug("EmotionAnalyzer", "标签验证: 没有有效标签，使用降级标签 '可爱'");
                    validEmotions.Add("可爱");
                }
                
                return validEmotions;
            }

            return emotions;
        }

        /// <summary>
        /// 通过向量匹配获取图片
        /// </summary>
        private async Task<BitmapImage> GetImageByVectorMatchingAsync(List<string> emotions)
        {
            try
            {
                var vectorRetriever = _imageMgr.GetVectorRetriever();
                if (vectorRetriever == null)
                {
                    Utils.Logger.Warning("EmotionAnalyzer", "向量检索器未初始化");
                    return null;
                }

                // 使用向量检索获取匹配的图片文件名
                var matchingImages = await vectorRetriever.FindMatchingImagesAsync(emotions, topK: 3);
                
                if (matchingImages != null && matchingImages.Count > 0)
                {
                    Utils.Logger.Debug("EmotionAnalyzer", $"向量匹配找到 {matchingImages.Count} 张候选图片");
                    
                    // 随机选择一张
                    var random = new Random();
                    string selectedFilename = matchingImages[random.Next(matchingImages.Count)];
                    Utils.Logger.Debug("EmotionAnalyzer", $"随机选择图片: {selectedFilename}");
                    
                    // 加载图片
                    var imagePath = _imageMgr.GetImagePath(selectedFilename);
                    if (!string.IsNullOrEmpty(imagePath))
                    {
                        var image = _imageMgr.LoadImageFromPath(imagePath);
                        if (image != null)
                        {
                            Utils.Logger.Debug("EmotionAnalyzer", $"向量匹配图片加载成功: {selectedFilename}");
                            return image;
                        }
                        else
                        {
                            Utils.Logger.Warning("EmotionAnalyzer", $"向量匹配图片加载失败: {selectedFilename}");
                        }
                    }
                    else
                    {
                        Utils.Logger.Warning("EmotionAnalyzer", $"未找到图片路径: {selectedFilename}");
                    }
                }
                else
                {
                    Utils.Logger.Debug("EmotionAnalyzer", "向量匹配未找到候选图片");
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("EmotionAnalyzer", $"向量匹配过程失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取所有有效的标签列表
        /// </summary>
        private HashSet<string> GetValidTags()
        {
            var allTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string dllPath = _imageMgr.LoaddllPath();

            // 读取内置表情包标签
            if (_imageMgr.Settings.EnableBuiltInImages)
            {
                string builtInLabelPath = Path.Combine(dllPath, "VPet_Expression", "label.json");
                ExtractTagsFromLabelFile(builtInLabelPath, allTags);
            }

            // 读取DIY表情包标签
            if (_imageMgr.Settings.EnableDIYImages)
            {
                string diyLabelPath = Path.Combine(dllPath, "DIY_Expression", "label.json");
                ExtractTagsFromLabelFile(diyLabelPath, allTags);
            }

            return allTags;
        }

        /// <summary>
        /// 获取降级情感（基于VPet当前心情）
        /// </summary>
        private List<string> GetFallbackEmotion()
        {
            try
            {
                var currentMode = _mainWindow.Core.Save.CalMode();

                return currentMode switch
                {
                    IGameSave.ModeType.Happy => new List<string> { "开心", "激动" },
                    IGameSave.ModeType.Nomal => new List<string> { "平静", "正常" },
                    IGameSave.ModeType.PoorCondition => new List<string> { "疲惫", "沮丧" },
                    IGameSave.ModeType.Ill => new List<string> { "生病", "难受" },
                    _ => new List<string> { "平静" }
                };
            }
            catch
            {
                return new List<string> { "平静" };
            }
        }
    }

    /// <summary>
    /// 标签数据结构（用于解析label.json）
    /// </summary>
    public class LabelData
    {
        public List<ImageLabelInfo> Images { get; set; }
    }

    /// <summary>
    /// 图片标签信息（用于解析label.json）
    /// </summary>
    public class ImageLabelInfo
    {
        public string Filename { get; set; }
        public string[] Labels { get; set; }
    }
}
