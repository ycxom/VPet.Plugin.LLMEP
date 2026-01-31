using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VPet.Plugin.Image.EmotionAnalysis;
using VPet.Plugin.Image.EmotionAnalysis.LLMClient;
using VPet.Plugin.Image.Utils;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using WpfAnimatedGif;

namespace VPet.Plugin.Image
{
    public class ImageMgr : MainPlugin
    {
        public override string PluginName => "LLM表情包";

        private DispatcherTimer timer;
        private Random random;
        private ImageUI image;
        private Dictionary<IGameSave.ModeType, List<BitmapImage>> imagepath;
        private MenuItem menuItem;
        private ImageSettings settings;
        private string settingsPath;
        private ImageSettingWindow winSetting;

        // 新的日志系统（现在使用静态Logger）

        // 旧的日志收集（保持兼容性）
        private List<string> logMessages = new List<string>();
        private const int MaxLogMessages = 1000; // 最多保存1000条日志

        // LLM情感分析组件
        private ILLMClient llmClient;
        private CacheManager cacheManager;
        private IEmotionAnalyzer emotionAnalyzer;
        private IVectorRetriever vectorRetriever;
        private ImageSelector imageSelector;
        private SpeechCapturer speechCapturer;

        // 气泡文本监听器
        private BubbleTextListener bubbleTextListener;

        // 标签图片匹配器
        private LabelImageMatcher labelImageMatcher;

        public ImageSettings Settings => settings;

        public ImageMgr(IMainWindow mainwin) : base(mainwin)
        {
            random = new Random();
            imagepath = new Dictionary<IGameSave.ModeType, List<BitmapImage>>();

            // 初始化设置
            InitializeSettings();
        }

        /// <summary>
        /// 初始化日志系统
        /// </summary>
        private void InitializeLogger()
        {
            try
            {
                string logPath = Path.Combine(LoaddllPath(), "VPet.Plugin.Image.log");
                Utils.Logger.LogFilePath = logPath;
                
                // 根据设置配置日志系统
                Utils.Logger.SetLogLevel((LogLevel)settings.LogLevel);
                Utils.Logger.EnableFileLogging = settings.EnableFileLogging;
                
                Utils.Logger.Info("Logger", "日志系统初始化完成");
                Utils.Logger.Debug("Logger", $"日志文件路径: {logPath}");
                Utils.Logger.Debug("Logger", $"日志等级: {(LogLevel)settings.LogLevel}");
                Utils.Logger.Debug("Logger", $"文件日志: {(settings.EnableFileLogging ? "启用" : "禁用")}");
            }
            catch (Exception ex)
            {
                // 如果日志系统初始化失败，回退到旧的日志方法
                LogMessage($"日志系统初始化失败: {ex.Message}");
            }
        }

        public override void LoadPlugin()
        {
            try
            {
                // 初始化日志系统
                InitializeLogger();
                
                Utils.Logger.Info("Plugin", "开始加载插件");

                // Create ImageUI
                image = new ImageUI(this);

                // Load images
                LoadImgae();

                // Create menu
                CreateMenu();

                // 添加设置菜单到MOD配置菜单
                AddSettingsToModConfig();

                // 初始化LLM情感分析系统
                InitializeEmotionAnalysis();

                // 初始化气泡文本监听器
                InitializeBubbleTextListener();

                // Create and setup timer if enabled (根据插件启用状态和时间触发开关)
                if (settings.IsEnabled && settings.UseTimeTrigger)
                {
                    StartTimer();
                }

                Utils.Logger.Info("Plugin", "插件加载完成");
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("Plugin", $"插件加载失败: {ex.Message}");
                Utils.Logger.Debug("Plugin", $"堆栈跟踪: {ex.StackTrace}");
            }
        }

        public override void Setting()
        {
            try
            {
                if (winSetting == null || !winSetting.IsLoaded)
                {
                    winSetting = new ImageSettingWindow(this);
                    winSetting.Closed += (s, e) => winSetting = null;

                    // 设置为主窗口的子窗口
                    if (MW is Window mainWindow)
                    {
                        winSetting.Owner = mainWindow;
                    }

                    winSetting.Show();
                    if (settings.DebugMode)
                        LogMessage("设置窗口已打开");
                }
                else
                {
                    winSetting.Activate();
                    if (settings.DebugMode)
                        LogMessage("设置窗口已激活");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"打开设置窗口失败: {ex.Message}");
            }
        }

        private void InitializeSettings()
        {
            try
            {
                string dllPath = LoaddllPath();
                settingsPath = Path.Combine(dllPath, "settings.json");
                settings = ImageSettings.LoadFromFile(settingsPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VPet表情包] 设置加载失败，使用默认设置: {ex.Message}");
                settings = new ImageSettings();
            }
        }

        private void StartTimer()
        {
            if (timer != null)
            {
                timer.Stop();
                timer = null;
            }

            timer = new DispatcherTimer();
            timer.Tick += Timer_Tick;
            SetRandomInterval();
            timer.Start();

            if (settings.DebugMode)
                LogMessage("定时器已启动");
        }

        private void StopTimer()
        {
            if (timer != null)
            {
                timer.Stop();
                timer = null;
                if (settings.DebugMode)
                    LogMessage("定时器已停止");
            }
        }

