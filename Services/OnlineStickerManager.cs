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
                }

                return tags;
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerManager", $"获取可用标签失败: {ex.Message}");
                return _availableTags ?? new List<string>();
            }
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

            try
            {
                Logger.Info("OnlineStickerManager", $"开始搜索表情包: 情感={emotion}");

                // 构建搜索查询
                var searchTags = new List<string> { emotion };
                if (additionalTags != null && additionalTags.Count > 0)
                {
                    searchTags.AddRange(additionalTags);
                }

                string query = string.Join(", ", searchTags);
                Logger.Debug("OnlineStickerManager", $"搜索查询: {query}");

                // 执行搜索
                var response = await _stickerService.SearchAsync(query, limit: 1, minScore: 0.2);

                if (response?.Success == true && response.Results?.Count > 0)
                {
                    var result = response.Results.OrderByDescending(r => r.Score).First();
                    Logger.Info("OnlineStickerManager", $"找到匹配的表情包: {result.Filename}, 分数: {result.Score:F2}");

                    // 显示表情包
                    if (!string.IsNullOrEmpty(result.Base64))
                    {
                        await DisplayBase64ImageAsync(result.Base64);
                        return true;
                    }
                    else
                    {
                        Logger.Warning("OnlineStickerManager", "表情包数据为空");
                    }
                }
                else
                {
                    Logger.Info("OnlineStickerManager", $"未找到匹配的表情包: {response?.Error ?? "无结果"}");
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerManager", $"搜索并显示表情包失败: {ex.Message}");
                return false;
            }
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
        /// 显示 Base64 编码的图片
        /// </summary>
        private async Task DisplayBase64ImageAsync(string base64Data)
        {
            try
            {
                Logger.Debug("OnlineStickerManager", "开始显示Base64图片");
                Logger.Debug("OnlineStickerManager", $"Base64数据长度: {base64Data?.Length ?? 0}");

                if (string.IsNullOrEmpty(base64Data))
                {
                    Logger.Warning("OnlineStickerManager", "Base64数据为空");
                    return;
                }

                // 解析 Base64 数据（参考 StickerPlugin 的处理方式）
                var base64 = base64Data;
                var isGif = false;

                // 检测是否为 GIF（通过 data URI 前缀）
                if (base64.StartsWith("data:image/gif"))
                {
                    isGif = true;
                    Logger.Debug("OnlineStickerManager", "检测到GIF格式（通过data URI前缀）");
                }

                // 移除 data:image/xxx;base64, 前缀
                if (base64.Contains(","))
                {
                    base64 = base64.Substring(base64.IndexOf(",") + 1);
                    Logger.Debug("OnlineStickerManager", "已移除data URI前缀");
                }

                Logger.Debug("OnlineStickerManager", $"清理后Base64数据长度: {base64.Length}");

                // 转换为字节数组
                byte[] imageBytes = Convert.FromBase64String(base64);
                Logger.Debug("OnlineStickerManager", $"解码后图片字节数: {imageBytes.Length}");

                // 检测 GIF 文件头 (47 49 46 38 = "GIF8")
                if (!isGif && imageBytes.Length > 4)
                {
                    isGif = imageBytes[0] == 0x47 && imageBytes[1] == 0x49 &&
                            imageBytes[2] == 0x46 && imageBytes[3] == 0x38;
                    if (isGif)
                    {
                        Logger.Debug("OnlineStickerManager", "检测到GIF格式（通过文件头）");
                    }
                }

                // 关闭之前的GIF流（如果存在）
                _currentGifStream?.Dispose();
                _currentGifStream = null;

                // 创建 BitmapImage
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
            catch (FormatException ex)
            {
                Logger.Error("OnlineStickerManager", $"Base64格式错误: {ex.Message}");
                Logger.Debug("OnlineStickerManager", $"原始Base64数据前100字符: {base64Data?.Substring(0, Math.Min(100, base64Data?.Length ?? 0))}");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error("OnlineStickerManager", $"显示Base64图片失败: {ex.Message}");
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