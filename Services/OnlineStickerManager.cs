#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using VPet.Plugin.LLMEP.Utils;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.LLMEP.Services
{
    /// <summary>
    /// 在线网络表情包管理器
    /// 负责管理在线表情包的搜索、缓存和显示
    /// </summary>
    public class OnlineStickerManager : IDisposable
    {
        private readonly ImageMgr _imageMgr;
        private readonly OnlineStickerService _stickerService;
        private readonly OnlineStickerImageCache _imageCache;
        private readonly Random _random;
        private List<string>? _availableTags;
        private DateTime _tagsLastUpdate = DateTime.MinValue;
        
        // 用于保存当前显示的GIF流的引用（GIF需要保持流打开才能播放）
        private MemoryStream? _currentGifStream;

        // 配置参数
        public bool IsEnabled { get; set; } = false;
        public bool UseBuiltInCredentials { get; set; } = true;
        public string ServiceUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public int TagCount { get; set; } = 10;
        public int CacheDurationMinutes { get; set; } = 5;
        public int DisplayDurationSeconds { get; set; } = 6;

        public OnlineStickerManager(ImageMgr imageMgr, IMainWindow mainWindow)
        {
            _imageMgr = imageMgr ?? throw new ArgumentNullException(nameof(imageMgr));
            _imageCache = new OnlineStickerImageCache();
            _random = new Random();

            // 获取 Steam ID 和认证密钥生成器
            ulong steamId = 0;
            Func<Task<int>>? getAuthKey = null;

            try
            {
                steamId = mainWindow?.SteamID ?? 0;
                getAuthKey = async () => await (mainWindow?.GenerateAuthKey() ?? Task.FromResult(0));
            }
            catch (Exception ex)
            {
                Logger.Warning("OnlineStickerManager", $"获取Steam信息失败: {ex.Message}");
            }

            // 初始化在线表情包服务
            _stickerService = new OnlineStickerService(
                GetEffectiveServiceUrl(),
                GetEffectiveApiKey(),
                steamId,
                getAuthKey,
                UseBuiltInCredentials
            );

            Logger.Info("OnlineStickerManager", "在线表情包管理器已初始化");
        }

        /// <summary>
        /// 获取有效的服务地址
        /// </summary>
        private string GetEffectiveServiceUrl()
        {
            if (UseBuiltInCredentials)
            {
                return OnlineStickerCredentials.GetBuiltInServiceUrl();
            }
            return ServiceUrl;
        }

        /// <summary>
        /// 获取有效的 API Key
        /// </summary>
        private string GetEffectiveApiKey()
        {
            if (UseBuiltInCredentials)
            {
                return OnlineStickerCredentials.GetBuiltInApiKey();
            }
            return ApiKey;
        }

        /// <summary>
        /// 更新配置
        /// </summary>
        public void UpdateConfiguration(bool isEnabled, bool useBuiltInCredentials,
            string serviceUrl, string apiKey, int tagCount, int cacheDurationMinutes, int displayDurationSeconds)
        {
            bool serviceChanged = UseBuiltInCredentials != useBuiltInCredentials ||
                                 ServiceUrl != serviceUrl ||
                                 ApiKey != apiKey;

            IsEnabled = isEnabled;
            UseBuiltInCredentials = useBuiltInCredentials;
            ServiceUrl = serviceUrl;
            ApiKey = apiKey;
            TagCount = Math.Max(1, Math.Min(100, tagCount));
            CacheDurationMinutes = Math.Max(1, cacheDurationMinutes);
            DisplayDurationSeconds = Math.Max(1, Math.Min(60, displayDurationSeconds));

            if (serviceChanged)
            {
                Logger.Info("OnlineStickerManager", "服务配置已更改，重新初始化服务");
                // 重新创建服务实例
                _stickerService?.Dispose();
                // 这里需要重新初始化 _stickerService，但由于构造函数的复杂性，暂时记录日志
                Logger.Warning("OnlineStickerManager", "服务重新初始化需要重启插件才能生效");

                // 清除缓存
                _availableTags = null;
                _tagsLastUpdate = DateTime.MinValue;
            }

            Logger.Info("OnlineStickerManager", $"配置已更新: 启用={IsEnabled}, 内置凭证={UseBuiltInCredentials}, 标签数量={TagCount}");
        }

        /// <summary>
        /// 测试服务连接
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            if (!IsEnabled)
            {
                Logger.Warning("OnlineStickerManager", "在线表情包功能未启用");
                return false;
            }

            try
            {
                Logger.Info("OnlineStickerManager", "开始测试服务连接");
                bool result = await _stickerService.HealthCheckAsync();

                if (result)
                {
                    Logger.Info("OnlineStickerManager", "服务连接测试成功");
                }
                else
                {
                    Logger.Warning("OnlineStickerManager", $"服务连接测试失败: {_stickerService.LastError}");
                }

                return result;
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerManager", $"测试服务连接时出现异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取可用标签列表
        /// </summary>
        public async Task<List<string>> GetAvailableTagsAsync()
        {
            if (!IsEnabled)
            {
                return new List<string>();
            }

            try
            {
                var cacheDuration = TimeSpan.FromMinutes(CacheDurationMinutes);
                var tags = await _stickerService.GetCachedTagsAsync(cacheDuration);

                if (tags.Count > 0)
                {
                    _availableTags = tags;
                    _tagsLastUpdate = DateTime.Now;
                    Logger.Debug("OnlineStickerManager", $"获取到 {tags.Count} 个可用标签");
                    return tags;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerManager", $"获取可用标签失败: {ex.Message}");
            }

            // 内存里之前缓存过的标签优先；如果这次是首次启动就离线（内存缓存也是空的），
            // 退化到 Temp 磁盘缓存里已经下载过的图片标签，保证离线也能随机挑一张
            if (_availableTags != null && _availableTags.Count > 0)
            {
                return _availableTags;
            }

            var offlineTags = _imageCache.GetAllCachedTags();
            if (offlineTags.Count > 0)
            {
                Logger.Debug("OnlineStickerManager", $"服务端不可用，使用离线缓存标签: {offlineTags.Count} 个");
            }
            return offlineTags;
        }

        /// <summary>
        /// 根据情感搜索并显示表情包
        /// </summary>
        public async Task<bool> SearchAndDisplayStickerAsync(string emotion, List<string> additionalTags = null)
        {
            if (!IsEnabled)
            {
                Logger.Debug("OnlineStickerManager", "在线表情包功能未启用，跳过搜索");
                return false;
            }

            // 构建搜索查询
            var searchTags = new List<string> { emotion };
            if (additionalTags != null && additionalTags.Count > 0)
            {
                searchTags.AddRange(additionalTags);
            }

            string query = string.Join(", ", searchTags);

            try
            {
                Logger.Info("OnlineStickerManager", $"开始搜索表情包: 情感={emotion}");
                Logger.Debug("OnlineStickerManager", $"搜索查询: {query}");

                // 执行搜索
                var response = await _stickerService.SearchAsync(query, limit: 1, minScore: 0.2);

                if (response?.Success == true && response.Results?.Count > 0)
                {
                    var result = response.Results.OrderByDescending(r => r.Score).First();
                    Logger.Info("OnlineStickerManager", $"找到匹配的表情包: {result.Id}, 分数: {result.Score:F2}");

                    // 按需获取图片数据（缓存优先，未命中再向服务端请求原始二进制，不走 Base64）
                    if (!string.IsNullOrEmpty(result.Id))
                    {
                        var imageBytes = await GetImageBytesAsync(result.Id!, result.Tags);

                        // /api/image 可能因服务端 API Key 权限未开放该端点而返回 403；
                        // 退化为旧的内联 Base64 方式，保证功能不因服务端权限配置问题而中断
                        if (imageBytes == null)
                        {
                            Logger.Warning("OnlineStickerManager", $"直接下载图片失败，回退到内联 Base64 方式: {result.Id}");
                            imageBytes = await GetImageBytesViaBase64FallbackAsync(query, result.Id!, result.Tags);
                        }

                        if (imageBytes != null)
                        {
                            await DisplayImageBytesAsync(imageBytes);
                            return true;
                        }
                    }
                    else
                    {
                        Logger.Warning("OnlineStickerManager", "搜索结果缺少图片 id");
                    }
                }
                else
                {
                    Logger.Info("OnlineStickerManager", $"未找到匹配的表情包: {response?.Error ?? "无结果"}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerManager", $"搜索并显示表情包失败: {ex.Message}");
            }

            // 服务端不可达、无匹配结果或下载失败时，尝试从本地 Temp 缓存离线检索
            return await TryDisplayFromOfflineCacheAsync(searchTags);
        }

        /// <summary>
        /// 离线兜底：服务端不可用时，按标签在本地磁盘缓存里挑一张之前缓存过的图片显示
        /// </summary>
        private async Task<bool> TryDisplayFromOfflineCacheAsync(List<string> searchTags)
        {
            if (!_imageCache.TryFindOffline(searchTags, out var offlineId, out var offlineBytes) || offlineBytes == null)
            {
                return false;
            }

            Logger.Info("OnlineStickerManager", $"服务端不可用，命中本地离线缓存: {offlineId}");
            await DisplayImageBytesAsync(offlineBytes);
            return true;
        }

        /// <summary>
        /// 根据标签搜索并显示表情包
        /// </summary>
        public async Task<bool> SearchAndDisplayStickerByTagsAsync(params string[] tags)
        {
            if (!IsEnabled || tags == null || tags.Length == 0)
            {
                return false;
            }

            string query = string.Join(", ", tags);
            Logger.Info("OnlineStickerManager", $"根据标签搜索表情包: {query}");

            return await SearchAndDisplayStickerAsync(tags[0], tags.Skip(1).ToList());
        }

        /// <summary>
        /// 显示随机表情包
        /// </summary>
        public async Task<bool> DisplayRandomStickerAsync()
        {
            if (!IsEnabled)
            {
                return false;
            }

            try
            {
                Logger.Info("OnlineStickerManager", "开始显示随机表情包");

                // 获取可用标签
                var availableTags = await GetAvailableTagsAsync();
                if (availableTags.Count == 0)
                {
                    Logger.Warning("OnlineStickerManager", "没有可用的标签");
                    return false;
                }

                // 随机选择标签
                var selectedTags = SelectRandomTags(availableTags, Math.Min(3, TagCount));
                if (selectedTags.Count == 0)
                {
                    Logger.Warning("OnlineStickerManager", "未能选择到标签");
                    return false;
                }

                Logger.Debug("OnlineStickerManager", $"随机选择的标签: {string.Join(", ", selectedTags)}");

                // 搜索并显示
                return await SearchAndDisplayStickerByTagsAsync(selectedTags.ToArray());
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerManager", $"显示随机表情包失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 按需获取图片数据：本地磁盘缓存命中则直接返回，否则向服务端请求原始二进制并写入缓存
        /// </summary>
        private async Task<byte[]?> GetImageBytesAsync(string id, List<string>? tags)
        {
            if (_imageCache.TryGet(id, out var cached))
            {
                Logger.Debug("OnlineStickerManager", $"使用本地缓存图片: {id}");
                return cached;
            }

            var (bytes, contentType) = await _stickerService.GetImageAsync(id);
            if (bytes == null || bytes.Length == 0)
            {
                Logger.Warning("OnlineStickerManager", $"下载图片失败: {id}, {_stickerService.LastError}");
                return null;
            }

            _imageCache.Save(id, bytes, contentType, tags);
            return bytes;
        }

        /// <summary>
        /// /api/image 不可用时的兜底方案：重新搜索并请求内联 Base64 数据，取出对应 id 的图片。
        /// 多花一次网络请求，但只在服务端未给该端点开放访问权限时才会触发。
        /// </summary>
        private async Task<byte[]?> GetImageBytesViaBase64FallbackAsync(string query, string id, List<string>? tags)
        {
            try
            {
                var response = await _stickerService.SearchAsync(query, limit: 5, minScore: 0.0, includeBase64: true);
                var match = response?.Results?.FirstOrDefault(r => r.Id == id);
                if (string.IsNullOrEmpty(match?.Base64))
                {
                    Logger.Warning("OnlineStickerManager", $"Base64回退未能取到图片: {id}");
                    return null;
                }

                var base64 = match!.Base64!;
                if (base64.Contains(","))
                {
                    base64 = base64.Substring(base64.IndexOf(",") + 1);
                }

                var bytes = Convert.FromBase64String(base64);
                _imageCache.Save(id, bytes, null, tags ?? match.Tags);
                return bytes;
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerManager", $"Base64回退失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 显示图片二进制数据
        /// </summary>
        private async Task DisplayImageBytesAsync(byte[] imageBytes)
        {
            try
            {
                Logger.Debug("OnlineStickerManager", $"开始显示图片，字节数: {imageBytes.Length}");

                // 检测 GIF 文件头 (47 49 46 38 = "GIF8")
                var isGif = imageBytes.Length > 4 &&
                            imageBytes[0] == 0x47 && imageBytes[1] == 0x49 &&
                            imageBytes[2] == 0x46 && imageBytes[3] == 0x38;
                if (isGif)
                {
                    Logger.Debug("OnlineStickerManager", "检测到GIF格式（通过文件头）");
                }

                // 关闭之前的GIF流（如果存在）
                _currentGifStream?.Dispose();
                _currentGifStream = null;

                // 在 UI 线程中创建 BitmapImage 并显示
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        BitmapImage bitmapImage;
                        MemoryStream? gifStream = null;
                        
                        if (isGif)
                        {
                            // GIF图片：保持流打开以便WpfAnimatedGif播放动画
                            gifStream = new MemoryStream(imageBytes);
                            bitmapImage = new BitmapImage();
                            bitmapImage.BeginInit();
                            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                            bitmapImage.StreamSource = gifStream;
                            bitmapImage.EndInit();
                            // GIF不能冻结，否则WpfAnimatedGif无法播放
                            
                            // 保存流引用以便后续释放
                            _currentGifStream = gifStream;
                            Logger.Debug("OnlineStickerManager", "GIF流已创建并保存");
                        }
                        else
                        {
                            // 静态图片：使用using块正常关闭流
                            using (var stream = new MemoryStream(imageBytes))
                            {
                                bitmapImage = new BitmapImage();
                                bitmapImage.BeginInit();
                                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                                bitmapImage.StreamSource = stream;
                                bitmapImage.EndInit();
                                bitmapImage.Freeze(); // 静态图片可以冻结
                            }
                        }

                        Logger.Debug("OnlineStickerManager", $"BitmapImage创建成功: {bitmapImage.PixelWidth}x{bitmapImage.PixelHeight}");
                        Logger.Info("OnlineStickerManager", $"在线表情包准备显示: {(isGif ? "GIF动画" : "静态图片")}");

                        // 显示图片（传递isGif信息确保GIF动画能正确播放）
                        _imageMgr.DisplayImagePublic(bitmapImage, isGif);
                        Logger.Info("OnlineStickerManager", "在线表情包显示成功");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("OnlineStickerManager", $"在UI线程中创建/显示图片失败: {ex.Message}");
                        Logger.Debug("OnlineStickerManager", $"错误堆栈: {ex.StackTrace}");
                    }
                });

                // 自动隐藏
                await Task.Delay(DisplayDurationSeconds * 1000);
                _imageMgr.HideImagePublic();
                
                // 隐藏后释放GIF流
                if (_currentGifStream != null)
                {
                    _currentGifStream.Dispose();
                    _currentGifStream = null;
                    Logger.Debug("OnlineStickerManager", "GIF流已释放");
                }
                
                Logger.Debug("OnlineStickerManager", "在线表情包已自动隐藏");
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerManager", $"显示图片失败: {ex.Message}");
                Logger.Debug("OnlineStickerManager", $"错误堆栈: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// 随机选择指定数量的标签
        /// </summary>
        private List<string> SelectRandomTags(List<string> allTags, int count)
        {
            if (allTags.Count <= count)
            {
                return new List<string>(allTags);
            }

            var selected = new HashSet<string>();
            var shuffled = allTags.OrderBy(_ => _random.Next()).ToList();

            foreach (var tag in shuffled)
            {
                if (selected.Count >= count)
                    break;
                selected.Add(tag);
            }

            return selected.ToList();
        }

        /// <summary>
        /// 获取系统提示词补充（用于注入 prompt）
        /// </summary>
        public async Task<string> GetSystemPromptAdditionAsync()
        {
            if (!IsEnabled)
            {
                return string.Empty;
            }

            try
            {
                var availableTags = await GetAvailableTagsAsync();
                if (availableTags.Count == 0)
                {
                    return string.Empty;
                }

                // 随机选择标签用于提示词
                var selectedTags = SelectRandomTags(availableTags, TagCount);
                if (selectedTags.Count == 0)
                {
                    return string.Empty;
                }

                var tagsStr = string.Join(", ", selectedTags);
                return $@"
[在线网络表情包库]
你可以使用在线网络表情包库发送网络表情包来增强对话表现力。
可用标签: {tagsStr}
使用方法: 当需要表达情感时，系统会自动根据情感分析结果搜索合适的在线表情包。
提示: 组合多个标签可以更精准地匹配表情包。在线表情包库包含丰富的网络表情资源。
";
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerManager", $"获取系统提示词补充失败: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 获取服务统计信息
        /// </summary>
        public async Task<(int totalImages, int indexedImages)> GetStatsAsync()
        {
            if (!IsEnabled)
            {
                return (0, 0);
            }

            try
            {
                var stats = await _stickerService.GetStatsAsync();
                if (stats?.Success == true)
                {
                    return (stats.TotalImages, stats.IndexedImages);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerManager", $"获取统计信息失败: {ex.Message}");
            }

            return (0, 0);
        }

        public void Dispose()
        {
            _currentGifStream?.Dispose();
            _currentGifStream = null;
            _stickerService?.Dispose();
            Logger.Info("OnlineStickerManager", "在线表情包管理器已释放");
        }
    }
}