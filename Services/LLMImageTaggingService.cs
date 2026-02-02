using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using VPet.Plugin.LLMEP.EmotionAnalysis;
using VPet.Plugin.LLMEP.EmotionAnalysis.LLMClient;

namespace VPet.Plugin.LLMEP.Services
{
    /// <summary>
    /// AI图片标签生成服务
    /// 使用支持Vision的LLM模型分析图片并生成标签和心情分类
    /// </summary>
    public class LLMImageTaggingService
    {
        private readonly ImageMgr _imageMgr;
        private readonly LabelManager _labelManager;
        private readonly string _pluginDirectory;
        private bool _isProcessing;
        private CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// 处理进度事件
        /// </summary>
        public event EventHandler<ImageTaggingProgressEventArgs> ProgressChanged;

        /// <summary>
        /// 处理完成事件
        /// </summary>
        public event EventHandler<ImageTaggingCompletedEventArgs> ProcessingCompleted;

        public bool IsProcessing => _isProcessing;

        public LLMImageTaggingService(ImageMgr imageMgr, LabelManager labelManager, string pluginDirectory)
        {
            _imageMgr = imageMgr;
            _labelManager = labelManager;
            _pluginDirectory = pluginDirectory;
        }

        /// <summary>
        /// 开始AI标签生成处理
        /// </summary>
        public async Task StartProcessingAsync(ImageSettings settings)
        {
            if (_isProcessing)
            {
                Utils.Logger.Warning("LLMImageTagging", "处理已在进行中");
                return;
            }

            if (!settings.EnableAIImageTagging)
            {
                Utils.Logger.Warning("LLMImageTagging", "AI标签生成功能未启用");
                return;
            }

            if (!settings.EmotionAnalysis.IsVisionModel)
            {
                Utils.Logger.Warning("LLMImageTagging", "未启用视觉模型支持");
                return;
            }

            _isProcessing = true;
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                Utils.Logger.Info("LLMImageTagging", "开始AI图片标签生成处理");

                // 扫描所有图片
                var images = _labelManager.ScanImages();
                var allImages = images.Values.SelectMany(list => list).ToList();

                // 过滤出未处理的图片
                var unprocessedImages = allImages.Where(img => !IsImageProcessedByLLM(img.RelativePath)).ToList();

                Utils.Logger.Info("LLMImageTagging", $"总共 {allImages.Count} 张图片，未处理 {unprocessedImages.Count} 张");

                int processedCount = 0;
                int failedCount = 0;

                foreach (var image in unprocessedImages)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        Utils.Logger.Info("LLMImageTagging", "处理已取消");
                        break;
                    }

