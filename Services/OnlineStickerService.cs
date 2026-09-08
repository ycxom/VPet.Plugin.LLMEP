#nullable enable
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
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
        private readonly bool _useBuiltInCredentials;
        private List<string>? _cachedTags;
        private DateTime _cacheTime = DateTime.MinValue;

        public string? LastError { get; private set; }

        public OnlineStickerService(string baseUrl, string? apiKey = null, bool useBuiltInCredentials = true)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _apiKey = apiKey;
            _useBuiltInCredentials = useBuiltInCredentials;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            if (!string.IsNullOrEmpty(_apiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
            }
        }

        /// <summary>
        /// 官方内置服务把请求交给本 MOD 自带的鉴权通道；用户自填地址/凭证的
        /// 第三方服务仍直接发送（不得套用官方协议）。
        /// </summary>
        private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            if (!_useBuiltInCredentials)
                return _httpClient.SendAsync(request);
            return AuthenticatedServiceTransport.SendAsync(_httpClient, request);
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
        /// 不请求 Base64 数据，避免服务端把图片内联进搜索响应造成额外带宽消耗；
        /// 命中结果后按 <see cref="GetImageAsync"/> 用 id 单独获取原始二进制数据
        /// </summary>
        public async Task<SearchResponse?> SearchAsync(string query, int limit = 1, double minScore = 0.2, bool includeBase64 = false)
        {
            try
            {
                var request = new SearchRequest
                {
                    Query = query,
                    Limit = limit,
                    MinScore = minScore,
                    IncludeBase64 = includeBase64,
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

        /// <summary>
        /// 按 id（或 filename）获取单张图片的原始二进制数据
        /// 相比 includeBase64 内联返回，省去了 Base64 编码带来的约 33% 体积膨胀，
        /// 且只在真正需要展示时才按需下载
        /// </summary>
        public async Task<(byte[]? Bytes, string? ContentType)> GetImageAsync(string id)
        {
            try
            {
                var url = _baseUrl + "/api/image";
                var json = JsonConvert.SerializeObject(new { id });

                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
                requestMessage.Content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await SendAsync(requestMessage);
                if (!response.IsSuccessStatusCode)
                {
                    LastError = $"HTTP {(int)response.StatusCode}";
                    Logger.Error("OnlineStickerService", $"获取图片失败: {LastError}, id: {id}");
                    return (null, null);
                }

                var bytes = await response.Content.ReadAsByteArrayAsync();
                var contentType = response.Content.Headers.ContentType?.MediaType;
                return (bytes, contentType);
            }
            catch (TaskCanceledException)
            {
                LastError = "请求超时";
                Logger.Error("OnlineStickerService", "获取图片超时");
                return (null, null);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Logger.Error("OnlineStickerService", $"获取图片异常: {ex.Message}");
                return (null, null);
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

                var response = await SendAsync(requestMessage);
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
        public bool IncludeBase64 { get; set; } = false;

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
        [JsonProperty("id")]
        public string? Id { get; set; }

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