        public void ApplySettings(ImageSettings newSettings)
        {
            bool needReloadImages = settings.EnableBuiltInImages != newSettings.EnableBuiltInImages ||
                                   settings.EnableDIYImages != newSettings.EnableDIYImages;

            bool emotionAnalysisChanged = settings.EmotionAnalysis?.EnableLLMEmotionAnalysis != newSettings.EmotionAnalysis?.EnableLLMEmotionAnalysis;

            bool timeTriggerChanged = settings.UseTimeTrigger != newSettings.UseTimeTrigger;
            bool bubbleTriggerChanged = settings.UseBubbleTrigger != newSettings.UseBubbleTrigger ||
                                       settings.BubbleTriggerProbability != newSettings.BubbleTriggerProbability;

            settings = newSettings.Clone();

            // 如果表情包启用状态改变，重新加载图片
            if (needReloadImages)
            {
                LogMessage("表情包启用状态已更改，重新加载图片");
                LoadImgae();
            }

            // 如果情感分析启用状态改变，需要重新配置监听器
            if (emotionAnalysisChanged)
            {
                LogMessage("情感分析启用状态已更改，重新配置监听器");
                
                // 先清理现有的监听器
                CleanupBubbleTextListener();
                CleanupEmotionAnalysis();
                
                if (settings.EmotionAnalysis?.EnableLLMEmotionAnalysis == true)
                {
                    LogMessage("情感分析已启用，初始化LLM情感分析系统");
                    InitializeEmotionAnalysis();
                    // 不初始化 BubbleTextListener，让 SpeechCapturer 独占处理
                }
                else
                {
                    LogMessage("情感分析已禁用，初始化简单匹配监听器");
                    InitializeBubbleTextListener();
                    // 不初始化情感分析系统
                }
            }

            // 如果时间触发设置改变，重新配置定时器
            if (timeTriggerChanged)
            {
                LogMessage($"时间触发设置已更改：{(settings.UseTimeTrigger ? "启用" : "禁用")}");
            }

            // 如果气泡触发设置改变，记录日志
            if (bubbleTriggerChanged)
            {
                LogMessage($"气泡触发设置已更改：{(settings.UseBubbleTrigger ? $"启用（概率: {settings.BubbleTriggerProbability}%）" : "禁用")}");
            }

            // 配置时间触发器（根据插件启用状态和时间触发开关）
            if (settings.IsEnabled && settings.UseTimeTrigger)
            {
                StartTimer();
                LogMessage("时间触发器已启动");
            }
            else
            {
                StopTimer();
                if (!settings.IsEnabled)
                {
                    LogMessage("插件未启用，时间触发器已停止");
                }
                else if (!settings.UseTimeTrigger)
                {
                    LogMessage("时间触发已禁用，时间触发器已停止");
                }
            }

            LogMessage("设置已应用");
        }

        /// <summary>
        /// 插件卸载时的清理工作
        /// </summary>
        public void UnloadPlugin()
        {
            try
            {
                LogMessage("开始卸载插件");

                // 停止定时器
                StopTimer();

                // 清理气泡文本监听器
                CleanupBubbleTextListener();

                // 清理情感分析系统
                CleanupEmotionAnalysis();

                // 隐藏图片
                HideImage();

                LogMessage("插件卸载完成");
            }
            catch (Exception ex)
            {
                LogMessage($"插件卸载失败: {ex.Message}");
            }
        }

        public void SaveSettings()
        {
            try
            {
                settings.SaveToFile(settingsPath);
                LogMessage("设置已保存到文件");
            }
            catch (Exception ex)
            {
                LogMessage($"保存设置失败: {ex.Message}");
                throw;
            }
        }

