using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        
        // 日志收集
        private List<string> logMessages = new List<string>();
        private const int MaxLogMessages = 1000; // 最多保存1000条日志

        public ImageSettings Settings => settings;

        public ImageMgr(IMainWindow mainwin) : base(mainwin)
        {
            random = new Random();
            imagepath = new Dictionary<IGameSave.ModeType, List<BitmapImage>>();
            
            // 初始化设置
            InitializeSettings();
        }

        public override void LoadPlugin()
        {
            try
            {
                LogMessage("开始加载插件");

                // Create ImageUI
                image = new ImageUI(this);

                // Load images
                LoadImgae();

                // Create menu
                CreateMenu();

                // 添加设置菜单到MOD配置菜单
                AddSettingsToModConfig();

                // Create and setup timer only if enabled
                if (settings.IsEnabled)
                {
                    StartTimer();
                }

                LogMessage("插件加载完成");
            }
            catch (Exception ex)
            {
                LogMessage($"插件加载失败: {ex.Message}");
                LogMessage($"堆栈跟踪: {ex.StackTrace}");
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
            
            settings = newSettings.Clone();
            
            // 如果表情包启用状态改变，重新加载图片
            if (needReloadImages)
            {
                LogMessage("表情包启用状态已更改，重新加载图片");
                LoadImgae();
            }
            
            // 重新配置定时器
            if (settings.IsEnabled)
            {
                StartTimer();
            }
            else
            {
                StopTimer();
            }
            
            LogMessage("设置已应用");
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
        /// 记录日志
        /// </summary>
        public void LogMessage(string message)
        {
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
        /// 获取所有日志
        /// </summary>
        public List<string> GetLogMessages()
        {
            lock (logMessages)
            {
                return new List<string>(logMessages);
            }
        }

        /// <summary>
        /// 清空日志
        /// </summary>
        public void ClearLogs()
        {
            lock (logMessages)
            {
                logMessages.Clear();
            }
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
            if (imagepath.ContainsKey(type) && imagepath[type].Count > 0)
            {
                var imageList = imagepath[type];
                int randomIndex = random.Next(imageList.Count);
                return imageList[randomIndex];
            }
            return null;
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
            if (imageToShow == null || image?.Image == null)
                return;

            image.Visibility = Visibility.Visible;

            if (IsGifImage(imageToShow))
            {
                // For GIF images
                ImageBehavior.SetAnimatedSource(image.Image, imageToShow);
                image.Image.Source = null;
            }
            else
            {
                // For static images
                ImageBehavior.SetAnimatedSource(image.Image, null);
                image.Image.Source = imageToShow;
            }
        }

        private void HideImage()
        {
            if (image?.Image == null)
                return;

            image.Visibility = Visibility.Collapsed;
            ImageBehavior.SetAnimatedSource(image.Image, null);
            image.Image.Source = null;
        }

        private void SetRandomInterval()
        {
            if (timer == null)
                return;

            int intervalMs = settings.GetDisplayIntervalMs();
            timer.Interval = TimeSpan.FromMilliseconds(intervalMs);
            
            if (settings.DebugMode)
            {
                if (settings.UseRandomInterval)
                {
                    int minutes = intervalMs / (60 * 1000);
                    LogMessage($"设置随机定时器间隔: {minutes} 分钟");
                }
                else
                {
                    LogMessage($"设置固定定时器间隔: {settings.DisplayInterval} 分钟");
                }
            }
        }

        private async void Timer_Tick(object sender, EventArgs e)
        {
            try
            {
                timer?.Stop();

                // Check if plugin is enabled
                if (!settings.IsEnabled)
                {
                    SetRandomInterval();
                    timer?.Start();
                    return;
                }

                // Get current pet mood
                var currentMode = MW.Core.Save.CalMode();
                var imageToShow = Return_Image(currentMode);

                if (imageToShow != null)
                {
                    if (settings.DebugMode)
                        LogMessage($"显示 {currentMode} 心情图片");
                    DisplayImage(imageToShow);

                    // Auto-hide after configured duration
                    await Task.Delay(settings.GetDisplayDurationMs());
                    HideImage();
                }

                // Restart timer with new random interval
                SetRandomInterval();
                timer?.Start();
            }
            catch (Exception ex)
            {
                LogMessage($"定时器事件出错: {ex.Message}");
                // Restart timer even on error
                SetRandomInterval();
                timer?.Start();
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
                timer?.Stop();

                var currentMode = MW.Core.Save.CalMode();
                var imageToShow = Return_Image(currentMode);

                if (imageToShow != null)
                {
                    LogMessage($"手动显示 {currentMode} 心情图片");
                    DisplayImage(imageToShow);

                    // Auto-hide after configured duration
                    await Task.Delay(settings.GetDisplayDurationMs());
                    HideImage();
                }

                // Restart timer
                SetRandomInterval();
                timer?.Start();
            }
            catch (Exception ex)
            {
                LogMessage($"手动显示出错: {ex.Message}");
                SetRandomInterval();
                timer?.Start();
            }
        }

        /// <summary>
        /// 测试显示表情包（用于设置窗口的测试按钮）
        /// </summary>
        public async void TestDisplayImage()
        {
            try
            {
                timer?.Stop();

                var currentMode = MW.Core.Save.CalMode();
                var imageToShow = Return_Image(currentMode);

                if (imageToShow != null)
                {
                    LogMessage($"测试显示 {currentMode} 心情图片");
                    DisplayImage(imageToShow);

                    // Auto-hide after configured duration
                    await Task.Delay(settings.GetDisplayDurationMs());
                    HideImage();
                }
                else
                {
                    LogMessage($"测试显示失败：{currentMode} 心情没有可用的图片");
                }

                // Restart timer if enabled
                if (settings.IsEnabled)
                {
                    SetRandomInterval();
                    timer?.Start();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"测试显示出错: {ex.Message}");
                if (settings.IsEnabled)
                {
                    SetRandomInterval();
                    timer?.Start();
                }
            }
        }
    }
}