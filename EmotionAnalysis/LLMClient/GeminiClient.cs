using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VPet.Plugin.Image.EmotionAnalysis.LLMClient
{
    /// <summary>
    /// Gemini 模型信息
    /// </summary>
    public class GeminiModelInfo : ModelInfo
    {
        /// <summary>
        /// 模型显示名称 (如 gemini-pro)
        /// </summary>
        public new string Name
        {
            get => base.Name;
            set => base.Name = value;
        }

        /// <summary>
        /// 模型完整名称 (如 models/gemini-pro)
        /// </summary>
        public string FullName
        {
            get => base.Id;
            set => base.Id = value;
        }
    }

    /// <summary>
    /// Google Gemini API客户端实现
    /// </summary>
    public class GeminiClient : ILLMClient
    {
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _model;
        private readonly string _embeddingModel;
        private readonly HttpClient _httpClient;
        private const string DEFAULT_MODEL = "gemini-pro";
        private const string DEFAULT_EMBEDDING_MODEL = "embedding-001";

        private readonly ImageMgr _imageMgr;

        public GeminiClient(string apiKey, string baseUrl = "https://generativelanguage.googleapis.com/v1beta", string model = null, string embeddingModel = null, ImageMgr imageMgr = null)
        {
            _apiKey = apiKey;
            _baseUrl = baseUrl.TrimEnd('/');
            _model = model ?? DEFAULT_MODEL;
            _embeddingModel = embeddingModel ?? DEFAULT_EMBEDDING_MODEL;
            _imageMgr = imageMgr;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        public async Task<string> SendRequestAsync(string text)
        {
            try
            {
                // Gemini API endpoint
                var url = $"{_baseUrl}/models/{_model}:generateContent?key={_apiKey}";

                var requestBody = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new[]
                            {
                                new
                                {
                                    text = $"你是一个情感分析助手。请从用户的句子中提取1-3个最相关的情感关键词。如果用户提供了可用标签列表，请从列表中选择；如果没有提供列表，请用中文返回情感关键词。只返回关键词，用逗号分隔，不要有其他解释。\n\n用户输入：{text}"
                                }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0.3,
                        maxOutputTokens = 50
                    }
                };

                var jsonContent = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = true });
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // 记录完整的HTTP请求信息
                _imageMgr?.LogDebug("Gemini", "=== Gemini HTTP 请求开始 ===");
                _imageMgr?.LogDebug("Gemini", $"URL: {url}");
                _imageMgr?.LogDebug("Gemini", $"Method: POST");
                _imageMgr?.LogDebug("Gemini", $"Content-Type: application/json");
                _imageMgr?.LogDebug("Gemini", "请求体:");
                _imageMgr?.LogDebug("Gemini", jsonContent);
                _imageMgr?.LogDebug("Gemini", "=== Gemini HTTP 请求结束 ===");

                var response = await _httpClient.PostAsync(url, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                // 记录完整的HTTP响应信息
                _imageMgr?.LogDebug("Gemini", "=== Gemini HTTP 响应开始 ===");
                _imageMgr?.LogDebug("Gemini", $"状态码: {response.StatusCode}");
                _imageMgr?.LogDebug("Gemini", $"响应头:");
                foreach (var header in response.Headers)
                {
                    _imageMgr?.LogDebug("Gemini", $"  {header.Key}: {string.Join(", ", header.Value)}");
                }
                foreach (var header in response.Content.Headers)
                {
                    _imageMgr?.LogDebug("Gemini", $"  {header.Key}: {string.Join(", ", header.Value)}");
                }
                _imageMgr?.LogDebug("Gemini", $"响应体:");
                _imageMgr?.LogDebug("Gemini", responseContent);
                _imageMgr?.LogDebug("Gemini", "=== Gemini HTTP 响应结束 ===");

                if (!response.IsSuccessStatusCode)
                {
                    _imageMgr?.LogError("Gemini", $"API Error: {response.StatusCode} - {responseContent}");
                    throw new Exception($"Gemini API error: {response.StatusCode}");
                }

                // 解析Gemini响应
                using (JsonDocument doc = JsonDocument.Parse(responseContent))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                    {
                        var firstCandidate = candidates[0];
                        if (firstCandidate.TryGetProperty("content", out var contentObj))
                        {
                            if (contentObj.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                            {
                                var firstPart = parts[0];
                                if (firstPart.TryGetProperty("text", out var textElement))
                                {
                                    return textElement.GetString() ?? "";
                                }
                            }
                        }
                    }
                }

                return "";
            }
            catch (Exception ex)
            {
                _imageMgr?.LogError("Gemini", $"Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 获取可用的 Gemini 模型列表
        /// </summary>
        public async Task<List<GeminiModelInfo>> GetAvailableModelsAsync()
        {
            try
            {
                var url = $"{_baseUrl}/models?key={_apiKey}";

                Console.WriteLine($"[GeminiClient] 获取模型列表");
                Console.WriteLine($"[GeminiClient] URL: {_baseUrl}/models?key=***");

                var response = await _httpClient.GetAsync(url);
                var responseContent = await response.Content.ReadAsStringAsync();

                Console.WriteLine($"[GeminiClient] 响应状态: {response.StatusCode}");
                Console.WriteLine($"[GeminiClient] 响应内容长度: {responseContent?.Length ?? 0}");

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[GeminiClient] 获取模型列表失败: {response.StatusCode}");
                    Console.WriteLine($"[GeminiClient] 错误响应: {responseContent}");
                    throw new Exception($"Gemini API error: {response.StatusCode} - {responseContent}");
                }

                // 打印部分响应内容用于调试
                var previewLength = Math.Min(responseContent?.Length ?? 0, 500);
                Console.WriteLine($"[GeminiClient] 响应内容预览: {responseContent?.Substring(0, previewLength)}...");

                var models = new List<GeminiModelInfo>();

                using (JsonDocument doc = JsonDocument.Parse(responseContent))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("models", out var modelsArray))
                    {
                        foreach (var model in modelsArray.EnumerateArray())
                        {
                            if (model.TryGetProperty("name", out var nameElement))
                            {
                                var name = nameElement.GetString() ?? "";
                                // 只返回支持 generateContent 的模型
                                if (name.Contains("gemini") && !name.Contains("embedding"))
                                {
                                    var displayName = name.Replace("models/", "");

                                    // 尝试获取描述
                                    string description = null;
                                    if (model.TryGetProperty("description", out var descElement))
                                    {
                                        description = descElement.GetString();
                                    }
                                    if (model.TryGetProperty("version", out var versionElement))
                                    {
                                        var version = versionElement.GetString();
                                        if (!string.IsNullOrEmpty(version))
                                        {
                                            description = string.IsNullOrEmpty(description)
                                                ? $"Version: {version}"
                                                : $"{description} (Version: {version})";
                                        }
                                    }

                                    models.Add(new GeminiModelInfo
                                    {
                                        Name = displayName,
                                        FullName = name,
                                        Description = description
                                    });
                                }
                            }
                        }
                    }
                }

                Console.WriteLine($"[GeminiClient] 成功解析 {models.Count} 个模型");
                for (int i = 0; i < Math.Min(models.Count, 5); i++)
                {
                    Console.WriteLine($"[GeminiClient] 模型 {i + 1}: {models[i].Name}");
                }
                if (models.Count > 5)
                {
                    Console.WriteLine($"[GeminiClient] ... 还有 {models.Count - 5} 个模型");
                }

                return models;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GeminiClient] GetModels Error: {ex.Message}");
                Console.WriteLine($"[GeminiClient] StackTrace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// 显式接口实现 - 返回基类ModelInfo列表
        /// </summary>
        async Task<List<ModelInfo>> ILLMClient.GetAvailableModelsAsync()
        {
            var models = await GetAvailableModelsAsync();
            return models.Cast<ModelInfo>().ToList();
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            try
            {
                // Gemini Embedding API endpoint
                var url = $"{_baseUrl}/models/{_embeddingModel}:embedContent?key={_apiKey}";

                var requestBody = new
                {
                    content = new
                    {
                        parts = new[]
                        {
                            new { text = text }
                        }
                    }
                };

                var jsonContent = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(url, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[GeminiClient] Embedding API Error: {response.StatusCode} - {responseContent}");
                    throw new Exception($"Gemini Embedding API error: {response.StatusCode}");
                }

                // 解析嵌入向量
                using (JsonDocument doc = JsonDocument.Parse(responseContent))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("embedding", out var embedding))
                    {
                        if (embedding.TryGetProperty("values", out var values))
                        {
                            var embeddingList = new List<float>();
                            foreach (var value in values.EnumerateArray())
                            {
                                embeddingList.Add((float)value.GetDouble());
                            }
                            return embeddingList.ToArray();
                        }
                    }
                }

                throw new Exception("Failed to parse embedding response");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GeminiClient] Embedding Error: {ex.Message}");
                throw;
            }
        }
    }
}
