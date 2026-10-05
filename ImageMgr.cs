using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VPet.Plugin.LLMEP.Core;
using VPet.Plugin.LLMEP.EmotionAnalysis;
using VPet.Plugin.LLMEP.EmotionAnalysis.LLMClient;
using VPet.Plugin.LLMEP.Services;
using VPet.Plugin.LLMEP.Utils;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.LLMEP
{
    public class ImageMgr : MainPlugin
    {
        public override string PluginName => "LLM表情包";

        private DispatcherTimer timer;
        private Random random;
        private ImageUI image;
        // 只存路径；LoadImgae 整体替换，后台线程读到的总是一份完整的库
        private volatile Dictionary<IGameSave.ModeType, List<string>> imagepath;
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

        // 在线表情包管理器
        private OnlineStickerManager onlineStickerManager;

        // 用于版本控制的设置副本
        private ImageSettings previousSettings;

        // 独占会话协调器（供外部插件如 StickerPlugin 使用）
        private ImagePluginCoordinator imageCoordinator;

        // 定时器用于清理超时会话
        private DispatcherTimer sessionCleanupTimer;

        public ImageSettings Settings => settings;

        /// <summary>
        /// 图片插件协调器（供外部插件访问）
        /// </summary>
        public IImagePluginCoordinator ImageCoordinator => imageCoordinator;

        public ImageMgr(IMainWindow mainwin) : base(mainwin)
        {
            random = new Random();
            imagepath = new Dictionary<IGameSave.ModeType, List<string>>();

            // 官方服务（在线表情包 / Free 模型）的鉴权通道：身份与加解密都在
            // 本 MOD 自带的 VPetLLM.SecureCommunication.dll 里完成。
            AuthenticatedServiceTransport.Initialize();
            // 状态里带原因（缺原生组件 / 架构不匹配），直接记下来省得回头再猜
            Logger.Info("ImageMgr", $"官方服务鉴权通道: {AuthenticatedServiceTransport.StatusMessage}");

            // 设置、情感分析缓存、日志从 MOD 目录迁到 文档\VPetLLM\Emotion\data。
            // 必须在读取设置之前：以前的迁移放在 LoadPlugin 里，比读设置晚，迁移那次启动读到的是默认设置
            DiyStickerStorage.MigratePluginData(LoaddllPath());

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
                string dataPath = GetDataDirectoryPath();
                string logPath = Path.Combine(dataPath, "VPet.Plugin.LLMEP.log");
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

                // DIY 表情包从 MOD 目录迁到 文档\VPetLLM\Emotion\（一次性；工坊目录会被 Steam 更新覆盖）
                DiyStickerStorage.Migrate(LoaddllPath());

                Utils.Logger.Info("Plugin", "开始加载插件");

                // 挂接退出事件。
                //
                // 这个插件此前没有任何关闭钩子：UnloadPlugin() 没有调用点（死代码），
                // 也没有重写 Save()。后果有两个——设置窗口在退出时不会被关闭，
                // 以及情感分析缓存的最后一批改动来不及落盘。
                if (Application.Current is not null)
                {
                    Application.Current.Exit += OnApplicationExit;
                }

                // Create ImageUI
                image = new ImageUI(this);

                // Load images
                LoadImgae();

                // Create menu
                CreateMenu();

                // 添加设置菜单到MOD配置菜单
                AddSettingsToModConfig();

                // 初始化独占会话协调器
                InitializeImageCoordinator();

                // 初始化LLM情感分析系统
                InitializeEmotionAnalysis();

                // 初始化气泡文本监听器
                InitializeBubbleTextListener();

                // 初始化在线表情包管理器
                InitializeOnlineStickerManager();

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
                    try
                    {
                        LogMessage("正在创建设置窗口...");
                        winSetting = new ImageSettingWindow(this);
                        LogMessage("设置窗口创建成功");

                        winSetting.Closed += (s, e) => winSetting = null;

                        // 设置为主窗口的子窗口
                        if (MW is Window mainWindow)
                        {
                            winSetting.Owner = mainWindow;
                            LogMessage("设置窗口Owner已设置");
                        }

                        winSetting.Show();
                        LogMessage("设置窗口已显示");

                        if (settings.DebugMode)
                            LogMessage("设置窗口已打开");
                    }
                    catch (Exception createEx)
                    {
                        LogMessage($"创建设置窗口失败: {createEx.Message}");
                        LogMessage($"堆栈跟踪: {createEx.StackTrace}");

                        // 显示详细错误信息给用户
                        System.Windows.MessageBox.Show(
                            $"无法打开设置窗口。\n\n错误信息: {createEx.Message}\n\n堆栈跟踪:\n{createEx.StackTrace}",
                            "设置窗口错误",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Error);
                        return;
                    }
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
                LogMessage($"外层异常堆栈跟踪: {ex.StackTrace}");
            }
        }

        private void InitializeSettings()
        {
            try
            {
                string dataPath = GetDataDirectoryPath();
                settingsPath = Path.Combine(dataPath, "settings.json");
                settings = ImageSettings.LoadFromFile(settingsPath);

                // 初始化设置副本用于版本控制
                previousSettings = settings.Clone();
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"[VPet表情包] 设置加载失败，使用默认设置: {ex.Message}");
                settings = new ImageSettings();
                previousSettings = settings.Clone();
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

            // 检查LLM提供商是否改变
            var oldProvider = settings.EmotionAnalysis?.Provider;
            var newProvider = newSettings.EmotionAnalysis?.Provider;
            bool llmProviderChanged = oldProvider != newProvider;

            LogMessage($"ApplySettings: 旧提供商={oldProvider}, 新提供商={newProvider}, 提供商改变={llmProviderChanged}");

            bool timeTriggerChanged = settings.UseTimeTrigger != newSettings.UseTimeTrigger;
            bool bubbleTriggerChanged = settings.UseBubbleTrigger != newSettings.UseBubbleTrigger ||
                                       settings.BubbleTriggerProbability != newSettings.BubbleTriggerProbability;

            // 检查在线表情包设置是否改变
            bool onlineStickerChanged = !settings.OnlineSticker.Equals(newSettings.OnlineSticker);

            settings = newSettings.Clone();

            // 如果表情包启用状态改变，重新加载图片
            if (needReloadImages)
            {
                LogMessage("表情包启用状态已更改，重新加载图片");
                LoadImgae();
            }

            // 如果情感分析启用状态改变或提供商改变，需要重新配置监听器
            if (emotionAnalysisChanged || llmProviderChanged)
            {
                if (emotionAnalysisChanged)
                {
                    LogMessage("情感分析启用状态已更改，重新配置监听器");
                }
                if (llmProviderChanged)
                {
                    LogMessage($"LLM提供商已更改: {newSettings.EmotionAnalysis?.Provider}，重新初始化情感分析系统");
                }

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

            // 如果在线表情包设置改变，更新配置
            if (onlineStickerChanged)
            {
                LogMessage("在线表情包设置已更改，更新配置");
                UpdateOnlineStickerManager();
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
        /// VPet 退出时的收尾。挂在 Application.Exit 上。
        /// </summary>
        private void OnApplicationExit(object sender, ExitEventArgs e)
        {
            UnloadPlugin();
        }

        /// <summary>
        /// 关闭插件自己开的窗口。退出路径专用，单个窗口关闭失败不能中断后续清理。
        /// </summary>
        private void CloseOwnedWindows()
        {
            try
            {
                if (winSetting != null)
                {
                    // 这个窗口的 Owner 是 VPet 主窗口：退出时主窗口 HWND 一销毁，
                    // Win32 会连带把它干掉（绕过 WPF），等到 Dispatcher.ShutdownFinished
                    // WPF 再去 DestroyWindow 就会抛 Win32Exception(1400) 无效窗口句柄。
                    // 主动 Close() 让 HwndSource 在句柄还有效时有序释放。
                    winSetting.Dispatcher.Invoke(winSetting.Close);
                    winSetting = null;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"关闭设置窗口失败（退出流程继续）: {ex.Message}");
            }
        }

        /// <summary>
        /// 插件卸载时的清理工作
        /// </summary>
        public void UnloadPlugin()
        {
            try
            {
                LogMessage("开始卸载插件");

                // 先关窗口，早于其它清理
                CloseOwnedWindows();

                // 停止定时器
                StopTimer();

                // 清理气泡文本监听器
                CleanupBubbleTextListener();

                // 清理情感分析系统
                CleanupEmotionAnalysis();

                // 清理在线表情包管理器
                CleanupOnlineStickerManager();

                // 隐藏图片
                HideCurrentSticker();

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
                // 检查精确匹配模式设置是否发生变化，如果变化则更新版本号
                if (settings.UpdateAccurateMatchingVersionIfChanged(previousSettings))
                {
                    Utils.Logger.Info("ImageMgr", "精确匹配模式设置已变化，将在下次LLM分析时清空缓存");
                }

                settings.SaveToFile(settingsPath);

                // 更新设置副本
                previousSettings = settings.Clone();

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
        /// 插件数据目录（设置、情感分析缓存、日志）：文档\VPetLLM\Emotion\data。
        /// 不再放 MOD 目录：创意工坊更新会清掉发布包以外的文件。
        /// </summary>
        public string GetDataDirectoryPath()
        {
            try
            {
                return DiyStickerStorage.EnsureDataPath();
            }
            catch (Exception ex)
            {
                // 文档目录不可写时退回旧位置，至少本次还能读写设置
                Utils.Logger.Error("ImageMgr", $"创建数据目录失败，退回 MOD 目录: {ex.Message}");
                string fallback = Path.Combine(LoaddllPath(), "plugin", "data");
                Directory.CreateDirectory(fallback);
                return fallback;
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

        /// <summary>
        /// 扫描表情包库，只记文件路径不解码。
        ///
        /// 以前在 UI 线程上把整库按原尺寸解码常驻（内置 129 张 ≈ 233MB，最大一张 2328×2195），
        /// 既拖慢加载又挤占 VPet 动画加载的内存余量（宿主在工作集超阈值时会暂停加载动画）。
        /// 现在显示哪张才在后台按显示尺寸解哪张。
        /// </summary>
        private void LoadImgae()
        {
            try
            {
                var library = new Dictionary<IGameSave.ModeType, List<string>>();

                string[] supportedFormats = { "*.png", "*.jpg", "*.gif", "*.jpeg" };
                string dllpath = LoaddllPath();

                // 加载内置表情包（VPet_Expression）
                // 注意：VPet_Expression 文件夹中的图片直接存放在根目录，没有心情子文件夹
                // 所以我们将所有图片加载到所有心情类别中，让它们在任何心情下都能显示
                if (settings.EnableBuiltInImages)
                {
                    string builtInPath = Path.Combine(dllpath, "VPet_Expression");
                    LogMessage($"开始加载内置表情包: {builtInPath}");

                    if (Directory.Exists(builtInPath))
                    {
                        var builtInImages = CollectImageFiles(builtInPath, supportedFormats);

                        if (builtInImages.Count > 0)
                        {
                            foreach (IGameSave.ModeType modeType in Enum.GetValues(typeof(IGameSave.ModeType)))
                            {
                                AddToLibrary(library, modeType, builtInImages);
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

                // 加载DIY表情包（文档\VPetLLM\Emotion，单层目录，心情由标签决定）
                if (settings.EnableDIYImages)
                {
                    LoadDiyImages(library);
                }
                else
                {
                    LogMessage("DIY表情包已禁用");
                }

                // 整体替换：后台线程上的 PickStickerPath 只会看到完整的旧库或完整的新库
                imagepath = library;

                int totalImages = 0;
                foreach (var kvp in library)
                {
                    totalImages += kvp.Value.Count;
                    LogMessage($"{kvp.Key} 心情: {kvp.Value.Count} 张图片");
                }

                LogMessage($"图片加载完成，共加载 {library.Count} 个心情类别，总计 {totalImages} 张图片");
            }
            catch (Exception ex)
            {
                LogMessage($"加载图片时出错: {ex.Message}");
            }
        }

        private static List<string> CollectImageFiles(string directoryPath, string[] supportedFormats)
        {
            var files = new List<string>();
            foreach (string format in supportedFormats)
            {
                try
                {
                    files.AddRange(Directory.GetFiles(directoryPath, format, SearchOption.TopDirectoryOnly));
                }
                catch
                {
                    // 忽略单个格式搜索的错误
                }
            }
            return files;
        }

        private static void AddToLibrary(Dictionary<IGameSave.ModeType, List<string>> library,
            IGameSave.ModeType modeType, IEnumerable<string> paths)
        {
            if (!library.TryGetValue(modeType, out var list))
            {
                list = new List<string>();
                library[modeType] = list;
            }
            list.AddRange(paths);
        }

        /// <summary>
        /// 心情标签 → 宠物心情。
        /// </summary>
        private static IGameSave.ModeType? MoodTagToMode(string mood) => mood switch
        {
            LabelManager.MoodHappy => IGameSave.ModeType.Happy,
            LabelManager.MoodNormal => IGameSave.ModeType.Nomal,
            LabelManager.MoodPoor => IGameSave.ModeType.PoorCondition,
            LabelManager.MoodIll => IGameSave.ModeType.Ill,
            _ => null
        };

        /// <summary>
        /// DIY 表情包：按标签里勾选的心情放进对应心情；一个都没勾的是泛用，放进所有心情
        /// （和内置表情包一样）。以前没设心情的图只进"正常"，和"泛用"的说法对不上。
        /// </summary>
        private void LoadDiyImages(Dictionary<IGameSave.ModeType, List<string>> library)
        {
            try
            {
                string diyPath = DiyStickerStorage.EnsureRoot();
                LogMessage($"开始加载DIY表情包: {diyPath}");

                var labelManager = new LabelManager();
                labelManager.LoadLabels();

                var allModes = (IGameSave.ModeType[])Enum.GetValues(typeof(IGameSave.ModeType));
                int general = 0, assigned = 0;

                foreach (var filePath in DiyStickerStorage.EnumerateImages())
                {
                    var modes = labelManager.GetImageMoods(Path.GetFileName(filePath))
                        .Select(MoodTagToMode)
                        .Where(m => m.HasValue)
                        .Select(m => m.Value)
                        .ToList();

                    if (modes.Count == 0)
                    {
                        foreach (var mode in allModes)
                            AddToLibrary(library, mode, new[] { filePath });
                        general++;
                    }
                    else
                    {
                        foreach (var mode in modes)
                            AddToLibrary(library, mode, new[] { filePath });
                        assigned++;
                    }
                }

                LogMessage($"DIY表情包加载完成: 指定心情 {assigned} 张，泛用 {general} 张");
            }
            catch (Exception ex)
            {
                LogMessage($"DIY表情包加载失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 重新加载表情包库（设置窗口里改了心情/标签、导入了图片之后调用），立即生效。
        /// </summary>
        public void ReloadStickerLibrary()
        {
            LoadImgae();
            labelImageMatcher?.LoadLabels();
            imageSelector?.BuildImagePathCache();
            (emotionAnalyzer as EmotionAnalyzer)?.RefreshTagPrompts();
        }

        /// <summary>
        /// 随机挑一张指定心情的表情包路径。任意线程可调用。
        /// </summary>
        private string PickStickerPath(IGameSave.ModeType type)
        {
            var library = imagepath;
            if (!library.TryGetValue(type, out var imageList) || imageList.Count == 0)
            {
                LogWarning("ImageMgr", $"{type} 心情的表情包集合为空");
                return null;
            }

            var selected = imageList[Random.Shared.Next(imageList.Count)];
            LogDebug("ImageMgr", $"从 {imageList.Count} 张 {type} 心情表情包中选中: {selected}");
            return selected;
        }

        private void LogLibraryState(string prefix)
        {
            LogMessage($"{prefix}: 当前表情包库状态:");
            foreach (var kvp in imagepath)
            {
                LogMessage($"  - {kvp.Key}: {kvp.Value?.Count ?? 0} 张");
            }
        }

        #region 表情包显示（唯一入口）

        // 整个插件同一时刻只有一张表情包。以前每条触发路径各自"显示→等→隐藏"，互不知情：
        // 前一轮的计时到点会把后一轮刚显示的关掉；一句话还会接连出两张。
        private readonly object _stickerGate = new object();
        private CancellationTokenSource _stickerCts;
        private long _stickerGeneration;

        /// <summary>
        /// 是否有表情包正在解码或显示。自动触发在这时直接放弃，避免连环解码。
        /// </summary>
        public bool IsStickerBusy
        {
            get { lock (_stickerGate) return _stickerCts != null; }
        }

        /// <summary>
        /// 显示本地图片文件。返回值表示是否真的显示出来了；到点后在后台自动隐藏。
        /// </summary>
        /// <param name="interrupt">true=顶掉当前表情包（用户手动/外部调用）；false=当前有表情包就放弃（自动触发）</param>
        public Task<bool> ShowStickerFileAsync(string path, int durationMs, bool interrupt, string source)
        {
            if (string.IsNullOrEmpty(path))
                return Task.FromResult(false);
            return ShowStickerAsync(maxPixel => StickerDecoder.DecodeFile(path, maxPixel), durationMs, interrupt, source);
        }

        /// <summary>
        /// 显示内存中的图片字节（在线表情包、Base64）。语义同 <see cref="ShowStickerFileAsync"/>。
        /// </summary>
        public Task<bool> ShowStickerBytesAsync(byte[] data, int durationMs, bool interrupt, string source)
        {
            if (data == null || data.Length == 0)
                return Task.FromResult(false);
            return ShowStickerAsync(maxPixel => StickerDecoder.Decode(data, maxPixel), durationMs, interrupt, source);
        }

        private async Task<bool> ShowStickerAsync(Func<int, DecodedSticker> decode, int durationMs, bool interrupt, string source)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (image == null || dispatcher == null)
            {
                LogWarning("ImageMgr", "UI组件未初始化，无法显示表情包");
                return false;
            }

            CancellationTokenSource previous;
            var cts = new CancellationTokenSource();
            long generation;
            lock (_stickerGate)
            {
                if (_stickerCts != null && !interrupt)
                {
                    LogDebug("ImageMgr", $"[{source}] 已有表情包在显示，跳过本次自动触发");
                    return false;
                }
                previous = _stickerCts;
                _stickerCts = cts;
                generation = Interlocked.Increment(ref _stickerGeneration);
            }
            // 锁外取消：被取消方的续体可能在 Cancel 里同步跑，不能让它撞上这把锁
            previous?.Cancel();

            try
            {
                int maxPixel = await dispatcher.InvokeAsync(() => image.GetDecodePixelSize());

                // 解码（含 GIF 全部帧）在线程池上做，UI 线程只负责挂上冻结好的帧
                var sticker = await Task.Run(() => decode(maxPixel), cts.Token).ConfigureAwait(false);
                cts.Token.ThrowIfCancellationRequested();

                bool shown = await dispatcher.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested)
                        return false;
                    image.ShowSticker(sticker);
                    return true;
                });

                if (!shown)
                {
                    ReleaseStickerSlot(cts);
                    return false;
                }

                LogInfo("ImageMgr", $"[{source}] 表情包显示成功（{sticker.Frames.Length} 帧，解码长边 ≤{maxPixel}px），{durationMs}ms 后自动隐藏");
                _ = HideStickerLaterAsync(cts, generation, durationMs);
                return true;
            }
            catch (OperationCanceledException)
            {
                ReleaseStickerSlot(cts);
                return false;
            }
            catch (Exception ex)
            {
                LogError("ImageMgr", $"[{source}] 显示表情包失败: {ex.Message}");
                // 上一张的自动隐藏已经被本次作废，本次又没显示出来——不清掉它就会一直挂着
                await ClearStickerIfCurrentAsync(generation);
                ReleaseStickerSlot(cts);
                return false;
            }
        }

        private async Task ClearStickerIfCurrentAsync(long generation)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                    return;

                await dispatcher.InvokeAsync(() =>
                {
                    if (Interlocked.Read(ref _stickerGeneration) == generation)
                        image?.ClearSticker();
                });
            }
            catch (Exception ex)
            {
                LogDebug("ImageMgr", $"隐藏表情包失败: {ex.Message}");
            }
        }

        private async Task HideStickerLaterAsync(CancellationTokenSource cts, long generation, int durationMs)
        {
            try
            {
                await Task.Delay(Math.Max(0, durationMs), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 被新表情包顶掉或被手动关闭：界面已归别人管，这里什么都不碰
                ReleaseStickerSlot(cts);
                return;
            }

            await ClearStickerIfCurrentAsync(generation);
            ReleaseStickerSlot(cts);
        }

        private void ReleaseStickerSlot(CancellationTokenSource cts)
        {
            lock (_stickerGate)
            {
                if (ReferenceEquals(_stickerCts, cts))
                    _stickerCts = null;
            }
            // CTS 没挂计时器也没取过 WaitHandle，不 Dispose 也不漏；
            // 不 Dispose 才能让别的线程在锁外放心 Cancel 它。
        }

        /// <summary>
        /// 立即隐藏当前表情包并作废它的自动隐藏计时。任意线程可调用。
        /// </summary>
        public void HideCurrentSticker()
        {
            CancellationTokenSource current;
            lock (_stickerGate)
            {
                current = _stickerCts;
                _stickerCts = null;
                Interlocked.Increment(ref _stickerGeneration);
            }
            current?.Cancel();

            var dispatcher = Application.Current?.Dispatcher;
            if (image == null || dispatcher == null)
                return;

            if (dispatcher.CheckAccess())
                image.ClearSticker();
            else
                dispatcher.BeginInvoke(new Action(() => image.ClearSticker()));
        }

        #endregion

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

                // 已有表情包在显示：这一轮让过去，别去打断它
                if (IsStickerBusy)
                {
                    LogDebug("ImageMgr", "定时器触发: 已有表情包在显示，本轮跳过");
                    SetRandomInterval();
                    timer?.Start();
                    return;
                }

                // 优先尝试在线表情包（如果启用且在随机显示中启用）
                if (settings.OnlineSticker.IsEnabled && settings.OnlineSticker.EnableInRandomDisplay)
                {
                    LogDebug("ImageMgr", "定时器触发: 尝试显示在线随机表情包");
                    bool onlineSuccess = await ShowOnlineRandomStickerAsync(interrupt: false);
                    if (onlineSuccess)
                    {
                        LogInfo("ImageMgr", "定时器触发: 在线表情包显示成功");
                        // Restart timer with new random interval (only if time trigger is still enabled)
                        if (settings.UseTimeTrigger)
                        {
                            SetRandomInterval();
                            timer?.Start();
                        }
                        LogDebug("ImageMgr", "=== 定时器周期完成（在线表情包）===");
                        return;
                    }
                    else
                    {
                        LogDebug("ImageMgr", "定时器触发: 在线表情包显示失败，使用本地表情包");
                    }
                }

                // Get current pet mood
                var currentMode = MW.Core.Save.CalMode();
                LogDebug("ImageMgr", $"当前宠物心情: {currentMode}");

                var imageToShow = PickStickerPath(currentMode);

                if (imageToShow != null)
                {
                    LogInfo("ImageMgr", $"定时器显示 {currentMode} 心情表情包");
                    await ShowStickerFileAsync(imageToShow, settings.GetDisplayDurationMs(), interrupt: false, "定时器");
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

                // Create submenu for online sticker
                var onlineStickerTrigger = new MenuItem()
                {
                    Header = "在线网络表情包库",
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
                onlineStickerTrigger.Click += async (s, e) => await ShowOnlineRandomStickerAsync();

                // Create submenu for settings
                var settingsMenu = new MenuItem()
                {
                    Header = "插件设置",
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
                settingsMenu.Click += (s, e) => Setting();

                menuItem.Items.Add(manualTrigger);
                menuItem.Items.Add(onlineStickerTrigger);
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

                // 优先尝试在线表情包（如果启用且在随机显示中启用）
                if (settings.OnlineSticker.IsEnabled && settings.OnlineSticker.EnableInRandomDisplay)
                {
                    LogMessage("尝试显示在线随机表情包");
                    bool onlineSuccess = await ShowOnlineRandomStickerAsync();
                    if (onlineSuccess)
                    {
                        LogMessage("在线随机表情包显示成功");
                        // 重启定时器
                        SetRandomInterval();
                        timer?.Start();
                        LogMessage("=== 手动显示周期完成（在线表情包）===");
                        return;
                    }
                    else
                    {
                        LogMessage("在线随机表情包显示失败，使用本地表情包");
                    }
                }

                var currentMode = MW.Core.Save.CalMode();
                LogMessage($"手动显示: 当前宠物心情: {currentMode}");

                var imageToShow = PickStickerPath(currentMode);

                if (imageToShow != null)
                {
                    LogMessage($"手动显示: 准备显示 {currentMode} 心情表情包");
                    await ShowStickerFileAsync(imageToShow, settings.GetDisplayDurationMs(), interrupt: true, "手动");
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
                string dataPath = GetDataDirectoryPath();
                // MOD 根目录（VPet_Expression 所在）。以前这里取的是 DLL 所在的 plugin 目录，
                // 拼出来的 plugin\VPet_Expression\label.json 不存在，向量匹配从没加载过内置表情包标签
                string dllPath = LoaddllPath();
                string cachePath = Path.Combine(dataPath, "emotion_cache.json");
                cacheManager = new CacheManager(cachePath);
                cacheManager.Load();
                LogMessage($"缓存管理器已加载: {cachePath}");

                // 创建情感分析器（候选标签取自 VPet_Expression\label.json 和 DIY 标签）
                emotionAnalyzer = new EmotionAnalyzer(llmClient, cacheManager, MW, this);
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
                speechCapturer = new SpeechCapturer(MW, emotionAnalyzer, imageSelector, this);
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

                // 应用代理设置到 LLM HttpClient 工厂
                LLMHttpClientFactory.Configure(config);

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

                    case LLMProvider.Free:
                        string freeModel = string.IsNullOrWhiteSpace(config.FreeModel)
                            ? "gpt-3.5-turbo"
                            : config.FreeModel;
                        LogMessage($"使用Free客户端, 模型: {freeModel}");
                        return new FreeClient(freeModel, this);

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

                // 加载DIY表情包标签（diy_labels.json；旧格式 label.json 已在启动迁移时并入）
                // 只取普通标签 + 心情标签，去掉 general / AI 处理标记这类不表达语义的保留标签
                var diyLabels = new LabelManager();
                diyLabels.LoadLabels();
                var diyImageLabels = DiyStickerStorage.EnumerateImages()
                    .Select(Path.GetFileName)
                    .ToDictionary(
                        name => name,
                        name => diyLabels.GetImageTags(name).Where(t => !LabelManager.IsReservedTag(t)).ToList());
                vectorRetriever.LoadLabels(diyImageLabels, DiyStickerStorage.LabelsFilePath);
                LogMessage($"已加载DIY表情标签: {DiyStickerStorage.LabelsFilePath}");
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

                var imageToShow = PickStickerPath(currentMode);

                if (imageToShow != null)
                {
                    LogMessage($"测试显示: 找到 {currentMode} 心情表情包，开始显示");
                    bool shown = await ShowStickerFileAsync(imageToShow, settings.GetDisplayDurationMs(), interrupt: true, "测试");
                    LogMessage(shown ? "测试显示: 表情包显示成功" : "测试显示: 表情包显示失败");
                }
                else
                {
                    LogMessage($"测试显示失败：{currentMode} 心情没有可用的图片");

                    // 显示详细的表情包库状态
                    LogLibraryState("测试显示");
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
        /// 初始化图片插件协调器
        /// </summary>
        private void InitializeImageCoordinator()
        {
            try
            {
                LogMessage("开始初始化图片插件协调器");

                // 创建协调器
                imageCoordinator = new ImagePluginCoordinator(this);

                // 启动定时器，定期清理超时会话（每 30 秒检查一次）
                sessionCleanupTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(30)
                };
                sessionCleanupTimer.Tick += (s, e) =>
                {
                    try
                    {
                        imageCoordinator?.CheckAndCleanupTimedOutSession();
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"清理超时会话失败: {ex.Message}");
                    }
                };
                sessionCleanupTimer.Start();

                LogMessage("图片插件协调器初始化完成");
            }
            catch (Exception ex)
            {
                LogMessage($"初始化图片插件协调器失败: {ex.Message}");
                LogMessage($"堆栈跟踪: {ex.StackTrace}");
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
        ///
        /// 开启气泡触发时的语义是"VPet 每次说话时按概率显示表情包"。以前关键词匹配那段写在
        /// 概率门外面，不管命不命中都会再显示一张——默认 20% 实际上是句句出图，命中时一句话出两张。
        /// 现在关键词只负责挑心情，开启时整段都在概率门之内；关闭时沿用原设计每句按关键词出图
        /// （与 SpeechCapturer 关闭气泡触发时"每句都做情感分析"对称），但都不打断正在显示的那张。
        /// </summary>
        private async void OnBubbleTextCaptured(object sender, string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    LogDebug("BubbleTextListener", "文本为空或空白，跳过处理");
                    return;
                }

                if (!settings.IsEnabled)
                {
                    LogDebug("BubbleTextListener", "插件未启用，跳过处理");
                    return;
                }

                if (IsStickerBusy)
                {
                    LogDebug("BubbleTextListener", "已有表情包在显示，跳过本句");
                    return;
                }

                if (!settings.UseBubbleTrigger)
                {
                    await ShowKeywordMatchedStickerAsync(text, "关键词匹配");
                    return;
                }

                if (!settings.ShouldTriggerBubble())
                {
                    LogDebug("BubbleTextListener", $"未命中概率 ({settings.BubbleTriggerProbability}%)，跳过显示");
                    return;
                }

                LogMessage($"BubbleTextListener: 命中概率 ({settings.BubbleTriggerProbability}%)，开始显示");
                await HandleBubbleProbabilityTrigger(text);
            }
            catch (Exception ex)
            {
                LogMessage($"BubbleTextListener 处理气泡文本失败: {ex.Message}");
                LogMessage($"BubbleTextListener 错误堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 简单关键词匹配：根据文本挑心情，没命中就用宠物当前心情。
        /// </summary>
        private IGameSave.ModeType MatchMoodByKeywords(string text)
        {
            IGameSave.ModeType currentMode = MW.Core.Save.CalMode();
            if (string.IsNullOrWhiteSpace(text))
                return currentMode;

            string lowerText = text.ToLower();

            if (lowerText.Contains("开心") || lowerText.Contains("高兴") || lowerText.Contains("快乐") ||
                lowerText.Contains("哈哈") || lowerText.Contains("嘻嘻"))
            {
                LogDebug("SimpleMatching", "匹配到开心相关关键词");
                return IGameSave.ModeType.Happy;
            }

            if (lowerText.Contains("难过") || lowerText.Contains("伤心") || lowerText.Contains("哭") ||
                lowerText.Contains("不开心") || lowerText.Contains("郁闷"))
            {
                LogDebug("SimpleMatching", "匹配到难过相关关键词");
                return IGameSave.ModeType.PoorCondition;
            }

            if (lowerText.Contains("生病") || lowerText.Contains("不舒服") || lowerText.Contains("头疼") ||
                lowerText.Contains("感冒") || lowerText.Contains("发烧"))
            {
                LogDebug("SimpleMatching", "匹配到生病相关关键词");
                return IGameSave.ModeType.Ill;
            }

            LogDebug("SimpleMatching", $"未匹配到特定关键词，使用当前心情: {currentMode}");
            return currentMode;
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
        /// 当前心情的随机表情包路径（供 ImageSelector / EmotionAnalyzer 降级用）。任意线程可调用。
        /// </summary>
        public string GetCurrentMoodImagePath()
        {
            try
            {
                return PickStickerPath(MW.Core.Save.CalMode());
            }
            catch (Exception ex)
            {
                LogMessage($"ImageMgr: 获取当前心情图片失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 处理气泡概率触发（内部方法）
        /// 注意：调用此方法前必须在入口处完成概率检查
        /// </summary>
        /// <param name="text">气泡文本；为空时直接用宠物当前心情</param>
        private async Task HandleBubbleProbabilityTrigger(string text = null)
        {
            try
            {
                // 优先尝试在线表情包（如果启用且在气泡触发中启用）
                if (settings.OnlineSticker.IsEnabled && settings.OnlineSticker.EnableInBubbleTrigger)
                {
                    LogMessage("气泡概率触发: 尝试显示在线随机表情包");
                    if (await ShowOnlineRandomStickerAsync(interrupt: false))
                    {
                        LogMessage("气泡概率触发: 在线表情包显示成功");
                        return;
                    }

                    LogMessage("气泡概率触发: 在线表情包显示失败，使用本地表情包");
                }

                // 显示本地表情包
                await ShowKeywordMatchedStickerAsync(text, "气泡触发");
            }
            catch (Exception ex)
            {
                LogMessage($"处理气泡概率触发失败: {ex.Message}");
                LogMessage($"气泡概率触发错误堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 按文本关键词挑心情并显示一张本地表情包（自动触发，不打断正在显示的那张）。
        /// </summary>
        private async Task ShowKeywordMatchedStickerAsync(string text, string source)
        {
            var targetMode = MatchMoodByKeywords(text);
            var imageToShow = PickStickerPath(targetMode);

            if (imageToShow != null)
            {
                LogDebug(source, $"显示 {targetMode} 心情表情包");
                await ShowStickerFileAsync(imageToShow, settings.GetDisplayDurationMs(), interrupt: false, source);
            }
            else
            {
                LogMessage($"{source}: 未找到 {targetMode} 心情的表情包");
                LogLibraryState(source);
            }
        }

        /// <summary>
        /// 供 SpeechCapturer 调用的气泡概率触发处理方法
        /// 在入口处进行概率检查，未命中时直接返回，不进入内部处理
        /// </summary>
        public async void HandleBubbleProbabilityFromSpeechCapturer()
        {
            try
            {
                // 入口检查：插件未启用或直接返回
                if (!settings.IsEnabled)
                {
                    return;
                }

                // 入口检查：气泡触发未启用直接返回
                if (!settings.UseBubbleTrigger)
                {
                    return;
                }

                // 入口检查：概率未命中直接返回，不进入内部处理
                if (!settings.ShouldTriggerBubble())
                {
                    LogMessage($"气泡概率触发: 未命中概率 ({settings.BubbleTriggerProbability}%)，跳过显示");
                    return;
                }

                // 概率命中，进入处理
                LogMessage($"气泡概率触发: 命中概率 ({settings.BubbleTriggerProbability}%)，开始显示");
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

        /// <summary>
        /// 获取图片选择器（供 EmotionAnalyzer 调用）
        /// </summary>
        public ImageSelector GetImageSelector()
        {
            return imageSelector;
        }

        /// <summary>
        /// 获取向量检索器（供 EmotionAnalyzer 调用）
        /// </summary>
        public IVectorRetriever GetVectorRetriever()
        {
            return vectorRetriever;
        }

        /// <summary>
        /// 根据文件名获取图片完整路径
        /// </summary>
        public string GetImagePath(string filename)
        {
            try
            {
                string dllPath = LoaddllPath();

                // 先在内置表情包目录查找
                string builtInPath = Path.Combine(dllPath, "VPet_Expression", filename);
                if (File.Exists(builtInPath))
                {
                    return builtInPath;
                }

                // 再在DIY表情包目录（文档\VPetLLM\Emotion，单层）查找
                string diyPath = Path.Combine(DiyStickerStorage.RootPath, filename);
                return File.Exists(diyPath) ? diyPath : null;
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("ImageMgr", $"获取图片路径失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 初始化在线表情包管理器
        /// </summary>
        private void InitializeOnlineStickerManager()
        {
            try
            {
                LogMessage("开始初始化在线表情包管理器");

                // 创建在线表情包管理器
                onlineStickerManager = new OnlineStickerManager(this);

                // 应用当前设置
                UpdateOnlineStickerManager();

                LogMessage("在线表情包管理器初始化完成");
            }
            catch (Exception ex)
            {
                LogMessage($"初始化在线表情包管理器失败: {ex.Message}");
                LogMessage($"堆栈跟踪: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 更新在线表情包管理器配置
        /// </summary>
        private void UpdateOnlineStickerManager()
        {
            try
            {
                if (onlineStickerManager == null)
                {
                    LogMessage("在线表情包管理器未初始化，跳过配置更新");
                    return;
                }

                var onlineSettings = settings.OnlineSticker;
                onlineStickerManager.UpdateConfiguration(
                    onlineSettings.IsEnabled,
                    onlineSettings.UseBuiltInCredentials,
                    onlineSettings.ServiceUrl,
                    onlineSettings.ApiKey,
                    onlineSettings.TagCount,
                    onlineSettings.CacheDurationMinutes,
                    onlineSettings.DisplayDurationSeconds
                );

                LogMessage($"在线表情包管理器配置已更新: 启用={onlineSettings.IsEnabled}");
            }
            catch (Exception ex)
            {
                LogMessage($"更新在线表情包管理器配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理在线表情包管理器
        /// </summary>
        private void CleanupOnlineStickerManager()
        {
            try
            {
                if (onlineStickerManager != null)
                {
                    onlineStickerManager.Dispose();
                    onlineStickerManager = null;
                    LogMessage("在线表情包管理器已清理");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"清理在线表情包管理器失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取在线表情包管理器（供其他组件调用）
        /// </summary>
        public OnlineStickerManager GetOnlineStickerManager()
        {
            return onlineStickerManager;
        }

        /// <summary>
        /// 测试在线表情包连接
        /// </summary>
        public async Task<bool> TestOnlineStickerConnectionAsync()
        {
            try
            {
                if (onlineStickerManager == null)
                {
                    LogMessage("在线表情包管理器未初始化");
                    return false;
                }

                LogMessage("开始测试在线表情包连接");
                bool result = await onlineStickerManager.TestConnectionAsync();

                if (result)
                {
                    LogMessage("在线表情包连接测试成功");
                }
                else
                {
                    LogMessage("在线表情包连接测试失败");
                }

                return result;
            }
            catch (Exception ex)
            {
                LogMessage($"测试在线表情包连接时出现异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 显示在线随机表情包
        /// </summary>
        /// <param name="interrupt">true=顶掉当前表情包（菜单/设置页手动触发）；false=当前有表情包就放弃（自动触发）</param>
        public async Task<bool> ShowOnlineRandomStickerAsync(bool interrupt = true)
        {
            try
            {
                if (onlineStickerManager == null || !settings.OnlineSticker.IsEnabled)
                {
                    LogMessage("在线表情包功能未启用或管理器未初始化");
                    return false;
                }

                LogMessage("开始显示在线随机表情包");
                bool result = await onlineStickerManager.DisplayRandomStickerAsync(interrupt);

                if (result)
                {
                    LogMessage("在线随机表情包显示成功");
                }
                else
                {
                    LogMessage("在线随机表情包显示失败");
                }

                return result;
            }
            catch (Exception ex)
            {
                LogMessage($"显示在线随机表情包时出现异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 根据情感显示在线表情包
        /// </summary>
        public async Task<bool> ShowOnlineStickerByEmotionAsync(string emotion, List<string> additionalTags = null)
        {
            try
            {
                if (onlineStickerManager == null || !settings.OnlineSticker.IsEnabled)
                {
                    return false;
                }

                LogMessage($"根据情感显示在线表情包: {emotion}");
                bool result = await onlineStickerManager.SearchAndDisplayStickerAsync(emotion, additionalTags);

                if (result)
                {
                    LogMessage($"在线表情包显示成功: {emotion}");
                }

                return result;
            }
            catch (Exception ex)
            {
                LogMessage($"根据情感显示在线表情包时出现异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取在线表情包的系统提示词补充
        /// </summary>
        public async Task<string> GetOnlineStickerSystemPromptAsync()
        {
            try
            {
                if (onlineStickerManager == null || !settings.OnlineSticker.IsEnabled)
                {
                    return string.Empty;
                }

                return await onlineStickerManager.GetSystemPromptAdditionAsync();
            }
            catch (Exception ex)
            {
                LogMessage($"获取在线表情包系统提示词失败: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 创建并返回AI图片标签生成服务（用于设置窗口）
        /// </summary>
        public LLMImageTaggingService CreateAIImageTaggingService(LabelManager labelManager)
        {
            try
            {
                string pluginDir = LoaddllPath();
                return new LLMImageTaggingService(this, labelManager, pluginDir);
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("ImageMgr", $"创建AI图片标签生成服务失败: {ex.Message}");
                return null;
            }
        }
    


        /// <summary>
        /// 从 Base64 字符串显示图片（供外部插件如 StickerPlugin 调用）
        /// </summary>
        /// <param name="base64Image">Base64 编码的图片</param>
        /// <param name="durationSeconds">显示时长（秒）</param>
        public async Task ShowImageFromBase64Async(string base64Image, int durationSeconds)
        {
            try
            {
                LogInfo("ImageMgr", $"开始从 Base64 显示图片，时长: {durationSeconds}秒");

                if (string.IsNullOrWhiteSpace(base64Image))
                {
                    LogWarning("ImageMgr", "Base64 图片数据为空");
                    return;
                }

                // 清理 Base64 字符串（移除可能的前缀和空白字符）
                string cleanBase64 = base64Image.Trim();
                
                // 移除 data:image 前缀（如果存在）
                if (cleanBase64.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    var commaIndex = cleanBase64.IndexOf(',');
                    if (commaIndex > 0)
                    {
                        cleanBase64 = cleanBase64.Substring(commaIndex + 1);
                        LogDebug("ImageMgr", "移除了 data:image 前缀");
                    }
                }

                // 移除所有空白字符（Base64 不应包含空白字符）
                cleanBase64 = cleanBase64.Replace(" ", "").Replace("\n", "").Replace("\r", "").Replace("\t", "");

                LogDebug("ImageMgr", $"清理后的 Base64 长度: {cleanBase64.Length}");

                // 解码 Base64
                byte[] imageBytes;
                try
                {
                    imageBytes = Convert.FromBase64String(cleanBase64);
                    LogDebug("ImageMgr", $"Base64 解码成功，大小: {imageBytes.Length} 字节");
                }
                catch (FormatException ex)
                {
                    LogError("ImageMgr", $"Base64 解码失败: {ex.Message}");
                    LogDebug("ImageMgr", $"Base64 前100个字符: {cleanBase64.Substring(0, Math.Min(100, cleanBase64.Length))}");
                    return;
                }

                // 解码与显示走统一入口：后台按显示尺寸解码，到点自动隐藏，不再阻塞到隐藏为止
                bool shown = await ShowStickerBytesAsync(imageBytes, durationSeconds * 1000, interrupt: true, "Base64");
                LogInfo("ImageMgr", shown ? "Base64 图片显示成功" : "Base64 图片显示失败");
            }
            catch (Exception ex)
            {
                LogError("ImageMgr", $"从 Base64 显示图片失败: {ex.Message}");
                LogDebug("ImageMgr", $"错误堆栈: {ex.StackTrace}");
            }
        }
    }
}
