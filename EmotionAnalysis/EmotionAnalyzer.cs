using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.Image.EmotionAnalysis
{
    /// <summary>
    /// 情感分析器实现
    /// </summary>
    public class EmotionAnalyzer : IEmotionAnalyzer
    {
        private readonly ILLMClient _llmClient;
        private readonly CacheManager _cacheManager;
        private readonly IMainWindow _mainWindow;
        private DateTime _lastRequestTime = DateTime.MinValue;
        private const int MIN_REQUEST_INTERVAL_MS = 10000; // 10秒
        private string _emotionLabelsPrompt = "";

        public EmotionAnalyzer(ILLMClient llmClient, CacheManager cacheManager, IMainWindow mainWindow, string emotionLabelsPath = null)
        {
            _llmClient = llmClient;
            _cacheManager = cacheManager;
            _mainWindow = mainWindow;

            // 加载情感标签提示词
            if (!string.IsNullOrEmpty(emotionLabelsPath) && File.Exists(emotionLabelsPath))
            {
                LoadEmotionLabelsPrompt(emotionLabelsPath);
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

        public async Task<List<string>> AnalyzeEmotionAsync(string text)
        {
            try
            {
                // 检查缓存
                if (_cacheManager.TryGetEmotion(text, out var cachedEmotions))
                {
                    return cachedEmotions;
                }

                // 限流检查
                var timeSinceLastRequest = DateTime.Now - _lastRequestTime;
                if (timeSinceLastRequest.TotalMilliseconds < MIN_REQUEST_INTERVAL_MS)
                {
                    Console.WriteLine($"[EmotionAnalyzer] Rate limited, using fallback");
                    return GetFallbackEmotion();
                }

                // 调用LLM分析（附带标签提示）
                _lastRequestTime = DateTime.Now;
                var promptWithLabels = text + _emotionLabelsPrompt;
                var response = await _llmClient.SendRequestAsync(promptWithLabels);

                // 解析响应
                var emotions = ParseEmotions(response);

                if (emotions.Count == 0)
                {
                    emotions = GetFallbackEmotion();
                }

                // 缓存结果
                _cacheManager.CacheEmotion(text, emotions);

                Console.WriteLine($"[EmotionAnalyzer] Analyzed: {text} -> {string.Join(", ", emotions)}");
                return emotions;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EmotionAnalyzer] Error: {ex.Message}");
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

            return emotions;
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
}
