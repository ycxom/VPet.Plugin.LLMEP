#nullable enable
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using VPet.Plugin.LLMEP.Utils;

namespace VPet.Plugin.LLMEP.Services
{
    /// <summary>
    /// 在线网络表情包库服务
    /// 基于 StickerPlugin 的 API 实现
    /// </summary>
    public class OnlineStickerService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string? _apiKey;
        private readonly ulong _steamId;
        private readonly Func<Task<int>>? _getAuthKey;
        private readonly bool _useBuiltInCredentials;
        private List<string>? _cachedTags;
        private DateTime _cacheTime = DateTime.MinValue;

        public string? LastError { get; private set; }

        public OnlineStickerService(string baseUrl, string? apiKey = null,
            ulong steamId = 0, Func<Task<int>>? getAuthKey = null, bool useBuiltInCredentials = true)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _apiKey = apiKey;
            _steamId = steamId;
            _getAuthKey = getAuthKey;
            _useBuiltInCredentials = useBuiltInCredentials;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            if (!string.IsNullOrEmpty(_apiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
            }
        }

        /// <summary>
        /// 健康检查
        /// </summary>
        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                var response = await PostAsync<object, HealthResponse>("/api/health", new { });
                return response?.Success ?? false;
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerService", $"健康检查失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 搜索表情包
        /// </summary>
        public async Task<SearchResponse?> SearchAsync(string query, int limit = 1, double minScore = 0.2)
        {
            try
            {
                var request = new SearchRequest
                {
                    Query = query,
                    Limit = limit,
                    MinScore = minScore,
                    IncludeBase64 = true,
                    Random = true
                };

                Logger.Debug("OnlineStickerService", $"搜索表情包: {query}, 限制: {limit}, 最小分数: {minScore}");
                var response = await PostAsync<SearchRequest, SearchResponse>("/api/search", request);

                if (response?.Success == true)
                {
                    Logger.Info("OnlineStickerService", $"搜索成功，找到 {response.Results?.Count ?? 0} 个结果");
                }
                else
                {
                    Logger.Warning("OnlineStickerService", $"搜索失败: {response?.Error ?? "未知错误"}");
                }

                return response;
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerService", $"搜索表情包失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取所有标签
        /// </summary>
        public async Task<TagsResponse?> GetTagsAsync()
        {
            try
            {
                Logger.Debug("OnlineStickerService", "获取标签列表");
                var response = await PostAsync<object, TagsResponse>("/api/tags", new { });

                if (response?.Success == true)
                {
                    Logger.Info("OnlineStickerService", $"获取标签成功，共 {response.Tags?.Count ?? 0} 个标签");
                }
                else
                {
                    Logger.Warning("OnlineStickerService", $"获取标签失败: {response?.Error ?? "未知错误"}");
                }

                return response;
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerService", $"获取标签失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取缓存的标签（带缓存机制）
        /// </summary>
        public async Task<List<string>> GetCachedTagsAsync(TimeSpan cacheDuration)
        {
            if (_cachedTags != null && DateTime.Now - _cacheTime < cacheDuration)
            {
                Logger.Debug("OnlineStickerService", $"使用缓存的标签，共 {_cachedTags.Count} 个");
                return _cachedTags;
            }

            var response = await GetTagsAsync();
            if (response?.Success == true && response.Tags != null)
            {
                _cachedTags = response.Tags;
                _cacheTime = DateTime.Now;
                Logger.Info("OnlineStickerService", $"标签缓存已更新，共 {_cachedTags.Count} 个");
                return _cachedTags;
            }

            return _cachedTags ?? new List<string>();
        }

        /// <summary>
        /// 清除标签缓存
        /// </summary>
        public void InvalidateCache()
        {
            _cachedTags = null;
            _cacheTime = DateTime.MinValue;
            Logger.Debug("OnlineStickerService", "标签缓存已清除");
        }

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public async Task<StatsResponse?> GetStatsAsync()
        {
            try
            {
                Logger.Debug("OnlineStickerService", "获取统计信息");
                var response = await PostAsync<object, StatsResponse>("/api/stats", new { });

                if (response?.Success == true)
                {
                    Logger.Info("OnlineStickerService", $"获取统计信息成功: 总图片 {response.TotalImages}, 已索引 {response.IndexedImages}");
                }

                return response;
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerService", $"获取统计信息失败: {ex.Message}");
                return null;
            }
        }

        private async Task<TResponse?> PostAsync<TRequest, TResponse>(string endpoint, TRequest request)
            where TResponse : class
        {
            try
            {
                var url = _baseUrl + endpoint;
                var json = JsonConvert.SerializeObject(request);

                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
                requestMessage.Content = new StringContent(json, Encoding.UTF8, "application/json");

                // 添加内置凭证头（如果启用）
                if (_useBuiltInCredentials)
                {
                    var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    int ck = 0;
                    if (_getAuthKey != null)
                    {
                        try
                        {
                            ck = await _getAuthKey();
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning("OnlineStickerService", $"获取认证密钥失败: {ex.Message}");
                        }
                    }

                    requestMessage.Headers.Add("X-Cache-Token", EncryptData(_steamId.ToString(), ts));
                    requestMessage.Headers.Add("X-Request-Signature", EncryptData(GetMagicNumber(), ts));
                    requestMessage.Headers.Add("X-Check-Key", EncryptData(ck.ToString(), ts));
                    requestMessage.Headers.Add("X-Trace-Id", GenerateTraceId(ts));
                }

                var response = await _httpClient.SendAsync(requestMessage);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return JsonConvert.DeserializeObject<TResponse>(responseJson);
                }

                LastError = $"HTTP {(int)response.StatusCode}";
                Logger.Error("OnlineStickerService", $"HTTP请求失败: {LastError}, 响应: {responseJson}");
                return null;
            }
            catch (TaskCanceledException)
            {
                LastError = "请求超时";
                Logger.Error("OnlineStickerService", "HTTP请求超时");
                return null;
            }
            catch (HttpRequestException ex)
            {
                LastError = ex.Message;
                Logger.Error("OnlineStickerService", $"HTTP请求异常: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Logger.Error("OnlineStickerService", $"请求处理异常: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        private static string GetMagicNumber()
        {
            var a = 0x1A2B ^ 0x1A2B;
            var p = new[] {
                (char)(51+a), (char)(53+a), (char)(54+a), (char)(49+a), (char)(57+a),
                (char)(51+a), (char)(50+a), (char)(52+a), (char)(49+a), (char)(53+a)
            };
            return new string(p);
        }

        private static long CalculateTimeHash(long timestamp)
        {
            var d = timestamp.ToString();
            long f = 0;
            for (int i = 0; i < d.Length; i++)
            {
                f += (d[i] - '0') * (i + 1);
            }
            return f % 60;
        }

        private static long ObfuscateTimestamp(long timestamp)
        {
            return (timestamp ^ (CalculateTimeHash(timestamp) * 0x5A5A)) + CalculateTimeHash(timestamp);
        }

        private static string EncryptData(string plaintext, long timestamp)
        {
            try
            {
                var obfuscatedTime = ObfuscateTimestamp(timestamp);
                using var sha256 = SHA256.Create();
                var key = sha256.ComputeHash(Encoding.UTF8.GetBytes(obfuscatedTime.ToString() + "VPetLLM_"));

                using var md5 = MD5.Create();
                var iv = md5.ComputeHash(Encoding.UTF8.GetBytes(timestamp.ToString()));

                using var aes = Aes.Create();
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var encryptor = aes.CreateEncryptor();
                var plainBytes = Encoding.UTF8.GetBytes(plaintext);
                var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

                return Convert.ToBase64String(encryptedBytes);
            }
            catch (Exception ex)
            {
                Logger.Warning("OnlineStickerService", $"数据加密失败: {ex.Message}");
                return plaintext;
            }
        }

        private static string GenerateTraceId(long timestamp)
        {
            try
            {
                var randomBytes = new byte[16];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(randomBytes);
                }

                var combined = new byte[20];
                Array.Copy(randomBytes, 0, combined, 0, 16);
                Array.Copy(BitConverter.GetBytes((int)(timestamp % 10000)), 0, combined, 16, 4);

                var xorKey = (byte)(ObfuscateTimestamp(timestamp) & 0xFF);
                for (int i = 0; i < 20; i++)
                {
                    combined[i] ^= xorKey;
                }

                return Convert.ToBase64String(combined);
            }
            catch (Exception ex)
            {
                Logger.Warning("OnlineStickerService", $"生成跟踪ID失败: {ex.Message}");
                return Convert.ToBase64String(Guid.NewGuid().ToByteArray());
            }
        }
    }

    public class SearchRequest
    {
        [JsonProperty("query")]
        public string Query { get; set; } = string.Empty;

        [JsonProperty("limit")]
        public int Limit { get; set; } = 1;

        [JsonProperty("minScore")]
        public double MinScore { get; set; } = 0.2;

        [JsonProperty("includeBase64")]
        public bool IncludeBase64 { get; set; } = true;

        [JsonProperty("random")]
        public bool Random { get; set; } = true;
    }

    public class SearchResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("results")]
        public List<SearchResult> Results { get; set; } = new();

        [JsonProperty("error")]
        public string? Error { get; set; }
    }

    public class SearchResult
    {
        [JsonProperty("filename")]
        public string Filename { get; set; } = string.Empty;

        [JsonProperty("filepath")]
        public string? Filepath { get; set; }

        [JsonProperty("tags")]
        public List<string> Tags { get; set; } = new();

        [JsonProperty("score")]
        public double Score { get; set; }

        [JsonProperty("created_at")]
        public string? CreatedAt { get; set; }

        [JsonProperty("base64")]
        public string? Base64 { get; set; }
    }

    public class TagsResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("tags")]
        public List<string> Tags { get; set; } = new();

        [JsonProperty("error")]
        public string? Error { get; set; }
    }

    public class HealthResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; } = string.Empty;

        [JsonProperty("error")]
        public string? Error { get; set; }
    }

    public class StatsResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("totalImages")]
        public int TotalImages { get; set; }

        [JsonProperty("indexedImages")]
        public int IndexedImages { get; set; }

        [JsonProperty("error")]
        public string? Error { get; set; }
    }
}