        public string LoaddllPath()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var location = assembly.Location;
                var directory = Path.GetDirectoryName(location);
                return Path.GetDirectoryName(directory); // Get parent directory
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 记录日志（兼容旧版本）
        /// </summary>
        public void LogMessage(string message)
        {
            // 使用新的日志系统
            Utils.Logger.Info("Legacy", message);
            
            // 保持旧的日志收集（向后兼容）
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logMessage = $"[{timestamp}] {message}";

            // 输出到控制台
            Console.WriteLine($"[VPet LLM表情包] {logMessage}");

            // 添加到日志列表
            lock (logMessages)
            {
                logMessages.Add(logMessage);

                // 限制日志数量
                if (logMessages.Count > MaxLogMessages)
                {
                    logMessages.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// 记录Debug级别日志
        /// </summary>
        public void LogDebug(string category, string message)
        {
            Utils.Logger.Debug(category, message);
        }

        /// <summary>
        /// 记录Info级别日志
        /// </summary>
        public void LogInfo(string category, string message)
        {
            Utils.Logger.Info(category, message);
        }

        /// <summary>
        /// 记录Warning级别日志
        /// </summary>
        public void LogWarning(string category, string message)
        {
            Utils.Logger.Warning(category, message);
        }

        /// <summary>
        /// 记录Error级别日志
        /// </summary>
        public void LogError(string category, string message)
        {
            Utils.Logger.Error(category, message);
        }

        /// <summary>
        /// 获取所有日志（兼容旧版本）
        /// </summary>
        public List<string> GetLogMessages()
        {
            // 使用新的静态日志系统
            return Utils.Logger.GetFormattedLogs(LogLevel.Info);
        }

        /// <summary>
        /// 获取指定等级的日志
        /// </summary>
        public List<string> GetLogMessages(LogLevel minLevel)
        {
            return Utils.Logger.GetFormattedLogs(minLevel);
        }

        /// <summary>
        /// 清空日志
        /// </summary>
        public void ClearLogs()
        {
            Utils.Logger.Clear();
        }

        public override void LoadDIY()
        {
            try
            {
                LogMessage("LoadDIY 被调用");

                if (menuItem == null)
                {
                    LogMessage("错误：menuItem 为 null");
                    return;
                }

                LogMessage($"menuItem 不为 null，子菜单数量: {menuItem.Items.Count}");

                if (MW?.Main?.ToolBar?.MenuDIY == null)
                {
                    LogMessage("错误：MenuDIY 为 null");
                    return;
                }

                MW.Main.ToolBar.MenuDIY.Items.Add(menuItem);
                LogMessage($"菜单已添加到DIY工具栏，MenuDIY.Items.Count = {MW.Main.ToolBar.MenuDIY.Items.Count}");
            }
            catch (Exception ex)
            {
                LogMessage($"添加DIY菜单失败: {ex.Message}");
                LogMessage($"堆栈跟踪: {ex.StackTrace}");
            }
        }

        private void AddSettingsToModConfig()
        {
            try
            {
                if (MW?.Main?.ToolBar?.MenuMODConfig == null)
                {
                    LogMessage("错误：MenuMODConfig 为 null");
                    return;
                }

                // 添加设置菜单项到MOD配置菜单
                var settingsMenuItem = new MenuItem()
                {
                    Header = "LLM表情包设置",
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                settingsMenuItem.Click += (s, e) => Setting();

                // 设置可见性
                MW.Main.ToolBar.MenuMODConfig.Visibility = Visibility.Visible;

                // 添加菜单项
                MW.Main.ToolBar.MenuMODConfig.Items.Add(settingsMenuItem);

                LogMessage("设置菜单已添加到MOD配置");
            }
            catch (Exception ex)
            {
                LogMessage($"添加MOD配置菜单失败: {ex.Message}");
                LogMessage($"堆栈跟踪: {ex.StackTrace}");
            }
        }

        private void LoadImgae()
        {
            try
            {
                imagepath.Clear();

                string[] supportedFormats = { "*.png", "*.jpg", "*.gif", "*.jpeg" };
                string dllpath = LoaddllPath();

                // 加载内置表情包（VPet_Expression）
                // 注意：VPet_Expression 文件夹中的图片直接存放在根目录，没有心情子文件夹
                // 所以我们将所有图片加载到所有心情类别中，让它们在任何心情下都能显示
                if (settings.EnableBuiltInImages)
                {
                    string builtInPath = Path.Combine(dllpath, "VPet_Expression");
                    LogMessage($"开始加载内置表情包: {builtInPath}");

                    // 从根目录加载所有图片，并添加到所有心情类别
                    if (Directory.Exists(builtInPath))
                    {
                        var builtInImages = new List<BitmapImage>();
                        var allFiles = new List<string>();

                        // 收集所有支持格式的文件
                        foreach (string format in supportedFormats)
                        {
                            try
                            {
                                allFiles.AddRange(Directory.GetFiles(builtInPath, format, SearchOption.TopDirectoryOnly));
                            }
                            catch
                            {
                                // 忽略单个格式搜索的错误
                            }
                        }

                        // 加载每个图片文件
                        foreach (string filePath in allFiles)
                        {
                            // 跳过 info.lps 和 label.txt 等非图片文件
                            string fileName = Path.GetFileName(filePath).ToLower();
                            if (fileName == "info.lps" || fileName == "label.txt")
                                continue;

                            try
                            {
                                var bitmapImage = new BitmapImage();
                                bitmapImage.BeginInit();
                                bitmapImage.UriSource = new Uri(filePath, UriKind.Absolute);
                                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                                bitmapImage.EndInit();

                                builtInImages.Add(bitmapImage);
                            }
                            catch (Exception ex)
                            {
                                if (settings.DebugMode)
                                    LogMessage($"无法加载图片: {filePath}, 错误: {ex.Message}");
                            }
                        }

                        // 将内置图片添加到所有心情类别
                        if (builtInImages.Count > 0)
                        {
                            foreach (IGameSave.ModeType modeType in Enum.GetValues(typeof(IGameSave.ModeType)))
                            {
                                if (!imagepath.ContainsKey(modeType))
                                {
                                    imagepath[modeType] = new List<BitmapImage>();
                                }
                                imagepath[modeType].AddRange(builtInImages);
                            }
                            LogMessage($"内置表情包加载完成: {builtInImages.Count} 张图片（适用于所有心情）");
                        }
                        else
                        {
                            LogMessage("内置表情包目录为空或无有效图片");
                        }
                    }
                    else
                    {
                        LogMessage($"内置表情包目录不存在: {builtInPath}");
                    }
                }
                else
                {
                    LogMessage("内置表情包已禁用");
                }

                // 加载DIY表情包（DIY_Expression）
                // DIY表情包使用标准的心情子文件夹结构
                if (settings.EnableDIYImages)
                {
                    string diyPath = Path.Combine(dllpath, "DIY_Expression");
                    LogMessage($"开始加载DIY表情包: {diyPath}");
                    LoadImagesFromDirectory(Path.Combine(diyPath, "Normal"), IGameSave.ModeType.Nomal, supportedFormats);
                    LoadImagesFromDirectory(Path.Combine(diyPath, "Happy"), IGameSave.ModeType.Happy, supportedFormats);
                    LoadImagesFromDirectory(Path.Combine(diyPath, "PoorCondition"), IGameSave.ModeType.PoorCondition, supportedFormats);
                    LoadImagesFromDirectory(Path.Combine(diyPath, "Ill"), IGameSave.ModeType.Ill, supportedFormats);
                }
                else
                {
                    LogMessage("DIY表情包已禁用");
                }

                // 统计加载结果
                int totalImages = 0;
                foreach (var kvp in imagepath)
                {
                    totalImages += kvp.Value.Count;
                    LogMessage($"{kvp.Key} 心情: {kvp.Value.Count} 张图片");
                }

                LogMessage($"图片加载完成，共加载 {imagepath.Count} 个心情类别，总计 {totalImages} 张图片");
            }
            catch (Exception ex)
            {
                LogMessage($"加载图片时出错: {ex.Message}");
            }
        }

        private void LoadImagesFromDirectory(string directoryPath, IGameSave.ModeType modeType, string[] supportedFormats)
        {
            if (!Directory.Exists(directoryPath))
            {
                if (settings.DebugMode)
                    LogMessage($"目录不存在: {directoryPath}");
                return;
            }

            var imageList = new List<BitmapImage>();
            var allFiles = new List<string>();

            // Collect all files with supported formats
            foreach (string format in supportedFormats)
            {
                try
                {
                    allFiles.AddRange(Directory.GetFiles(directoryPath, format, SearchOption.TopDirectoryOnly));
                }
                catch
                {
                    // Ignore errors for individual format searches
                }
            }

            // Load each image file
            foreach (string filePath in allFiles)
            {
                try
                {
                    var bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.UriSource = new Uri(filePath, UriKind.Absolute);
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();

                    imageList.Add(bitmapImage);
                }
                catch (Exception ex)
                {
                    if (settings.DebugMode)
                        LogMessage($"无法加载图片: {filePath}, 错误: {ex.Message}");
                }
            }

            if (imageList.Count > 0)
            {
                // 如果该心情类别已有图片，追加而不是替换
                if (!imagepath.ContainsKey(modeType))
                {
                    imagepath[modeType] = new List<BitmapImage>();
                }
                imagepath[modeType].AddRange(imageList);

                if (settings.DebugMode)
                    LogMessage($"{modeType} 从 {directoryPath} 加载了 {imageList.Count} 张图片");
            }
        }

        private BitmapImage Return_Image(IGameSave.ModeType type)
        {
            try
            {
                LogDebug("ImageMgr", $"请求获取 {type} 心情的表情包");

                if (!imagepath.ContainsKey(type))
                {
                    LogWarning("ImageMgr", $"未找到 {type} 心情的表情包集合");
                    return null;
                }

                var imageList = imagepath[type];
                if (imageList == null || imageList.Count == 0)
                {
                    LogWarning("ImageMgr", $"{type} 心情的表情包集合为空");
                    return null;
                }

                LogDebug("ImageMgr", $"{type} 心情共有 {imageList.Count} 张表情包");

                int randomIndex = random.Next(imageList.Count);
                var selectedImage = imageList[randomIndex];

                LogDebug("ImageMgr", $"随机选择第 {randomIndex + 1} 张表情包");
                
                // 安全地获取图片路径（避免线程问题）
                try
                {
                    string imagePath = selectedImage?.UriSource?.ToString() ?? "未知";
                    LogDebug("ImageMgr", $"选中的表情包路径: {imagePath}");
                }
                catch (Exception ex)
                {
                    LogDebug("ImageMgr", $"获取图片路径时出现线程问题: {ex.Message}");
                    LogDebug("ImageMgr", $"继续返回图片对象");
                }

                return selectedImage;
            }
            catch (Exception ex)
            {
                LogError("ImageMgr", $"获取 {type} 心情表情包失败: {ex.Message}");
                LogDebug("ImageMgr", $"错误堆栈: {ex.StackTrace}");
                return null;
            }
        }

        private bool IsGifImage(BitmapImage image)
        {
            if (image?.UriSource == null)
                return false;

            string extension = Path.GetExtension(image.UriSource.ToString()).ToLower();
            return extension == ".gif";
        }

        private void DisplayImage(BitmapImage imageToShow)
        {
            try
            {
                if (imageToShow == null)
                {
                    LogWarning("ImageMgr", "传入的图片为空，无法显示");
                    return;
                }

                if (image?.Image == null)
                {
                    LogWarning("ImageMgr", "UI组件未初始化，无法显示图片");
                    return;
                }

                LogDebug("ImageMgr", "开始显示表情包");
                LogDebug("ImageMgr", $"图片路径: {imageToShow.UriSource?.ToString() ?? "未知"}");
                LogDebug("ImageMgr", $"图片尺寸: {imageToShow.PixelWidth}x{imageToShow.PixelHeight}");

                image.Visibility = Visibility.Visible;
                LogDebug("ImageMgr", "UI组件已设置为可见");

                if (IsGifImage(imageToShow))
                {
                    LogDebug("ImageMgr", "检测到GIF动画，使用动画显示模式");
                    // For GIF images
                    ImageBehavior.SetAnimatedSource(image.Image, imageToShow);
                    image.Image.Source = null;
                    LogDebug("ImageMgr", "GIF动画设置完成");
                }
                else
                {
                    LogDebug("ImageMgr", "检测到静态图片，使用静态显示模式");
                    // For static images
                    ImageBehavior.SetAnimatedSource(image.Image, null);
                    image.Image.Source = imageToShow;
                    LogDebug("ImageMgr", "静态图片设置完成");
                }

                LogInfo("ImageMgr", "表情包显示成功");
            }
            catch (Exception ex)
            {
                LogError("ImageMgr", $"显示表情包失败: {ex.Message}");
                LogDebug("ImageMgr", $"错误堆栈: {ex.StackTrace}");
            }
        }

        private void HideImage()
        {
            try
            {
                if (image?.Image == null)
                {
                    LogDebug("ImageMgr", "UI组件未初始化，无需隐藏");
                    return;
                }

                LogDebug("ImageMgr", "开始隐藏表情包");

                image.Visibility = Visibility.Collapsed;
                ImageBehavior.SetAnimatedSource(image.Image, null);
                image.Image.Source = null;

                LogInfo("ImageMgr", "表情包已隐藏");
            }
            catch (Exception ex)
            {
                LogError("ImageMgr", $"隐藏表情包失败: {ex.Message}");
                LogDebug("ImageMgr", $"错误堆栈: {ex.StackTrace}");
            }
        }

        private void SetRandomInterval()
        {
            if (timer == null)
                return;

            int intervalMs = settings.GetDisplayIntervalMs();
            timer.Interval = TimeSpan.FromMilliseconds(intervalMs);

            if (settings.UseRandomInterval)
            {
                int minutes = intervalMs / (60 * 1000);
                LogDebug("ImageMgr", $"设置随机定时器间隔: {minutes} 分钟");
            }
            else
            {
                LogDebug("ImageMgr", $"设置固定定时器间隔: {settings.DisplayInterval} 分钟");
            }
        }

        private async void Timer_Tick(object sender, EventArgs e)
        {
            try
            {
                LogDebug("ImageMgr", "=== 定时器触发 ===");
                timer?.Stop();

                // Check if plugin is enabled
                if (!settings.IsEnabled)
                {
                    LogDebug("ImageMgr", "插件未启用，跳过显示");
                    SetRandomInterval();
                    timer?.Start();
                    return;
                }

                // Check if time trigger is enabled
                if (!settings.UseTimeTrigger)
                {
                    LogDebug("ImageMgr", "时间触发已禁用，停止定时器");
                    return; // Don't restart timer when time trigger is disabled
                }

                // Get current pet mood
                var currentMode = MW.Core.Save.CalMode();
                LogDebug("ImageMgr", $"当前宠物心情: {currentMode}");

                var imageToShow = Return_Image(currentMode);

                if (imageToShow != null)
                {
                    LogInfo("ImageMgr", $"定时器显示 {currentMode} 心情表情包");
                    DisplayImage(imageToShow);

                    LogDebug("ImageMgr", $"表情包将显示 {settings.GetDisplayDurationMs()}ms");
                    // Auto-hide after configured duration
                    await Task.Delay(settings.GetDisplayDurationMs());
                    HideImage();
                    LogDebug("ImageMgr", "表情包显示周期完成");
                }
                else
                {
                    LogWarning("ImageMgr", $"未找到 {currentMode} 心情的表情包，跳过显示");
                }

                // Restart timer with new random interval (only if time trigger is still enabled)
                if (settings.UseTimeTrigger)
                {
                    SetRandomInterval();
                    timer?.Start();
                }
                LogDebug("ImageMgr", "=== 定时器周期完成 ===");
            }
            catch (Exception ex)
            {
                LogError("ImageMgr", $"定时器事件出错: {ex.Message}");
                LogDebug("ImageMgr", $"定时器错误堆栈: {ex.StackTrace}");
                // Restart timer even on error (only if time trigger is enabled)
                if (settings.UseTimeTrigger)
                {
                    SetRandomInterval();
                    timer?.Start();
                }
            }
        }

        private void CreateMenu()
        {
            try
            {
                LogMessage("开始创建菜单");

                // Create main menu item
                menuItem = new MenuItem()
                {
                    Header = "LLM表情包互动",
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };

                // Create submenu for manual trigger
                var manualTrigger = new MenuItem()
                {
                    Header = "随机发送表情",
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
                manualTrigger.Click += (s, e) => ShowRandomImageImmediately();

                // Create submenu for settings
                var settingsMenu = new MenuItem()
                {
                    Header = "插件设置",
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
                settingsMenu.Click += (s, e) => Setting();

                menuItem.Items.Add(manualTrigger);
                menuItem.Items.Add(new Separator());
                menuItem.Items.Add(settingsMenu);

                LogMessage($"菜单创建完成，子菜单数量: {menuItem.Items.Count}");
            }
            catch (Exception ex)
            {
                LogMessage($"创建菜单失败: {ex.Message}");
            }
        }

        private async void ShowRandomImageImmediately()
        {
            try
            {
                LogMessage("=== 手动触发表情包显示 ===");
                timer?.Stop();

                var currentMode = MW.Core.Save.CalMode();
                LogMessage($"手动显示: 当前宠物心情: {currentMode}");

                var imageToShow = Return_Image(currentMode);

                if (imageToShow != null)
                {
                    LogMessage($"手动显示: 准备显示 {currentMode} 心情表情包");
                    DisplayImage(imageToShow);

                    LogMessage($"手动显示: 表情包将显示 {settings.GetDisplayDurationMs()}ms");
                    // Auto-hide after configured duration
                    await Task.Delay(settings.GetDisplayDurationMs());
                    HideImage();
                    LogMessage("手动显示: 表情包显示完成");
                }
                else
                {
                    LogMessage($"手动显示: 未找到 {currentMode} 心情的表情包");
                }

                // Restart timer
                SetRandomInterval();
                timer?.Start();
                LogMessage("=== 手动显示周期完成 ===");
            }
            catch (Exception ex)
            {
                LogMessage($"手动显示出错: {ex.Message}");
                LogMessage($"手动显示错误堆栈: {ex.StackTrace}");
                SetRandomInterval();
                timer?.Start();
            }
        }

        /// <summary>
        /// 初始化LLM情感分析系统
        /// </summary>
        private void InitializeEmotionAnalysis()
        {
            try
            {
                if (settings?.EmotionAnalysis == null || !settings.EmotionAnalysis.EnableLLMEmotionAnalysis)
                {
                    LogMessage("LLM情感分析功能未启用");
                    return;
                }

                LogMessage("开始初始化LLM情感分析系统");

                // 创建LLM客户端
                llmClient = CreateLLMClient();
                if (llmClient == null)
                {
                    LogMessage("LLM客户端创建失败");
                    return;
                }

                // 创建缓存管理器
                string dllPath = LoaddllPath();
                string cachePath = Path.Combine(dllPath, "emotion_cache.json");
                cacheManager = new CacheManager(cachePath);
                cacheManager.Load();
                LogMessage($"缓存管理器已加载: {cachePath}");

                // 加载情感标签参考文件路径
                string emotionLabelsPath = Path.Combine(dllPath, "plugin", "data", "emotion_labels.json");
                if (File.Exists(emotionLabelsPath))
                {
                    LogMessage($"找到情感标签参考文件: {emotionLabelsPath}");
                }
                else
                {
                    LogMessage($"警告：情感标签参考文件不存在: {emotionLabelsPath}");
                    emotionLabelsPath = null;
                }

                // 创建情感分析器（传入ImageMgr参数以支持标签匹配）
                emotionAnalyzer = new EmotionAnalyzer(llmClient, cacheManager, MW, this, emotionLabelsPath);
                LogMessage("情感分析器已创建");

                // 创建标签图片匹配器
                labelImageMatcher = new LabelImageMatcher(this);
                labelImageMatcher.LoadLabels();
                LogMessage("标签图片匹配器已创建并加载标签");

                // 创建向量检索器
                vectorRetriever = new VectorRetriever(llmClient);

                // 加载标签文件
                LoadEmotionLabels(dllPath);

                // 创建图片选择器
                imageSelector = new ImageSelector(this, vectorRetriever);
                imageSelector.BuildImagePathCache();
                LogMessage("图片选择器已创建");

                // 创建语音捕获器
                speechCapturer = new SpeechCapturer(MW, emotionAnalyzer, imageSelector, settings, this);
                speechCapturer.Initialize();
                LogMessage("语音捕获器已初始化");

                LogMessage("LLM情感分析系统初始化完成");
            }
            catch (Exception ex)
            {
                LogMessage($"初始化LLM情感分析系统失败: {ex.Message}");
                LogMessage($"堆栈跟踪: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 创建LLM客户端
        /// </summary>
        private ILLMClient CreateLLMClient()
        {
            try
            {
                var config = settings.EmotionAnalysis;

                switch (config.Provider)
                {
                    case LLMProvider.OpenAI:
                        if (string.IsNullOrWhiteSpace(config.OpenAIApiKey))
                        {
                            LogMessage("OpenAI API Key未配置");
                            return null;
                        }
                        LogMessage("使用OpenAI客户端");
                        string openaiBaseUrl = string.IsNullOrWhiteSpace(config.OpenAIBaseUrl)
                            ? "https://api.openai.com/v1"
                            : config.OpenAIBaseUrl;
                        string openaiModel = string.IsNullOrWhiteSpace(config.OpenAIModel)
                            ? "gpt-3.5-turbo"
                            : config.OpenAIModel;
                        string openaiEmbeddingModel = string.IsNullOrWhiteSpace(config.OpenAIEmbeddingModel)
                            ? "text-embedding-3-small"
                            : config.OpenAIEmbeddingModel;
                        return new OpenAIClient(config.OpenAIApiKey, openaiBaseUrl, openaiModel, openaiEmbeddingModel, this);

                    case LLMProvider.Gemini:
                        if (string.IsNullOrWhiteSpace(config.GeminiApiKey))
                        {
                            LogMessage("Gemini API Key未配置");
                            return null;
                        }
                        LogMessage("使用Gemini客户端");
                        string geminiBaseUrl = string.IsNullOrWhiteSpace(config.GeminiBaseUrl)
                            ? "https://generativelanguage.googleapis.com/v1beta"
                            : config.GeminiBaseUrl;
                        string geminiModel = string.IsNullOrWhiteSpace(config.GeminiModel)
                            ? "gemini-pro"
                            : config.GeminiModel;
                        string geminiEmbeddingModel = string.IsNullOrWhiteSpace(config.GeminiEmbeddingModel)
                            ? "embedding-001"
                            : config.GeminiEmbeddingModel;
                        return new GeminiClient(config.GeminiApiKey, geminiBaseUrl, geminiModel, geminiEmbeddingModel, this);

                    case LLMProvider.Ollama:
                        string ollamaBaseUrl = string.IsNullOrWhiteSpace(config.OllamaBaseUrl)
                            ? "http://localhost:11434"
                            : config.OllamaBaseUrl;
                        string ollamaModel = string.IsNullOrWhiteSpace(config.OllamaModel)
                            ? "llama2"
                            : config.OllamaModel;
                        LogMessage($"使用Ollama客户端: {ollamaBaseUrl}, 模型: {ollamaModel}");
                        return new OllamaClient(ollamaBaseUrl, ollamaModel, this);

                    default:
                        LogMessage($"未知的LLM提供商: {config.Provider}");
                        return null;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"创建LLM客户端失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 加载情感标签
        /// </summary>
        private void LoadEmotionLabels(string dllPath)
        {
            try
            {
                // 加载VPet_Expression的标签（仅JSON格式）
                string builtInJsonPath = Path.Combine(dllPath, "VPet_Expression", "label.json");

                if (File.Exists(builtInJsonPath))
                {
                    vectorRetriever.LoadLabels(builtInJsonPath);
                    LogMessage($"已加载内置表情标签: {builtInJsonPath}");
                }
                else
                {
                    LogMessage($"内置表情标签文件不存在: {builtInJsonPath}");
                }

                // 加载DIY_Expression的标签（仅JSON格式）
                string diyJsonPath = Path.Combine(dllPath, "DIY_Expression", "label.json");

                if (File.Exists(diyJsonPath))
                {
                    vectorRetriever.LoadLabels(diyJsonPath);
                    LogMessage($"已加载DIY表情标签: {diyJsonPath}");
                }
                else
                {
                    LogMessage($"DIY表情标签文件不存在: {diyJsonPath}");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"加载情感标签失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理LLM情感分析系统
        /// </summary>
        private void CleanupEmotionAnalysis()
        {
            try
            {
                // 注销语音捕获器
                if (speechCapturer != null)
                {
                    speechCapturer.Cleanup();
                    speechCapturer = null;
                    LogMessage("语音捕获器已清理");
                }

                // 保存缓存
                if (cacheManager != null)
                {
                    cacheManager.Save();
                    cacheManager = null;
                    LogMessage("缓存已保存");
                }

                // 清理其他组件
                labelImageMatcher = null;
                imageSelector = null;
                vectorRetriever = null;
                emotionAnalyzer = null;
                llmClient = null;

                LogMessage("LLM情感分析系统已清理");
            }
            catch (Exception ex)
            {
                LogMessage($"清理LLM情感分析系统失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 测试显示表情包（用于设置窗口的测试按钮）
        /// </summary>
        public async void TestDisplayImage()
        {
            try
            {
                LogMessage("=== 测试显示表情包 ===");
                timer?.Stop();

                var currentMode = MW.Core.Save.CalMode();
                LogMessage($"测试显示: 当前宠物心情: {currentMode}");

                var imageToShow = Return_Image(currentMode);

                if (imageToShow != null)
                {
                    LogMessage($"测试显示: 找到 {currentMode} 心情表情包，开始显示");
                    DisplayImage(imageToShow);

                    LogMessage($"测试显示: 表情包将显示 {settings.GetDisplayDurationMs()}ms");
                    // Auto-hide after configured duration
                    await Task.Delay(settings.GetDisplayDurationMs());
                    HideImage();
                    LogMessage("测试显示: 表情包显示完成");
                }
                else
                {
                    LogMessage($"测试显示失败：{currentMode} 心情没有可用的图片");
                    
                    // 显示详细的表情包库状态
                    LogMessage("测试显示: 当前表情包库状态:");
                    foreach (var kvp in imagepath)
                    {
                        LogMessage($"  - {kvp.Key}: {kvp.Value?.Count ?? 0} 张");
                    }
                }

                // Restart timer if enabled
                if (settings.IsEnabled)
                {
                    SetRandomInterval();
                    timer?.Start();
                    LogMessage("测试显示: 定时器已重新启动");
                }
                else
                {
                    LogMessage("测试显示: 插件未启用，定时器保持停止状态");
                }
                
                LogMessage("=== 测试显示完成 ===");
            }
            catch (Exception ex)
            {
                LogMessage($"测试显示出错: {ex.Message}");
                LogMessage($"测试显示错误堆栈: {ex.StackTrace}");
                if (settings.IsEnabled)
                {
                    SetRandomInterval();
                    timer?.Start();
                }
            }
        }

        /// <summary>
        /// 初始化气泡文本监听器
        /// </summary>
        private void InitializeBubbleTextListener()
        {
            try
            {
                LogMessage("开始初始化气泡文本监听器");

                // 检查是否启用了LLM情感分析
                if (settings?.EmotionAnalysis?.EnableLLMEmotionAnalysis == true)
                {
                    LogMessage("检测到LLM情感分析已启用，将由 SpeechCapturer 独占处理气泡文本");
                    LogMessage("跳过 BubbleTextListener 初始化，避免重复监听");
                    return;
                }

                LogMessage("LLM情感分析未启用，初始化 BubbleTextListener 进行简单匹配处理");

                // 创建监听器
                bubbleTextListener = new BubbleTextListener(MW, this);

                // 订阅文本捕获事件
                bubbleTextListener.TextCaptured += OnBubbleTextCaptured;

                // 初始化监听器
                bubbleTextListener.Initialize();

                LogMessage("气泡文本监听器初始化完成");
            }
            catch (Exception ex)
            {
                LogMessage($"初始化气泡文本监听器失败: {ex.Message}");
                LogMessage($"堆栈跟踪: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 处理捕获到的气泡文本（仅在未启用LLM情感分析时使用）
        /// </summary>
        private async void OnBubbleTextCaptured(object sender, string text)
        {
            try
            {
                LogMessage("=== BubbleTextListener 开始处理气泡文本 ===");
                
                if (string.IsNullOrWhiteSpace(text))
                {
                    LogMessage("BubbleTextListener: 文本为空或空白，跳过处理");
                    return;
                }

                // 记录文本信息
                string textPreview = text.Length > 100 ? text.Substring(0, 100) + "..." : text;
                LogMessage($"BubbleTextListener: 接收到文本 [长度: {text.Length}]");
                LogMessage($"BubbleTextListener: 内容预览: {textPreview}");

                // 检查插件是否启用
                if (!settings.IsEnabled)
                {
                    LogMessage("BubbleTextListener: 插件未启用，跳过处理");
                    return;
                }

                // 处理气泡概率触发（如果启用气泡触发模式）
                if (settings.UseBubbleTrigger)
                {
                    LogMessage($"BubbleTextListener: 气泡触发已启用，概率: {settings.BubbleTriggerProbability}%");
                    await HandleBubbleProbabilityTrigger();
                }
                else
                {
                    LogMessage("BubbleTextListener: 气泡触发已禁用，跳过概率触发");
                }

                // 注意：此方法只在未启用LLM情感分析时才会被调用
                // 使用简单的关键词匹配逻辑（与气泡触发并行工作）
                LogMessage("BubbleTextListener: 使用简单关键词匹配处理");
                await ProcessTextWithSimpleMatching(text);
                
                LogMessage("=== BubbleTextListener 气泡文本处理完成 ===");
            }
            catch (Exception ex)
            {
                LogMessage($"BubbleTextListener 处理气泡文本失败: {ex.Message}");
                LogMessage($"BubbleTextListener 错误堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 使用简单关键词匹配处理文本
        /// </summary>
        private async Task ProcessTextWithSimpleMatching(string text)
        {
            try
            {
                LogMessage("--- 开始简单关键词匹配处理 ---");
                
                // 简单的关键词匹配示例
                // 可以根据需要扩展更复杂的匹配逻辑

                IGameSave.ModeType currentMode = MW.Core.Save.CalMode();
                IGameSave.ModeType targetMode = currentMode; // 默认使用当前心情
                
                LogMessage($"简单匹配: 当前宠物心情: {currentMode}");
                LogMessage($"简单匹配: 开始分析文本关键词");

                // 根据文本内容判断情感倾向
                string lowerText = text.ToLower();
                bool foundKeyword = false;
                string matchedKeywords = "";

                if (lowerText.Contains("开心") || lowerText.Contains("高兴") || lowerText.Contains("快乐") || 
                    lowerText.Contains("哈哈") || lowerText.Contains("嘻嘻"))
                {
                    targetMode = IGameSave.ModeType.Happy;
                    foundKeyword = true;
                    matchedKeywords = "开心相关关键词";
                }
                else if (lowerText.Contains("难过") || lowerText.Contains("伤心") || lowerText.Contains("哭") ||
                         lowerText.Contains("不开心") || lowerText.Contains("郁闷"))
                {
                    targetMode = IGameSave.ModeType.PoorCondition;
                    foundKeyword = true;
                    matchedKeywords = "难过相关关键词";
                }
                else if (lowerText.Contains("生病") || lowerText.Contains("不舒服") || lowerText.Contains("头疼") ||
                         lowerText.Contains("感冒") || lowerText.Contains("发烧"))
                {
                    targetMode = IGameSave.ModeType.Ill;
                    foundKeyword = true;
                    matchedKeywords = "生病相关关键词";
                }

                if (foundKeyword)
                {
                    LogMessage($"简单匹配: 匹配到 {matchedKeywords}，目标心情: {targetMode}");
                }
                else
                {
                    LogMessage($"简单匹配: 未匹配到特定关键词，使用当前心情: {targetMode}");
                }

                // 显示对应心情的表情包
                var imageToShow = Return_Image(targetMode);
                if (imageToShow != null)
                {
                    LogMessage($"简单匹配: 找到 {targetMode} 心情的表情包，开始显示");
                    LogMessage($"简单匹配: 表情包路径: {imageToShow.UriSource?.ToString() ?? "未知"}");
                    
                    DisplayImage(imageToShow);
                    LogMessage($"简单匹配: 表情包显示成功，将在 {settings.GetDisplayDurationMs()}ms 后自动隐藏");

                    // 自动隐藏
                    await Task.Delay(settings.GetDisplayDurationMs());
                    HideImage();
                    LogMessage("简单匹配: 表情包已自动隐藏");
                }
                else
                {
                    LogMessage($"简单匹配: 未找到 {targetMode} 心情的表情包");
                    
                    // 统计各心情的表情包数量
                    LogMessage("简单匹配: 当前表情包库状态:");
                    foreach (var kvp in imagepath)
                    {
                        LogMessage($"  - {kvp.Key}: {kvp.Value?.Count ?? 0} 张");
                    }
                }
                
                LogMessage("--- 简单关键词匹配处理完成 ---");
            }
            catch (Exception ex)
            {
                LogMessage($"简单匹配处理失败: {ex.Message}");
                LogMessage($"简单匹配错误堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 清理气泡文本监听器
        /// </summary>
        private void CleanupBubbleTextListener()
        {
            try
            {
                if (bubbleTextListener != null)
                {
                    bubbleTextListener.TextCaptured -= OnBubbleTextCaptured;
                    bubbleTextListener.Cleanup();
                    bubbleTextListener = null;
                    LogMessage("气泡文本监听器已清理");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"清理气泡文本监听器失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 公共方法：显示指定的表情包（供 ImageSelector 调用）
        /// </summary>
        public void DisplayImagePublic(BitmapImage imageToShow)
        {
            try
            {
                // 确保在UI线程中执行
                if (!Application.Current.Dispatcher.CheckAccess())
                {
                    LogMessage("ImageSelector 请求显示表情包（切换到UI线程）");
                    Application.Current.Dispatcher.Invoke(() => DisplayImagePublic(imageToShow));
                    return;
                }

                LogMessage("ImageSelector 请求显示表情包");
                DisplayImage(imageToShow);
            }
            catch (Exception ex)
            {
                LogMessage($"ImageSelector 显示表情包失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 公共方法：隐藏表情包（供 ImageSelector 调用）
        /// </summary>
        public void HideImagePublic()
        {
            try
            {
                // 确保在UI线程中执行
                if (!Application.Current.Dispatcher.CheckAccess())
                {
                    LogMessage("ImageSelector 请求隐藏表情包（切换到UI线程）");
                    Application.Current.Dispatcher.Invoke(() => HideImagePublic());
                    return;
                }

                LogMessage("ImageSelector 请求隐藏表情包");
                HideImage();
            }
            catch (Exception ex)
            {
                LogMessage($"ImageSelector 隐藏表情包失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 公共方法：获取当前心情的随机图片（供 ImageSelector 调用）
        /// </summary>
        public BitmapImage GetCurrentMoodImagePublic()
        {
            try
            {
                // 确保在UI线程中执行
                if (!Application.Current.Dispatcher.CheckAccess())
                {
                    LogMessage("ImageMgr: 切换到UI线程获取当前心情图片");
                    return Application.Current.Dispatcher.Invoke(() => GetCurrentMoodImagePublic());
                }

                var currentMode = MW.Core.Save.CalMode();
                LogMessage($"ImageMgr: 为 ImageSelector 获取 {currentMode} 心情的随机图片");
                return Return_Image(currentMode);
            }
            catch (Exception ex)
            {
                LogMessage($"ImageMgr: 获取当前心情图片失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 处理气泡概率触发
        /// </summary>
        private async Task HandleBubbleProbabilityTrigger()
        {
            try
            {
                if (!settings.IsEnabled)
                {
                    LogMessage("气泡概率触发: 插件未启用，跳过处理");
                    return;
                }

                if (!settings.UseBubbleTrigger)
                {
                    LogMessage("气泡概率触发: 气泡触发已禁用，跳过处理");
                    return;
                }

                LogMessage($"气泡概率触发: 检查概率 {settings.BubbleTriggerProbability}%");

                if (settings.ShouldTriggerBubble())
                {
                    LogMessage("=== 气泡概率触发：命中概率，显示表情包 ===");
                    
                    // 显示表情包
                    var currentMode = MW.Core.Save.CalMode();
                    var imageToShow = Return_Image(currentMode);

                    if (imageToShow != null)
                    {
                        LogMessage($"气泡概率触发: 显示 {currentMode} 心情表情包");
                        DisplayImage(imageToShow);

                        // 自动隐藏
                        await Task.Delay(settings.GetDisplayDurationMs());
                        HideImage();
                        LogMessage("气泡概率触发: 表情包显示完成");
                    }
                    else
                    {
                        LogMessage($"气泡概率触发: 未找到 {currentMode} 心情的表情包");
                    }

                    LogMessage("=== 气泡概率触发周期完成 ===");
                }
                else
                {
                    LogMessage("气泡概率触发: 未命中概率，跳过显示");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"处理气泡概率触发失败: {ex.Message}");
                LogMessage($"气泡概率触发错误堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 供 SpeechCapturer 调用的气泡概率触发处理方法
        /// </summary>
        public async void HandleBubbleProbabilityFromSpeechCapturer()
        {
            try
            {
                LogMessage("SpeechCapturer 请求处理气泡概率触发");
                await HandleBubbleProbabilityTrigger();
            }
            catch (Exception ex)
            {
                LogMessage($"SpeechCapturer 气泡概率触发处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取标签图片匹配器（供 EmotionAnalyzer 调用）
        /// </summary>
        public LabelImageMatcher GetLabelImageMatcher()
        {
            return labelImageMatcher;
        }
    }
}