                    try
                    {
                        OnProgressChanged(new ImageTaggingProgressEventArgs
                        {
                            CurrentImage = image.FileName,
                            CurrentIndex = processedCount + failedCount + 1,
                            TotalCount = unprocessedImages.Count,
                            Status = $"正在处理: {image.FileName}"
                        });

                        // 分析图片
                        var result = await AnalyzeImageAsync(image, settings);

                        if (result != null)
                        {
                            // 保存标签
                            var tags = new List<string>(result.Tags);
                            if (!string.IsNullOrEmpty(result.Emotion) && result.Emotion != "general")
                            {
                                tags.Add(result.Emotion);
                            }

                            _labelManager.SetImageTags(image.RelativePath, tags);

                            // 标记为已处理
                            MarkImageAsProcessedByLLM(image.RelativePath);

                            processedCount++;

                            Utils.Logger.Info("LLMImageTagging", $"成功处理图片: {image.FileName}, 标签: [{string.Join(", ", tags)}]");
                        }
                        else
                        {
                            failedCount++;
                            Utils.Logger.Warning("LLMImageTagging", $"处理图片失败: {image.FileName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        Utils.Logger.Error("LLMImageTagging", $"处理图片异常: {image.FileName}, 错误: {ex.Message}");
                    }

                    // 间隔3秒后处理下一张
                    if (processedCount + failedCount < unprocessedImages.Count)
                    {
                        await Task.Delay(3000, _cancellationTokenSource.Token);
                    }
                }

                // 保存所有标签
                _labelManager.SaveLabels();

                Utils.Logger.Info("LLMImageTagging", $"处理完成: 成功 {processedCount} 张, 失败 {failedCount} 张");

                OnProcessingCompleted(new ImageTaggingCompletedEventArgs
                {
                    SuccessCount = processedCount,
                    FailedCount = failedCount,
                    TotalCount = unprocessedImages.Count
                });
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LLMImageTagging", $"处理过程发生错误: {ex.Message}");
            }
            finally
            {
                _isProcessing = false;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        /// <summary>
        /// 停止处理
        /// </summary>
        public void StopProcessing()
        {
            _cancellationTokenSource?.Cancel();
        }

        /// <summary>
        /// 分析单张图片
        /// </summary>
        private async Task<ImageAnalysisResult> AnalyzeImageAsync(ImageInfo image, ImageSettings settings)
        {
            try
            {
                // 读取图片并转换为base64
                string base64Image = await ConvertImageToBase64Async(image.FullPath);
                if (string.IsNullOrEmpty(base64Image))
                {
                    return null;
                }

                // 创建LLM客户端
                var llmClient = CreateLLMClient(settings);
                if (llmClient == null)
                {
                    Utils.Logger.Error("LLMImageTagging", "无法创建LLM客户端");
                    return null;
                }

                // 构建提示词
                string prompt = BuildAnalysisPrompt();

                // 发送请求
                string response = await SendVisionRequestAsync(llmClient, prompt, base64Image, image.FullPath, settings);

                if (string.IsNullOrEmpty(response))
                {
                    return null;
                }

                // 解析响应
                return ParseAnalysisResponse(response);
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LLMImageTagging", $"分析图片失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 构建分析提示词
        /// </summary>
        private string BuildAnalysisPrompt()
        {
            return @"请分析这张表情包图片，完成以下任务：

1. 生成3-5个描述图片内容的标签（用逗号分隔）
2. 判断图片对应的心情分类，从以下选项中选择一项：
   - happy: 开心、愉快、兴奋的表情
   - normal: 平静、正常的表情
   - poor: 疲惫、沮丧、状态不佳的表情
   - ill: 生病、难受的表情
   - general: 泛用、无法明确分类的表情

请严格按照以下JSON格式返回结果：
{
  ""tags"": [""标签1"", ""标签2"", ""标签3""],
  ""emotion"": ""happy""
}

只返回JSON，不要有任何其他文字。";
        }

        /// <summary>
        /// 创建LLM客户端
        /// </summary>
        private ILLMClient CreateLLMClient(ImageSettings settings)
        {
            try
            {
                var emotionSettings = settings.EmotionAnalysis;

                return emotionSettings.Provider switch
                {
                    LLMProvider.OpenAI => new OpenAIClient(
                        emotionSettings.OpenAIApiKey,
                        emotionSettings.OpenAIBaseUrl,
                        emotionSettings.OpenAIModel,
                        emotionSettings.OpenAIEmbeddingModel,
                        _imageMgr),
                    LLMProvider.Gemini => new GeminiClient(
                        emotionSettings.GeminiApiKey,
                        emotionSettings.GeminiBaseUrl,
                        emotionSettings.GeminiModel,
                        emotionSettings.GeminiEmbeddingModel,
                        _imageMgr),
                    LLMProvider.Ollama => new OllamaClient(
                        emotionSettings.OllamaBaseUrl,
                        emotionSettings.OllamaModel,
                        _imageMgr),
                    LLMProvider.Free => new FreeClient(emotionSettings.FreeModel, _imageMgr),
                    _ => null
                };
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LLMImageTagging", $"创建LLM客户端失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 发送视觉模型请求
        /// </summary>
        private async Task<string> SendVisionRequestAsync(ILLMClient client, string prompt, string base64Image, string imagePath, ImageSettings settings)
        {
            try
            {
                // 根据提供商类型使用不同的处理方式
                var provider = settings.EmotionAnalysis.Provider;

                if (client is OpenAIClient openAiClient)
                {
                    return await SendOpenAIVisionRequestAsync(openAiClient, prompt, base64Image, settings);
                }
                else if (client is GeminiClient geminiClient)
                {
                    return await SendGeminiVisionRequestAsync(geminiClient, prompt, base64Image, settings);
                }
                else if (client is FreeClient freeClient)
                {
                    return await SendFreeVisionRequestAsync(freeClient, prompt, base64Image);
                }
                else
                {
                    // 其他客户端暂不支持视觉功能，使用普通文本请求作为降级
                    Utils.Logger.Warning("LLMImageTagging", "当前提供商不支持视觉功能，跳过处理");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LLMImageTagging", $"发送视觉请求失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 发送OpenAI视觉请求
        /// </summary>
        private async Task<string> SendOpenAIVisionRequestAsync(OpenAIClient client, string prompt, string base64Image, ImageSettings settings)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {settings.EmotionAnalysis.OpenAIApiKey}");

                string url = $"{settings.EmotionAnalysis.OpenAIBaseUrl.TrimEnd('/')}/chat/completions";

                var requestBody = new
                {
                    model = settings.EmotionAnalysis.OpenAIModel,
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "text", text = prompt },
                                new
                                {
                                    type = "image_url",
                                    image_url = new
                                    {
                                        url = $"data:image/jpeg;base64,{base64Image}"
                                    }
                                }
                            }
                        }
                    },
                    temperature = 0.3,
                    max_tokens = 2048
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                Utils.Logger.Debug("LLMImageTagging", $"发送OpenAI视觉请求到: {url}");

                var response = await httpClient.PostAsync(url, content);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Utils.Logger.Error("LLMImageTagging", $"OpenAI请求失败: {response.StatusCode}, {responseJson}");
                    return null;
                }

                var responseObj = JsonSerializer.Deserialize<JsonElement>(responseJson);
                var messageContent = responseObj
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                return messageContent?.Trim();
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LLMImageTagging", $"OpenAI视觉请求失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 发送Gemini视觉请求
        /// </summary>
        private async Task<string> SendGeminiVisionRequestAsync(GeminiClient client, string prompt, string base64Image, ImageSettings settings)
        {
            try
            {
                using var httpClient = new HttpClient();

                string url = $"{settings.EmotionAnalysis.GeminiBaseUrl.TrimEnd('/')}/models/{settings.EmotionAnalysis.GeminiModel}:generateContent?key={settings.EmotionAnalysis.GeminiApiKey}";

                var requestBody = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new object[]
                            {
                                new { text = prompt },
                                new
                                {
                                    inline_data = new
                                    {
                                        mime_type = "image/jpeg",
                                        data = base64Image
                                    }
                                }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0.3,
                        maxOutputTokens = 2048
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                Utils.Logger.Debug("LLMImageTagging", $"发送Gemini视觉请求");

                var response = await httpClient.PostAsync(url, content);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Utils.Logger.Error("LLMImageTagging", $"Gemini请求失败: {response.StatusCode}, {responseJson}");
                    return null;
                }

                var responseObj = JsonSerializer.Deserialize<JsonElement>(responseJson);
                var text = responseObj
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();

                return text?.Trim();
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LLMImageTagging", $"Gemini视觉请求失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 发送Free视觉请求
        /// </summary>
        private async Task<string> SendFreeVisionRequestAsync(FreeClient client, string prompt, string base64Image)
        {
            try
            {
                Utils.Logger.Debug("LLMImageTagging", "发送Free视觉请求");
                string response = await client.SendVisionRequestAsync(prompt, base64Image);
                return response;
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LLMImageTagging", $"Free视觉请求失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析分析响应
        /// </summary>
        private ImageAnalysisResult ParseAnalysisResponse(string response)
        {
            try
            {
                // 尝试提取JSON部分
                string jsonContent = response;

                // 如果响应包含markdown代码块，提取其中的JSON
                if (response.Contains("```json"))
                {
                    var start = response.IndexOf("```json") + 7;
                    var end = response.LastIndexOf("```");
                    if (end > start)
                    {
                        jsonContent = response.Substring(start, end - start).Trim();
                    }
                    else
                    {
                        // 代码块未闭合，提取剩余部分
                        jsonContent = response.Substring(start).Trim();
                    }
                }
                else if (response.Contains("```"))
                {
                    var start = response.IndexOf("```") + 3;
                    var end = response.LastIndexOf("```");
                    if (end > start)
                    {
                        jsonContent = response.Substring(start, end - start).Trim();
                    }
                    else
                    {
                        // 代码块未闭合，提取剩余部分
                        jsonContent = response.Substring(start).Trim();
                    }
                }

                // 尝试修复不完整的JSON
                jsonContent = jsonContent.Trim();
                
                // 如果JSON不完整（缺少结尾），尝试修复
                if (jsonContent.StartsWith("{"))
                {
                    // 检查并修复不完整的JSON
                    int openBraces = jsonContent.Count(c => c == '{');
                    int closeBraces = jsonContent.Count(c => c == '}');
                    
                    // 添加缺失的闭括号
                    while (closeBraces < openBraces)
                    {
                        jsonContent += "}";
                        closeBraces++;
                    }

                    // 尝试解析JSON
                    try
                    {
                        return ParseJsonContent(jsonContent);
                    }
                    catch (JsonException)
                    {
                        // 如果解析失败，尝试提取部分信息
                        return TryExtractPartialInfo(jsonContent);
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LLMImageTagging", $"解析响应失败: {ex.Message}, 响应: {response}");
                return null;
            }
        }

        /// <summary>
        /// 解析JSON内容
        /// </summary>
        private ImageAnalysisResult ParseJsonContent(string jsonContent)
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            var result = new ImageAnalysisResult();

            // 解析tags
            if (root.TryGetProperty("tags", out var tagsElement))
            {
                if (tagsElement.ValueKind == JsonValueKind.Array)
                {
                    result.Tags = tagsElement.EnumerateArray()
                        .Select(t => t.GetString())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();
                }
                else if (tagsElement.ValueKind == JsonValueKind.String)
                {
                    // 处理逗号分隔的字符串
                    var tagsStr = tagsElement.GetString();
                    result.Tags = tagsStr.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();
                }
            }

            // 解析emotion
            if (root.TryGetProperty("emotion", out var emotionElement))
            {
                result.Emotion = emotionElement.GetString()?.ToLower();
            }

            // 验证emotion是否有效
            var validEmotions = new[] { "happy", "normal", "poor", "ill", "general" };
            if (!validEmotions.Contains(result.Emotion))
            {
                result.Emotion = "general";
            }

            return result;
        }

        /// <summary>
        /// 尝试从部分/损坏的JSON中提取信息
        /// </summary>
        private ImageAnalysisResult TryExtractPartialInfo(string jsonContent)
        {
            try
            {
                var result = new ImageAnalysisResult();
                
                // 尝试提取tags数组 - 使用正则表达式匹配 "tags": [...]
                var tagsMatch = Regex.Match(jsonContent, "\"tags\"\\s*:\\s*\\[([^\\]]*)\\]");
                if (tagsMatch.Success)
                {
                    var tagsContent = tagsMatch.Groups[1].Value;
                    // 提取引号内的字符串 - 匹配 "text"
                    var tagMatches = Regex.Matches(tagsContent, "\"([^\"]+)\"");
                    foreach (Match match in tagMatches)
                    {
                        var tag = match.Groups[1].Value.Trim();
                        if (!string.IsNullOrEmpty(tag))
                        {
                            result.Tags.Add(tag);
                        }
                    }
                }

                // 尝试提取emotion - 匹配 "emotion": "value"
                var emotionMatch = Regex.Match(jsonContent, "\"emotion\"\\s*:\\s*\"([^\"]+)\"");
                if (emotionMatch.Success)
                {
                    result.Emotion = emotionMatch.Groups[1].Value.Trim().ToLower();
                }

                // 验证emotion是否有效
                var validEmotions = new[] { "happy", "normal", "poor", "ill", "general" };
                if (!validEmotions.Contains(result.Emotion))
                {
                    result.Emotion = "general";
                }

                // 如果提取到了标签或emotion，返回结果
                if (result.Tags.Count > 0 || !string.IsNullOrEmpty(result.Emotion))
                {
                    Utils.Logger.Debug("LLMImageTagging", "从部分JSON中提取到信息: tags=[" + string.Join(", ", result.Tags) + "], emotion=" + result.Emotion);
                    return result;
                }

                return null;
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LLMImageTagging", $"提取部分信息失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 将图片转换为Base64
        /// </summary>
        private async Task<string> ConvertImageToBase64Async(string imagePath)
        {
            try
            {
                using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
                using var ms = new MemoryStream();
                await fs.CopyToAsync(ms);
                byte[] imageBytes = ms.ToArray();
                return Convert.ToBase64String(imageBytes);
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LLMImageTagging", $"转换图片为Base64失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 检查图片是否已被LLM处理过
        /// </summary>
        private bool IsImageProcessedByLLM(string relativePath)
        {
            return _labelManager.IsImageProcessedByLLM(relativePath);
        }

        /// <summary>
        /// 标记图片已被LLM处理
        /// </summary>
        private void MarkImageAsProcessedByLLM(string relativePath)
        {
            _labelManager.MarkImageAsProcessedByLLM(relativePath);
        }

        /// <summary>
        /// 触发进度改变事件
        /// </summary>
        private void OnProgressChanged(ImageTaggingProgressEventArgs e)
        {
            ProgressChanged?.Invoke(this, e);
        }

        /// <summary>
        /// 触发处理完成事件
        /// </summary>
        private void OnProcessingCompleted(ImageTaggingCompletedEventArgs e)
        {
            ProcessingCompleted?.Invoke(this, e);
        }
    }

    /// <summary>
    /// 图片分析结果
    /// </summary>
    public class ImageAnalysisResult
    {
        public List<string> Tags { get; set; } = new List<string>();
        public string Emotion { get; set; } = "general";
    }

    /// <summary>
    /// 图片标签生成进度事件参数
    /// </summary>
    public class ImageTaggingProgressEventArgs : EventArgs
    {
        public string CurrentImage { get; set; }
        public int CurrentIndex { get; set; }
        public int TotalCount { get; set; }
        public string Status { get; set; }
    }

    /// <summary>
    /// 图片标签生成完成事件参数
    /// </summary>
    public class ImageTaggingCompletedEventArgs : EventArgs
    {
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public int TotalCount { get; set; }
    }
}
