using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VPet.Plugin.LLMEP.EmotionAnalysis.LLMClient
{
    /// <summary>
    /// Free模型信息
    /// </summary>
    public class FreeModelInfo : ModelInfo
    {
        /// <summary>
        /// 模型ID
        /// </summary>
        public new string Id
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
    /// Free客户端实现 - 使用VPetLLM的Free配置
    /// </summary>
    public class FreeClient : ILLMClient
    {
        private readonly string _model;
        private readonly HttpClient _httpClient;
        private readonly ImageMgr _imageMgr;

        private string _apiKey;
        private string _apiUrl;

        // 保留硬编码的User-Agent（与VPetLLM保持一致）
        private const string ENCODED_UA = "566c426c6445784d54563947636d566c58304a3558304a5a54513d3d";

        public FreeClient(string model = "gpt-3.5-turbo", ImageMgr imageMgr = null)
        {
            _model = model;
            _imageMgr = imageMgr;

            LoadConfig();

            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);

            // 设置API密钥
            if (!string.IsNullOrEmpty(_apiKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            }

            // 设置解码后的User-Agent头部
            var decodedUA = DecodeString(ENCODED_UA);
            if (!string.IsNullOrEmpty(decodedUA))
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(decodedUA);
            }
        }

        private void LoadConfig()
        {
            try
            {
                // 首先尝试初始化配置（如果配置不存在）
                InitializeConfigIfNeeded();

                var config = GetChatConfig();
                if (config is not null)
                {
                    _apiKey = DecodeString(config["API_KEY"]?.ToString() ?? "");
                    _apiUrl = DecodeString(config["API_URL"]?.ToString() ?? "");
                    _imageMgr?.LogDebug("FreeClient", "Free配置加载成功");
                }
                else
                {
                    _imageMgr?.LogWarning("FreeClient", "Free配置文件不存在，请等待配置下载完成后重启程序");
                    _apiKey = "";
                    _apiUrl = "";
                }
            }
            catch (Exception ex)
            {
                _imageMgr?.LogError("FreeClient", $"加载Free配置失败: {ex.Message}");
                _apiKey = "";
                _apiUrl = "";
            }
        }

        /// <summary>
        /// 如果配置不存在，尝试初始化配置
        /// </summary>
        private void InitializeConfigIfNeeded()
        {
            try
            {
                var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var configDirectory = Path.Combine(documentsPath, "VPetLLM", "FreeConfig");

                if (!Directory.Exists(configDirectory))
                {
                    _imageMgr?.LogInfo("FreeClient", "VPetLLM配置目录不存在，创建目录");
                    Directory.CreateDirectory(configDirectory);
                }

                // 检查是否已有配置文件
                var hasConfig = HasValidChatConfig(configDirectory);
                if (!hasConfig)
                {
                    _imageMgr?.LogInfo("FreeClient", "未找到有效的Free配置，尝试初始化配置...");
                    _imageMgr?.LogInfo("FreeClient", "正在后台下载Free配置文件，请稍候...");

                    // 异步初始化配置，但不等待完成（避免阻塞UI）
                    Task.Run(async () =>
                    {
                        try
                        {
                            var success = await InitializeConfigsAsync();
                            if (success)
                            {
                                _imageMgr?.LogInfo("FreeClient", "Free配置初始化成功！请重新尝试使用Free提供商");
                            }
                            else
                            {
                                _imageMgr?.LogWarning("FreeClient", "Free配置初始化失败，请检查网络连接或稍后重试");
                            }
                        }
                        catch (Exception ex)
                        {
                            _imageMgr?.LogError("FreeClient", $"异步初始化Free配置失败: {ex.Message}");
                        }
                    });
                }
                else
                {
                    _imageMgr?.LogDebug("FreeClient", "找到有效的Free配置文件");
                }
            }
            catch (Exception ex)
            {
                _imageMgr?.LogError("FreeClient", $"检查配置状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查是否有有效的Chat配置
        /// </summary>
        private bool HasValidChatConfig(string configDirectory)
        {
            try
            {
                var files = Directory.GetFiles(configDirectory);
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.Length == 32 && !fileName.Contains("."))
                    {
                        try
                        {
                            var encryptedContent = File.ReadAllText(file);
                            var decryptedContent = DecryptConfig(encryptedContent);
                            if (!string.IsNullOrEmpty(decryptedContent))
                            {
                                var json = JObject.Parse(decryptedContent);
                                var model = json["Model"]?.ToString();
                                if (model == "bymbymbym") // Chat配置的特征
                                {
                                    return true;
                                }
                            }
                        }
                        catch
                        {
                            // 忽略解密失败的文件
                        }
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 初始化配置 - 检查并更新所有配置文件（简化版本）
        /// </summary>
        private async Task<bool> InitializeConfigsAsync()
        {
            try
            {
                _imageMgr?.LogInfo("FreeClient", "开始初始化Free配置...");

                // 下载版本信息
                var versionInfo = await DownloadVersionInfoAsync();
                if (versionInfo is null)
                {
                    _imageMgr?.LogWarning("FreeClient", "无法获取版本信息");
                    return false;
                }

                // 只初始化Chat配置
                bool chatOk = await CheckAndUpdateConfigAsync("Free_Chat_Config.json", versionInfo);

                _imageMgr?.LogInfo("FreeClient", $"Free配置初始化完成: {chatOk}");
                return chatOk;
            }
            catch (Exception ex)
            {
                _imageMgr?.LogError("FreeClient", $"初始化配置异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 下载版本信息
        /// </summary>
        private async Task<JObject> DownloadVersionInfoAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var url = "https://vpetllm.ycxom.com/api/vpetllm.json";
                _imageMgr?.LogDebug("FreeClient", $"开始下载版本信息: {url}");
                var response = await client.GetStringAsync(url);
                _imageMgr?.LogDebug("FreeClient", $"版本信息下载成功，内容长度: {response.Length}");
                var versionInfo = JObject.Parse(response);
                _imageMgr?.LogDebug("FreeClient", $"版本信息解析成功");
                return versionInfo;
            }
            catch (Exception ex)
            {
                _imageMgr?.LogError("FreeClient", $"下载版本信息失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 检查并更新配置文件
        /// </summary>
        private async Task<bool> CheckAndUpdateConfigAsync(string configName, JObject versionInfo)
        {
            try
            {
                // 从版本信息中获取期望的MD5
                var configKey = configName.Replace(".json", "");
                _imageMgr?.LogDebug("FreeClient", $"查找配置键: {configKey}");

                var expectedMd5 = versionInfo["vpetllm"]?[configKey]?.ToString();
                if (string.IsNullOrEmpty(expectedMd5))
                {
                    _imageMgr?.LogWarning("FreeClient", $"版本信息中未找到 {configName} 的MD5");
                    _imageMgr?.LogDebug("FreeClient", $"版本信息内容: {versionInfo}");
                    return false;
                }

                _imageMgr?.LogDebug("FreeClient", $"期望的MD5: {expectedMd5}");

                var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var configDirectory = Path.Combine(documentsPath, "VPetLLM", "FreeConfig");
                var encryptedPath = Path.Combine(configDirectory, expectedMd5);

                // 检查加密文件是否存在
                if (File.Exists(encryptedPath))
                {
                    _imageMgr?.LogDebug("FreeClient", $"配置已是最新 (MD5: {expectedMd5})");
                    return true;
                }

                // 需要下载新配置
                _imageMgr?.LogInfo("FreeClient", $"下载新配置 {configName}...");
                var configContent = await DownloadConfigAsync(configName);
                if (string.IsNullOrEmpty(configContent))
                {
                    return false;
                }

                _imageMgr?.LogDebug("FreeClient", $"配置内容下载成功，长度: {configContent.Length}");

                // 计算下载内容的MD5
                var actualMd5 = CalculateMD5(configContent);
                _imageMgr?.LogDebug("FreeClient", $"实际MD5: {actualMd5}");

                if (actualMd5 != expectedMd5)
                {
                    _imageMgr?.LogWarning("FreeClient", $"MD5校验失败 - 期望:{expectedMd5}, 实际:{actualMd5}");
                    return false;
                }

                // 加密并保存
                var encryptedContent = EncryptConfig(configContent);
                File.WriteAllText(encryptedPath, encryptedContent);

                _imageMgr?.LogInfo("FreeClient", $"配置更新成功，已保存为: {expectedMd5}");

                // 清理旧的Chat配置文件
                CleanOldEncryptedFiles(expectedMd5, "Chat", configDirectory);

                return true;
            }
            catch (Exception ex)
            {
                _imageMgr?.LogError("FreeClient", $"更新配置 {configName} 失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 下载配置文件
        /// </summary>
        private async Task<string> DownloadConfigAsync(string configName)
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var url = $"https://vpetllm.ycxom.com/api/{configName}";
                return await client.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                _imageMgr?.LogError("FreeClient", $"下载配置 {configName} 失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 加密配置内容
        /// </summary>
        private string EncryptConfig(string content)
        {
            // 使用简单的XOR加密 + Base64
            var key = "VPetLLM_Free_Config_Key_2024";
            var contentBytes = Encoding.UTF8.GetBytes(content);
            var keyBytes = Encoding.UTF8.GetBytes(key);

            for (int i = 0; i < contentBytes.Length; i++)
            {
                contentBytes[i] ^= keyBytes[i % keyBytes.Length];
            }

            return Convert.ToBase64String(contentBytes);
        }

        /// <summary>
        /// 计算字符串的MD5
        /// </summary>
        private string CalculateMD5(string content)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(content));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// 清理旧的加密文件（同类型配置的旧版本）
        /// </summary>
        private void CleanOldEncryptedFiles(string currentMd5, string configType, string configDirectory)
        {
            try
            {
                var files = Directory.GetFiles(configDirectory);
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    // 如果文件名是32位MD5格式且不是当前版本
                    if (fileName.Length == 32 && fileName != currentMd5 && !fileName.Contains("."))
                    {
                        // 尝试解密并检查是否是同类型配置
                        try
                        {
                            var encryptedContent = File.ReadAllText(file);
                            var decryptedContent = DecryptConfig(encryptedContent);
                            if (!string.IsNullOrEmpty(decryptedContent))
                            {
                                var json = JObject.Parse(decryptedContent);
                                var model = json["Model"]?.ToString();

                                // 根据Model判断配置类型，只删除同类型的旧配置
                                bool isSameType = false;
                                if (configType == "ASR" && model == "LBGAME") isSameType = true;
                                else if (configType == "Chat" && model == "bymbymbym") isSameType = true;
                                else if (configType == "TTS" && model == "vpetllm") isSameType = true;

                                if (isSameType)
                                {
                                    File.Delete(file);
                                    _imageMgr?.LogInfo("FreeClient", $"清理旧{configType}配置文件: {fileName}");
                                }
                            }
                        }
                        catch
                        {
                            // 无法解密或解析的文件，可能是损坏的，也删除
                            File.Delete(file);
                            _imageMgr?.LogInfo("FreeClient", $"清理无效配置文件: {fileName}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _imageMgr?.LogError("FreeClient", $"清理旧文件失败: {ex.Message}");
            }
        }

        public async Task<string> SendRequestAsync(string prompt)
        {
            try
            {
                if (string.IsNullOrEmpty(_apiUrl) || string.IsNullOrEmpty(_apiKey))
                {
                    var errorMessage = "Free Chat 配置未加载，请等待配置下载完成后重启程序";
                    _imageMgr?.LogError("FreeClient", errorMessage);
                    throw new Exception(errorMessage);
                }

                var requestBody = new
                {
                    model = _model,
                    messages = new[]
                    {
                        new { role = "system", content = "你是一个情感分析助手。请从用户的句子中提取1-3个最相关的情感关键词。如果用户提供了可用标签列表，请从列表中选择；如果没有提供列表，请用中文返回情感关键词。只返回关键词，用逗号分隔，不要有其他解释。" },
                        new { role = "user", content = prompt }
                    },
                    temperature = 0.3,
                    max_tokens = 50,
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = true });
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // 记录完整的HTTP请求信息
                _imageMgr?.LogDebug("FreeClient", "=== Free HTTP 请求开始 ===");
                _imageMgr?.LogDebug("FreeClient", $"URL: {_apiUrl}");
                _imageMgr?.LogDebug("FreeClient", $"Method: POST");
                _imageMgr?.LogDebug("FreeClient", $"Content-Type: application/json");
                _imageMgr?.LogDebug("FreeClient", $"Authorization: Bearer {(_apiKey?.Length > 10 ? _apiKey.Substring(0, 10) + "..." : _apiKey)}");
                _imageMgr?.LogDebug("FreeClient", "请求体:");
                _imageMgr?.LogDebug("FreeClient", json);
                _imageMgr?.LogDebug("FreeClient", "=== Free HTTP 请求结束 ===");

                var response = await _httpClient.PostAsync(_apiUrl, content);
                var responseJson = await response.Content.ReadAsStringAsync();

                // 记录完整的HTTP响应信息
                _imageMgr?.LogDebug("FreeClient", "=== Free HTTP 响应开始 ===");
                _imageMgr?.LogDebug("FreeClient", $"状态码: {response.StatusCode}");
                _imageMgr?.LogDebug("FreeClient", $"响应头:");
                foreach (var header in response.Headers)
                {
                    _imageMgr?.LogDebug("FreeClient", $"  {header.Key}: {string.Join(", ", header.Value)}");
                }
                foreach (var header in response.Content.Headers)
                {
                    _imageMgr?.LogDebug("FreeClient", $"  {header.Key}: {string.Join(", ", header.Value)}");
                }
                _imageMgr?.LogDebug("FreeClient", $"响应体:");
                _imageMgr?.LogDebug("FreeClient", responseJson);
                _imageMgr?.LogDebug("FreeClient", "=== Free HTTP 响应结束 ===");

                if (!response.IsSuccessStatusCode)
                {
                    // 检查是否是服务器错误
                    if (responseJson.Contains("Failed to retrieve proxy group") ||
                        responseJson.Contains("INTERNAL_SERVER_ERROR") ||
                        response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        _imageMgr?.LogError("FreeClient", $"Free API 服务器错误: {response.StatusCode} - {responseJson}");
                        throw new Exception("Free API 服务暂时不可用，请稍后再试");
                    }
                    else
                    {
                        _imageMgr?.LogError("FreeClient", $"Free API 错误: {response.StatusCode} - {responseJson}");
                        throw new Exception($"API调用失败: {response.StatusCode} - {responseJson}");
                    }
                }

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
                _imageMgr?.LogError("FreeClient", $"Error: {ex.Message}");
                _imageMgr?.LogDebug("FreeClient", $"StackTrace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// 获取可用的Free模型列表
        /// </summary>
        public async Task<List<FreeModelInfo>> GetAvailableModelsAsync()
        {
            try
            {
                // Free服务通常提供固定的模型列表
                var models = new List<FreeModelInfo>
                {
                    new FreeModelInfo { Id = "gpt-3.5-turbo", Name = "GPT-3.5 Turbo", Description = "OpenAI GPT-3.5 Turbo" },
                    new FreeModelInfo { Id = "gpt-4", Name = "GPT-4", Description = "OpenAI GPT-4" },
                    new FreeModelInfo { Id = "gpt-4-turbo", Name = "GPT-4 Turbo", Description = "OpenAI GPT-4 Turbo" },
                    new FreeModelInfo { Id = "claude-3-haiku", Name = "Claude 3 Haiku", Description = "Anthropic Claude 3 Haiku" },
                    new FreeModelInfo { Id = "claude-3-sonnet", Name = "Claude 3 Sonnet", Description = "Anthropic Claude 3 Sonnet" }
                };

                _imageMgr?.LogDebug("FreeClient", $"返回 {models.Count} 个Free模型");
                return models;
            }
            catch (Exception ex)
            {
                _imageMgr?.LogError("FreeClient", $"GetModels Error: {ex.Message}");
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
            // Free服务通常不提供嵌入功能，返回空数组或抛出异常
            _imageMgr?.LogWarning("FreeClient", "Free服务不支持嵌入功能");
            throw new NotSupportedException("Free服务不支持嵌入功能");
        }

        /// <summary>
        /// 发送视觉分析请求（支持图片）
        /// </summary>
        /// <param name="prompt">文本提示词</param>
        /// <param name="base64Image">Base64编码的图片</param>
        /// <returns>LLM响应内容</returns>
        public async Task<string> SendVisionRequestAsync(string prompt, string base64Image)
        {
            try
            {
                if (string.IsNullOrEmpty(_apiUrl) || string.IsNullOrEmpty(_apiKey))
                {
                    var errorMessage = "Free Chat 配置未加载，请等待配置下载完成后重启程序";
                    _imageMgr?.LogError("FreeClient", errorMessage);
                    throw new Exception(errorMessage);
                }

                var requestBody = new
                {
                    model = _model,
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
                    max_tokens = 1000,
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = true });
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // 记录HTTP请求信息
                _imageMgr?.LogDebug("FreeClient", "=== Free Vision HTTP 请求开始 ===");
                _imageMgr?.LogDebug("FreeClient", $"URL: {_apiUrl}");
                _imageMgr?.LogDebug("FreeClient", $"Model: {_model}");
                _imageMgr?.LogDebug("FreeClient", "=== Free Vision HTTP 请求结束 ===");

                var response = await _httpClient.PostAsync(_apiUrl, content);
                var responseJson = await response.Content.ReadAsStringAsync();

                _imageMgr?.LogDebug("FreeClient", "=== Free Vision HTTP 响应开始 ===");
                _imageMgr?.LogDebug("FreeClient", $"状态码: {response.StatusCode}");
                _imageMgr?.LogDebug("FreeClient", $"响应体: {responseJson}");
                _imageMgr?.LogDebug("FreeClient", "=== Free Vision HTTP 响应结束 ===");

                if (!response.IsSuccessStatusCode)
                {
                    if (responseJson.Contains("Failed to retrieve proxy group") ||
                        responseJson.Contains("INTERNAL_SERVER_ERROR") ||
                        response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        _imageMgr?.LogError("FreeClient", $"Free API 服务器错误: {response.StatusCode} - {responseJson}");
                        throw new Exception("Free API 服务暂时不可用，请稍后再试");
                    }
                    else
                    {
                        _imageMgr?.LogError("FreeClient", $"Free API 错误: {response.StatusCode} - {responseJson}");
                        throw new Exception($"API调用失败: {response.StatusCode} - {responseJson}");
                    }
                }

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
                _imageMgr?.LogError("FreeClient", $"Vision request error: {ex.Message}");
                _imageMgr?.LogDebug("FreeClient", $"StackTrace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// 获取当前系统语言代码
        /// </summary>
        private string GetCurrentLanguageCode()
        {
            try
            {
                var currentCulture = System.Globalization.CultureInfo.CurrentCulture;
                var languageCode = currentCulture.Name.ToLowerInvariant();

                // 映射到支持的语言代码
                return languageCode switch
                {
                    var s when s.StartsWith("zh") =>
                        s.Contains("hant") || s.Contains("tw") || s.Contains("hk") ? "zh-hant" : "zh-hans",
                    var s when s.StartsWith("ja") || s.StartsWith("jp") => "jp",
                    var s when s.StartsWith("en") => "en",
                    _ => "zh-hans" // 默认使用简体中文
                };
            }
            catch
            {
                return "zh-hans";
            }
        }

        /// <summary>
        /// 获取Free服务的描述信息（根据当前系统语言）
        /// </summary>
        public string GetDescription()
        {
            try
            {
                var config = GetChatConfig();
                if (config != null)
                {
                    var langCode = GetCurrentLanguageCode();
                    var description = config["Language"]?.Value<JObject>()?["Description"]?.Value<string>(langCode);
                    return description ?? config["Language"]?.Value<JObject>()?["Description"]?.Value<string>("zh-hans") ??
                           "Free Chat 使用内置的免费LLM服务，无需配置 API Key。";
                }
            }
            catch (Exception ex)
            {
                _imageMgr?.LogDebug("FreeClient", $"获取描述信息失败: {ex.Message}");
            }
            return "Free Chat 使用内置的免费LLM服务，无需配置 API Key。";
        }

        /// <summary>
        /// 获取Free服务的提供者信息（根据当前系统语言）
        /// </summary>
        public string GetProvider()
        {
            try
            {
                var config = GetChatConfig();
                if (config != null)
                {
                    var langCode = GetCurrentLanguageCode();
                    var provider = config["Language"]?.Value<JObject>()?["Provider"]?.Value<string>(langCode);
                    return provider ?? config["Language"]?.Value<JObject>()?["Provider"]?.Value<string>("zh-hans") ??
                           "感谢提供者 QQ：790132463";
                }
            }
            catch (Exception ex)
            {
                _imageMgr?.LogDebug("FreeClient", $"获取提供者信息失败: {ex.Message}");
            }
            return "感谢提供者 QQ：790132463";
        }

        /// <summary>
        /// 获取Chat配置（简化版本，不依赖VPetLLM的完整FreeConfigManager）
        /// </summary>
        private JObject GetChatConfig()
        {
            try
            {
                // 尝试从VPetLLM的配置目录读取
                var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var configDirectory = Path.Combine(documentsPath, "VPetLLM", "FreeConfig");

                if (!Directory.Exists(configDirectory))
                {
                    _imageMgr?.LogWarning("FreeClient", $"VPetLLM配置目录不存在: {configDirectory}");
                    return null;
                }

                // 查找Chat配置文件
                var files = Directory.GetFiles(configDirectory);
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    // 检查是否是32位MD5格式（无扩展名）
                    if (fileName.Length == 32 && !fileName.Contains("."))
                    {
                        try
                        {
                            var encryptedContent = File.ReadAllText(file);
                            var decryptedContent = DecryptConfig(encryptedContent);
                            if (!string.IsNullOrEmpty(decryptedContent))
                            {
                                var json = JObject.Parse(decryptedContent);
                                // 检查是否是Chat配置（通过Model字段判断）
                                var model = json["Model"]?.ToString();
                                if (model == "bymbymbym") // Chat配置的特征
                                {
                                    _imageMgr?.LogDebug("FreeClient", $"找到Free Chat配置文件: {fileName}");
                                    return json;
                                }
                            }
                        }
                        catch
                        {
                            // 忽略解密失败的文件
                        }
                    }
                }

                _imageMgr?.LogWarning("FreeClient", "未找到Free Chat配置文件");
                return null;
            }
            catch (Exception ex)
            {
                _imageMgr?.LogError("FreeClient", $"读取Free配置失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解密配置内容（与VPetLLM保持一致）
        /// </summary>
        private string DecryptConfig(string encryptedContent)
        {
            try
            {
                var key = "VPetLLM_Free_Config_Key_2024";
                var contentBytes = Convert.FromBase64String(encryptedContent);
                var keyBytes = Encoding.UTF8.GetBytes(key);

                for (int i = 0; i < contentBytes.Length; i++)
                {
                    contentBytes[i] ^= keyBytes[i % keyBytes.Length];
                }

                return Encoding.UTF8.GetString(contentBytes);
            }
            catch (Exception ex)
            {
                _imageMgr?.LogError("FreeClient", $"解密配置失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解码字符串（与VPetLLM保持一致）
        /// </summary>
        private string DecodeString(string encodedString)
        {
            try
            {
                if (string.IsNullOrEmpty(encodedString))
                {
                    return "";
                }

                // 第一步：Hex解码
                var hexBytes = new byte[encodedString.Length / 2];
                for (int i = 0; i < hexBytes.Length; i++)
                {
                    hexBytes[i] = Convert.ToByte(encodedString.Substring(i * 2, 2), 16);
                }

                // 第二步：Base64解码
                var base64String = Encoding.UTF8.GetString(hexBytes);
                var finalBytes = Convert.FromBase64String(base64String);
                var result = Encoding.UTF8.GetString(finalBytes);

                return result;
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}