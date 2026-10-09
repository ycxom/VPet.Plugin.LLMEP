using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VPet.Plugin.LLMEP.Utils;
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

        public EmotionAnalyzer(ILLMClient llmClient, CacheManager cacheManager, IMainWindow mainWindow, ImageMgr imageMgr)
        {
            _llmClient = llmClient;
            _cacheManager = cacheManager;
            _mainWindow = mainWindow;
            _imageMgr = imageMgr;

            // 构建两种模式的候选标签提示词
            BuildImageTagsPrompt();
        }

        /// <summary>
        /// 表情包标签变了（标签管理面板保存、导入图片、AI 打标完成）之后重建候选标签。
        /// </summary>
        public void RefreshTagPrompts() => BuildImageTagsPrompt();

        /// <summary>
        /// 构建图片标签提示词。精确模式和传统模式用同一份候选标签：
        /// 内置表情包的 VPet_Expression\label.json（MOD 自带的只读数据）+ DIY 标签。
        ///
        /// 以前传统模式的候选来自 plugin\data\emotion_labels.json —— 那是个 0 字节空文件，
        /// 每次解析失败，传统模式发给模型的提示里根本没有候选标签，只能让它自由发挥，
        /// 返回的词和表情包标签对不上，向量匹配基本落空。
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
                    // DIY标签（旧格式 label.json 已在启动迁移时并入 diy_labels.json）
                    ExtractTagsFromDIYLabelFile(DiyStickerStorage.LabelsFilePath, allTags);
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

                    _emotionLabelsPrompt = $"\n\n可用的情感标签列表：{string.Join("、", tagList)}\n\n请从上述标签中选择1-3个最相关的标签。";

                    _imageMgr.LogDebug("EmotionAnalyzer", $"图片标签提示词已构建，包含 {allTags.Count} 个标签");
                }
                else
                {
                    _imageTagsPrompt = "";
                    _emotionLabelsPrompt = "";
                    _imageMgr.LogWarning("EmotionAnalyzer", "未找到可用的图片标签");
                }
            }
            catch (Exception ex)
            {
                _imageMgr.LogError("EmotionAnalyzer", $"构建图片标签提示词失败: {ex.Message}");
                _imageTagsPrompt = "";
                _emotionLabelsPrompt = "";
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
                                    // 过滤掉心情标签和保留标签（general、__llm_processed__），只保留普通标签用于LLM匹配。
                                    // 以前 AI 处理标记会混进给模型的候选标签里
                                    if (!LabelManager.IsMoodTag(label) && !LabelManager.IsReservedTag(label))
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
        /// 分析情感并返回匹配的图片（支持精确标签匹配）
        /// </summary>
        /// <param name="text">要分析的文本</param>
        /// <returns>匹配的图片，如果没有匹配则返回null</returns>
        public async Task<string> AnalyzeEmotionAndGetImagePathAsync(string text)
        {
            try
            {
                _imageMgr.LogDebug("EmotionAnalyzer", "=== AnalyzeEmotionAndGetImagePathAsync 开始 ===");
                _imageMgr.LogDebug("EmotionAnalyzer", $"接收到文本: {text}");
                _imageMgr.LogDebug("EmotionAnalyzer", $"精确匹配设置: {_imageMgr.Settings.UseAccurateImageMatching}");

                List<string> emotionTags = null;

                // 如果启用了精确图片匹配，使用标签匹配
                if (_imageMgr.Settings.UseAccurateImageMatching)
                {
                    _imageMgr.LogInfo("EmotionAnalyzer", "使用精确标签匹配模式");

                    Utils.Logger.Debug("EmotionAnalyzer", "准备调用 AnalyzeEmotionAsync...");
                    // 获取标签
                    emotionTags = await AnalyzeEmotionAsync(text);
                    Utils.Logger.Debug("EmotionAnalyzer", "AnalyzeEmotionAsync 调用完成");

                    if (emotionTags != null && emotionTags.Count > 0)
                    {
                        Utils.Logger.Info("EmotionAnalyzer", $"情感分析结果: {text} -> [{string.Join(", ", emotionTags)}]");

                        // 优先尝试在线表情包（如果启用且在情感分析中启用）
                        if (_imageMgr.Settings.OnlineSticker.IsEnabled && _imageMgr.Settings.OnlineSticker.EnableInEmotionAnalysis)
                        {
                            Utils.Logger.Debug("EmotionAnalyzer", "尝试使用在线表情包");
                            var onlineSuccess = await TryUseOnlineStickerAsync(emotionTags);
                            if (onlineSuccess)
                            {
                                Utils.Logger.Info("EmotionAnalyzer", "在线表情包显示成功");
                                return null; // 在线表情包已经显示，不需要返回本地图片
                            }
                            else
                            {
                                Utils.Logger.Debug("EmotionAnalyzer", "在线表情包显示失败，继续使用本地表情包");
                            }
                        }

                        // 使用标签匹配图片
                        var labelMatcher = _imageMgr.GetLabelImageMatcher();
                        if (labelMatcher != null)
                        {
                            var matchedImage = labelMatcher.MatchImagePathByTags(emotionTags);
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
                    emotionTags = await AnalyzeEmotionAsync(text);
                    Utils.Logger.Debug("EmotionAnalyzer", "AnalyzeEmotionAsync 调用完成");

                    if (emotionTags != null && emotionTags.Count > 0)
                    {
                        Utils.Logger.Info("EmotionAnalyzer", $"情感分析结果: {text} -> [{string.Join(", ", emotionTags)}]");

                        // 优先尝试在线表情包（如果启用且在情感分析中启用）
                        if (_imageMgr.Settings.OnlineSticker.IsEnabled && _imageMgr.Settings.OnlineSticker.EnableInEmotionAnalysis)
                        {
                            Utils.Logger.Debug("EmotionAnalyzer", "尝试使用在线表情包");
                            var onlineSuccess = await TryUseOnlineStickerAsync(emotionTags);
                            if (onlineSuccess)
                            {
                                Utils.Logger.Info("EmotionAnalyzer", "在线表情包显示成功");
                                return null; // 在线表情包已经显示，不需要返回本地图片
                            }
                            else
                            {
                                Utils.Logger.Debug("EmotionAnalyzer", "在线表情包显示失败，继续使用本地表情包");
                            }
                        }

                        // 使用向量匹配查找图片
                        var imageSelector = _imageMgr.GetImageSelector();
                        if (imageSelector != null)
                        {
                            Utils.Logger.Debug("EmotionAnalyzer", "开始向量匹配查找图片");

                            // 通过ImageSelector进行向量匹配（但不显示图片，只获取图片）
                            var matchedImage = await GetImageByVectorMatchingAsync(emotionTags);
                            if (matchedImage != null)
                            {
                                Utils.Logger.Debug("EmotionAnalyzer", "向量匹配成功，返回匹配的图片");
                                return matchedImage;
                            }

                            // 向量匹配落空（渠道没有嵌入接口，如 Free；或嵌入请求失败）时先按标签名匹配。
                            // 传统模式的提示词同样带着候选标签列表，模型返回的词能直接对上图片标签
                            matchedImage = _imageMgr.GetLabelImageMatcher()?.MatchImagePathByTags(emotionTags);
                            if (matchedImage != null)
                            {
                                Utils.Logger.Debug("EmotionAnalyzer", "向量匹配失败，标签匹配成功");
                                return matchedImage;
                            }
                            Utils.Logger.Debug("EmotionAnalyzer", "向量匹配和标签匹配都失败，使用降级方案");
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
                return _imageMgr.GetCurrentMoodImagePath();
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("EmotionAnalyzer", $"情感分析和图片匹配失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 尝试使用在线表情包
        /// </summary>
        private async Task<bool> TryUseOnlineStickerAsync(List<string> emotionTags)
        {
            try
            {
                if (emotionTags == null || emotionTags.Count == 0)
                {
                    return false;
                }

                var onlineStickerManager = _imageMgr.GetOnlineStickerManager();
                if (onlineStickerManager == null)
                {
                    Utils.Logger.Warning("EmotionAnalyzer", "在线表情包管理器未初始化");
                    return false;
                }

                // 使用第一个标签作为主要情感，其余作为附加标签
                string primaryEmotion = emotionTags[0];
                var additionalTags = emotionTags.Skip(1).ToList();

                Utils.Logger.Debug("EmotionAnalyzer", $"在线表情包搜索: 主要情感={primaryEmotion}, 附加标签=[{string.Join(", ", additionalTags)}]");

                // 搜索并显示在线表情包
                // 情感分析是自动触发：已有表情包在显示就不去打断它
                bool success = await onlineStickerManager.SearchAndDisplayStickerAsync(primaryEmotion, additionalTags, interrupt: false);

                if (success)
                {
                    Utils.Logger.Info("EmotionAnalyzer", $"在线表情包显示成功: {primaryEmotion}");
                }
                else
                {
                    Utils.Logger.Debug("EmotionAnalyzer", $"在线表情包未找到匹配结果: {primaryEmotion}");
                }

                return success;
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("EmotionAnalyzer", $"尝试使用在线表情包失败: {ex.Message}");
                return false;
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
                    Utils.Logger.Log($"[EmotionAnalyzer] Rate limited, using fallback");
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
        private async Task<string> GetImageByVectorMatchingAsync(List<string> emotions)
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
                        Utils.Logger.Debug("EmotionAnalyzer", $"向量匹配图片: {selectedFilename}");
                        return imagePath;
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

            // 读取DIY表情包标签（新旧两种格式都认，和 BuildImageTagsPrompt 给模型的候选一致；
            // 以前这里只读旧 label.json，标签管理面板打的标签会被当成无效标签丢掉）
            if (_imageMgr.Settings.EnableDIYImages)
            {
                ExtractTagsFromDIYLabelFile(DiyStickerStorage.LabelsFilePath, allTags);
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
