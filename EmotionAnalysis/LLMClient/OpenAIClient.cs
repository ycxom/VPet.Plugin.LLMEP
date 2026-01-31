using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VPet.Plugin.LLMEP.EmotionAnalysis.LLMClient
{
    /// <summary>
    /// OpenAI 模型信息
    /// </summary>
    public class OpenAIModelInfo : ModelInfo
    {
        /// <summary>
        /// 模型ID
        /// </summary>
        public string Id
        {
            get => base.Id;
            set => base.Id = value;
        }

        /// <summary>
        /// 模型名称
        /// </summary>
        public new string Name
        {
            get => base.Name;
            set => base.Name = value;
        }
    }

    /// <summary>
    /// OpenAI客户端实现 - 支持OpenAI、OpenRouter、OneAPI等兼容OpenAI API的端点
    /// </summary>
    public class OpenAIClient : ILLMClient
    {
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _model;
        private readonly string _embeddingModel;
        private readonly HttpClient _httpClient;

        private readonly ImageMgr _imageMgr;

        public OpenAIClient(string apiKey, string baseUrl = "https://api.openai.com/v1", string model = "gpt-3.5-turbo", string embeddingModel = "text-embedding-3-small", ImageMgr imageMgr = null)
        {
            _apiKey = apiKey;
            _baseUrl = baseUrl.TrimEnd('/');
            _model = model;
            _embeddingModel = embeddingModel;
            _imageMgr = imageMgr;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        public async Task<string> SendRequestAsync(string prompt)
        {
            try
            {
                var requestBody = new
                {
                    model = _model,
                    messages = new[]
                    {
                        new { role = "system", content = "你是一个情感分析助手。请从用户的句子中提取1-3个最相关的情感关键词。如果用户提供了可用标签列表，请从列表中选择；如果没有提供列表，请用中文返回情感关键词。只返回关键词，用逗号分隔，不要有其他解释。" },
                        new { role = "user", content = prompt }
                    },
                    temperature = 0.3,
                    max_tokens = 50
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = true });
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // 记录完整的HTTP请求信息
                _imageMgr?.LogDebug("OpenAI", "=== OpenAI HTTP 请求开始 ===");
                _imageMgr?.LogDebug("OpenAI", $"URL: {_baseUrl}/chat/completions");
                _imageMgr?.LogDebug("OpenAI", $"Method: POST");
                _imageMgr?.LogDebug("OpenAI", $"Content-Type: application/json");
                _imageMgr?.LogDebug("OpenAI", $"Authorization: Bearer {(_apiKey?.Length > 10 ? _apiKey.Substring(0, 10) + "..." : _apiKey)}");
                _imageMgr?.LogDebug("OpenAI", "请求体:");
                _imageMgr?.LogDebug("OpenAI", json);
                _imageMgr?.LogDebug("OpenAI", "=== OpenAI HTTP 请求结束 ===");

                var response = await _httpClient.PostAsync($"{_baseUrl}/chat/completions", content);
                
                var responseJson = await response.Content.ReadAsStringAsync();
                
                // 记录完整的HTTP响应信息
                _imageMgr?.LogDebug("OpenAI", "=== OpenAI HTTP 响应开始 ===");
                _imageMgr?.LogDebug("OpenAI", $"状态码: {response.StatusCode}");
                _imageMgr?.LogDebug("OpenAI", $"响应头:");
                foreach (var header in response.Headers)
                {
                    _imageMgr?.LogDebug("OpenAI", $"  {header.Key}: {string.Join(", ", header.Value)}");
                }
                foreach (var header in response.Content.Headers)
                {
                    _imageMgr?.LogDebug("OpenAI", $"  {header.Key}: {string.Join(", ", header.Value)}");
                }
                _imageMgr?.LogDebug("OpenAI", $"响应体:");
                _imageMgr?.LogDebug("OpenAI", responseJson);
                _imageMgr?.LogDebug("OpenAI", "=== OpenAI HTTP 响应结束 ===");

                response.EnsureSuccessStatusCode();

                var responseObj = JsonSerializer.Deserialize<JsonElement>(responseJson);

                var messageContent = responseObj
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                return messageContent?.Trim() ?? "";
            }
            catch (Exception ex)
            {
                _imageMgr?.LogError("OpenAI", $"Error: {ex.Message}");
                _imageMgr?.LogDebug("OpenAI", $"StackTrace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// 获取可用的 OpenAI 模型列表 - 支持OpenRouter、OneAPI等端点
        /// </summary>
        public async Task<List<OpenAIModelInfo>> GetAvailableModelsAsync()
        {
            try
            {
                // 构建模型列表URL - 参考VPetLLM的实现逻辑
                // 确保URL格式正确，处理各种输入情况
                string modelsUrl = BuildModelsUrl(_baseUrl);

                Console.WriteLine($"[OpenAIClient] 获取模型列表");
                Console.WriteLine($"[OpenAIClient] URL: {modelsUrl}");
                Console.WriteLine($"[OpenAIClient] API Key: {(_apiKey?.Length > 0 ? $"***{_apiKey.Substring(Math.Max(0, _apiKey.Length - 4))}" : "未设置")}");

                var response = await _httpClient.GetAsync(modelsUrl);

                // 读取响应内容
                var responseContent = await response.Content.ReadAsStringAsync();

                Console.WriteLine($"[OpenAIClient] 响应状态: {response.StatusCode}");
                Console.WriteLine($"[OpenAIClient] 响应内容长度: {responseContent?.Length ?? 0}");

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[OpenAIClient] 获取模型列表失败: {response.StatusCode}");
                    Console.WriteLine($"[OpenAIClient] 错误响应: {responseContent}");
                    throw new Exception($"API返回错误: {response.StatusCode} - {responseContent}");
                }

                // 打印部分响应内容用于调试
                var previewLength = Math.Min(responseContent?.Length ?? 0, 500);
                Console.WriteLine($"[OpenAIClient] 响应内容预览: {responseContent?.Substring(0, previewLength)}...");

                var responseObj = JsonSerializer.Deserialize<JsonElement>(responseContent);

                var models = new List<OpenAIModelInfo>();

                // 尝试不同的响应格式
                // 标准 OpenAI 格式: { "data": [{ "id": "..." }] }
                // OneAPI/OpenRouter 可能使用: { "data": [{ "id": "...", "name": "..." }] }
                // 或者: [{ "id": "..." }] (直接数组)

                if (responseObj.ValueKind == JsonValueKind.Array)
                {
                    // 直接数组格式
                    foreach (var model in responseObj.EnumerateArray())
                    {
                        ExtractModelInfo(model, models);
                    }
                }
                else if (responseObj.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
                {
                    // 标准 data 包装格式
                    foreach (var model in dataArray.EnumerateArray())
                    {
                        ExtractModelInfo(model, models);
                    }
                }
                else if (responseObj.TryGetProperty("models", out var modelsArray) && modelsArray.ValueKind == JsonValueKind.Array)
                {
                    // 某些 API 使用 "models" 字段
                    foreach (var model in modelsArray.EnumerateArray())
                    {
                        ExtractModelInfo(model, models);
                    }
                }
                else
                {
                    Console.WriteLine($"[OpenAIClient] 警告: 无法识别的响应格式");
                    Console.WriteLine($"[OpenAIClient] 响应类型: {responseObj.ValueKind}");
                }

                Console.WriteLine($"[OpenAIClient] 成功解析 {models.Count} 个模型");

                // 打印前5个模型用于调试
                for (int i = 0; i < Math.Min(models.Count, 5); i++)
                {
                    Console.WriteLine($"[OpenAIClient] 模型 {i + 1}: {models[i].Id} ({models[i].Name})");
                }
                if (models.Count > 5)
                {
                    Console.WriteLine($"[OpenAIClient] ... 还有 {models.Count - 5} 个模型");
                }

                return models;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OpenAIClient] GetModels Error: {ex.Message}");
                Console.WriteLine($"[OpenAIClient] StackTrace: {ex.StackTrace}");
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

        /// <summary>
        /// 从 JSON 元素提取模型信息
        /// </summary>
        private void ExtractModelInfo(JsonElement modelElement, List<OpenAIModelInfo> models)
        {
            string id = null;
            string name = null;
            string description = null;

            // 调试：打印原始JSON元素
            if (models.Count < 3) // 只打印前几个避免日志过多
            {
                Console.WriteLine($"[OpenAIClient] 解析模型元素: {modelElement}");
            }

            // 尝试获取 id
            if (modelElement.TryGetProperty("id", out var idProp))
            {
                id = idProp.GetString();
                if (models.Count < 3)
                {
                    Console.WriteLine($"[OpenAIClient] 找到 id 属性: {id}");
                }
            }
            else if (modelElement.TryGetProperty("name", out var nameProp))
            {
                // 某些 API 使用 name 作为标识
                id = nameProp.GetString();
                if (models.Count < 3)
                {
                    Console.WriteLine($"[OpenAIClient] 找到 name 属性作为 id: {id}");
                }
            }
            else
            {
                if (models.Count < 3)
                {
                    Console.WriteLine($"[OpenAIClient] 警告: 未找到 id 或 name 属性");
                }
            }

            // 尝试获取显示名称
            if (modelElement.TryGetProperty("name", out var displayNameProp) && displayNameProp.ValueKind == JsonValueKind.String)
            {
                name = displayNameProp.GetString();
            }
            else if (modelElement.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String)
            {
                description = descProp.GetString();
                if (string.IsNullOrEmpty(name))
                {
                    name = description;
                }
            }

            // OpenRouter 特殊字段处理
            if (modelElement.TryGetProperty("description", out var openRouterDesc))
            {
                description = openRouterDesc.GetString();
            }

            if (!string.IsNullOrEmpty(id))
            {
                // 过滤掉非聊天模型
                var lowerId = id.ToLowerInvariant();
                var isChat = IsChatModel(lowerId);

                if (models.Count < 3)
                {
                    Console.WriteLine($"[OpenAIClient] 模型 {id}: IsChatModel={isChat}");
                }
                if (IsChatModel(lowerId))
                {
                    models.Add(new OpenAIModelInfo
                    {
                        Id = id,
                        Name = name ?? id,
                        Description = description
                    });
                }
            }
        }

        /// <summary>
        /// 构建模型列表URL - 与VPetLLM保持一致
        /// </summary>
        private string BuildModelsUrl(string baseUrl)
        {
            // 标准化 baseUrl
            var url = baseUrl.TrimEnd('/');

            // 如果URL包含 /chat/completions，替换为 /models
            if (url.Contains("/chat/completions"))
            {
                return url.Replace("/chat/completions", "/models");
            }

            // 如果URL以 /v1 结尾，直接添加 /models
            if (url.EndsWith("/v1"))
            {
                return url + "/models";
            }

            // 如果URL已经包含 /v1/，检查是否已经包含 /models
            if (url.Contains("/v1/"))
            {
                if (!url.EndsWith("/models"))
                {
                    // 找到 /v1/ 的位置，在其后添加 models
                    var v1Index = url.LastIndexOf("/v1/");
                    if (v1Index >= 0)
                    {
                        var basePart = url.Substring(0, v1Index + 3); // 包含 /v1
                        return basePart + "/models";
                    }
                }
                return url;
            }

            // 默认情况：添加 /v1/models
            return url + "/v1/models";
        }

        /// <summary>
        /// 判断是否为聊天模型（过滤嵌入、语音、图像等模型）
        /// </summary>
        private bool IsChatModel(string modelId)
        {
            // 排除的模型类型关键词
            string[] excludedKeywords = new[]
            {
                "embedding", "embed",
                "whisper",
                "tts", "text-to-speech",
                "dall-e", "dalle",
                "moderation",
                "transcribe", "translate",
                "image", "audio",
                "instruct" // 某些instruct模型可能不适合聊天
            };

            foreach (var keyword in excludedKeywords)
            {
                if (modelId.Contains(keyword))
                    return false;
            }

            // 包含这些关键词的通常是聊天模型
            string[] chatKeywords = new[]
            {
                "gpt", "claude", "llama", "mistral", "mixtral",
                "qwen", "glm", "moonshot", "kimi", "baichuan",
                "chat", "turbo", "preview"
            };

            // 如果模型ID包含任何聊天关键词，认为是聊天模型
            foreach (var keyword in chatKeywords)
            {
                if (modelId.Contains(keyword))
                    return true;
            }

            // 默认返回true，因为无法确定时应该让用户自己选择
            return true;
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            try
            {
                var requestBody = new
                {
                    model = _embeddingModel,
                    input = text
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_baseUrl}/embeddings", content);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<JsonElement>(responseJson);

                var embeddingArray = responseObj
                    .GetProperty("data")[0]
                    .GetProperty("embedding");

                var embedding = new List<float>();
                foreach (var element in embeddingArray.EnumerateArray())
                {
                    embedding.Add((float)element.GetDouble());
                }

                return embedding.ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OpenAI] Embedding error: {ex.Message}");
                throw;
            }
        }
    }
}
