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
    /// Ollama 模型信息
    /// </summary>
    public class OllamaModelInfo : ModelInfo
    {
        /// <summary>
        /// 模型名称
        /// </summary>
        public new string Name
        {
            get => base.Name;
            set => base.Name = value;
        }

        /// <summary>
        /// 模型大小（人类可读格式）
        /// </summary>
        public string Size
        {
            get => base.Description;
            set => base.Description = value;
        }

        /// <summary>
        /// 模型参数大小（如 7B, 13B）
        /// </summary>
        public string ParameterSize { get; set; }

        /// <summary>
        /// 模型格式（如 gguf）
        /// </summary>
        public string Format { get; set; }

        /// <summary>
        /// 模型家族（如 llama, mistral）
        /// </summary>
        public string Family { get; set; }
    }

    /// <summary>
    /// Ollama本地API客户端实现
    /// </summary>
    public class OllamaClient : ILLMClient
    {
        private readonly string _baseUrl;
        private readonly string _model;
        private readonly HttpClient _httpClient;

        private readonly ImageMgr _imageMgr;

        public OllamaClient(string baseUrl = "http://localhost:11434", string model = "llama2", ImageMgr imageMgr = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _model = model;
            _imageMgr = imageMgr;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(60); // Ollama可能需要更长时间
        }

        public async Task<string> SendRequestAsync(string text)
        {
            try
            {
                // Ollama chat API endpoint
                var url = $"{_baseUrl}/api/generate";

                var requestBody = new
                {
                    model = _model,
                    prompt = $"你是一个情感分析助手。请从用户的句子中提取1-3个最相关的情感关键词。如果用户提供了可用标签列表，请从列表中选择；如果没有提供列表，请用中文返回情感关键词。只返回关键词，用逗号分隔，不要有其他解释。\n\n用户输入：{text}",
                    stream = false,
                    options = new
                    {
                        temperature = 0.3,
                        num_predict = 50
                    }
                };

                var jsonContent = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = true });
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // 记录完整的HTTP请求信息
                _imageMgr?.LogDebug("Ollama", "=== Ollama HTTP 请求开始 ===");
                _imageMgr?.LogDebug("Ollama", $"URL: {url}");
                _imageMgr?.LogDebug("Ollama", $"Method: POST");
                _imageMgr?.LogDebug("Ollama", $"Content-Type: application/json");
                _imageMgr?.LogDebug("Ollama", "请求体:");
                _imageMgr?.LogDebug("Ollama", jsonContent);
                _imageMgr?.LogDebug("Ollama", "=== Ollama HTTP 请求结束 ===");

                var response = await _httpClient.PostAsync(url, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                // 记录完整的HTTP响应信息
                _imageMgr?.LogDebug("Ollama", "=== Ollama HTTP 响应开始 ===");
                _imageMgr?.LogDebug("Ollama", $"状态码: {response.StatusCode}");
                _imageMgr?.LogDebug("Ollama", $"响应头:");
                foreach (var header in response.Headers)
                {
                    _imageMgr?.LogDebug("Ollama", $"  {header.Key}: {string.Join(", ", header.Value)}");
                }
                foreach (var header in response.Content.Headers)
                {
                    _imageMgr?.LogDebug("Ollama", $"  {header.Key}: {string.Join(", ", header.Value)}");
                }
                _imageMgr?.LogDebug("Ollama", $"响应体:");
                _imageMgr?.LogDebug("Ollama", responseContent);
                _imageMgr?.LogDebug("Ollama", "=== Ollama HTTP 响应结束 ===");

                if (!response.IsSuccessStatusCode)
                {
                    _imageMgr?.LogError("Ollama", $"API Error: {response.StatusCode} - {responseContent}");
                    throw new Exception($"Ollama API error: {response.StatusCode}");
                }

                // 解析Ollama响应
                using (JsonDocument doc = JsonDocument.Parse(responseContent))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("response", out var responseElement))
                    {
                        return responseElement.GetString() ?? "";
                    }
                }

                return "";
            }
            catch (Exception ex)
            {
                _imageMgr?.LogError("Ollama", $"Error: {ex.Message}");
                _imageMgr?.LogDebug("Ollama", $"StackTrace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// 获取本地可用的 Ollama 模型列表
        /// </summary>
        public async Task<List<OllamaModelInfo>> GetAvailableModelsAsync()
        {
            try
            {
                var url = $"{_baseUrl}/api/tags";

                Console.WriteLine($"[OllamaClient] 获取模型列表");
                Console.WriteLine($"[OllamaClient] URL: {url}");

                var response = await _httpClient.GetAsync(url);
                var responseContent = await response.Content.ReadAsStringAsync();

                Console.WriteLine($"[OllamaClient] 响应状态: {response.StatusCode}");
                Console.WriteLine($"[OllamaClient] 响应内容长度: {responseContent?.Length ?? 0}");

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[OllamaClient] 获取模型列表失败: {response.StatusCode}");
                    Console.WriteLine($"[OllamaClient] 错误响应: {responseContent}");
                    throw new Exception($"Ollama API error: {response.StatusCode} - {responseContent}");
                }

                // 打印部分响应内容用于调试
                var previewLength = Math.Min(responseContent?.Length ?? 0, 500);
                Console.WriteLine($"[OllamaClient] 响应内容预览: {responseContent?.Substring(0, previewLength)}...");

                var models = new List<OllamaModelInfo>();

                using (JsonDocument doc = JsonDocument.Parse(responseContent))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("models", out var modelsArray))
                    {
                        foreach (var model in modelsArray.EnumerateArray())
                        {
                            string name = null;
                            string size = null;
                            string parameterSize = null;
                            string format = null;
                            string family = null;

                            if (model.TryGetProperty("name", out var nameElement))
                            {
                                name = nameElement.GetString() ?? "";
                            }

                            // 获取模型大小
                            if (model.TryGetProperty("size", out var sizeElement))
                            {
                                var sizeBytes = sizeElement.GetInt64();
                                size = FormatBytes(sizeBytes);
                            }

                            // 获取详细信息
                            if (model.TryGetProperty("details", out var detailsElement))
                            {
                                if (detailsElement.TryGetProperty("parameter_size", out var paramSizeElement))
                                {
                                    parameterSize = paramSizeElement.GetString();
                                }
                                if (detailsElement.TryGetProperty("format", out var formatElement))
                                {
                                    format = formatElement.GetString();
                                }
                                if (detailsElement.TryGetProperty("family", out var familyElement))
                                {
                                    family = familyElement.GetString();
                                }
                            }

                            // 构建描述信息
                            var descriptionParts = new List<string>();
                            if (!string.IsNullOrEmpty(parameterSize))
                                descriptionParts.Add(parameterSize);
                            if (!string.IsNullOrEmpty(family))
                                descriptionParts.Add(family);
                            if (!string.IsNullOrEmpty(format))
                                descriptionParts.Add(format);

                            var description = string.Join(" | ", descriptionParts);
                            if (string.IsNullOrEmpty(description))
                            {
                                description = size;
                            }

                            if (!string.IsNullOrEmpty(name))
                            {
                                models.Add(new OllamaModelInfo
                                {
                                    Name = name,
                                    Size = size,
                                    ParameterSize = parameterSize,
                                    Format = format,
                                    Family = family,
                                    Description = description
                                });
                            }
                        }
                    }
                }

                Console.WriteLine($"[OllamaClient] 成功解析 {models.Count} 个模型");
                for (int i = 0; i < Math.Min(models.Count, 5); i++)
                {
                    Console.WriteLine($"[OllamaClient] 模型 {i + 1}: {models[i].Name} ({models[i].Size})");
                }
                if (models.Count > 5)
                {
                    Console.WriteLine($"[OllamaClient] ... 还有 {models.Count - 5} 个模型");
                }

                return models;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OllamaClient] GetModels Error: {ex.Message}");
                Console.WriteLine($"[OllamaClient] StackTrace: {ex.StackTrace}");
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
        /// 格式化字节大小为人类可读格式
        /// </summary>
        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            if (bytes == 0) return "0 B";
            int i = (int)Math.Floor(Math.Log(bytes) / Math.Log(1024));
            return $"{bytes / Math.Pow(1024, i):F1} {sizes[i]}";
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            try
            {
                // Ollama embeddings API endpoint
                var url = $"{_baseUrl}/api/embeddings";

                var requestBody = new
                {
                    model = _model,
                    prompt = text
                };

                var jsonContent = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(url, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[OllamaClient] Embedding API Error: {response.StatusCode} - {responseContent}");
                    throw new Exception($"Ollama Embedding API error: {response.StatusCode}");
                }

                // 解析嵌入向量
                using (JsonDocument doc = JsonDocument.Parse(responseContent))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("embedding", out var embedding))
                    {
                        var embeddingList = new List<float>();
                        foreach (var value in embedding.EnumerateArray())
                        {
                            embeddingList.Add((float)value.GetDouble());
                        }
                        return embeddingList.ToArray();
                    }
                }

                throw new Exception("Failed to parse embedding response");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OllamaClient] Embedding Error: {ex.Message}");
                throw;
            }
        }
    }
}
