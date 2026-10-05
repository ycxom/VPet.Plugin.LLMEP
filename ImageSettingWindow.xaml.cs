using Panuon.WPF.UI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VPet.Plugin.LLMEP.EmotionAnalysis;
using VPet.Plugin.LLMEP.EmotionAnalysis.LLMClient;
using VPet.Plugin.LLMEP.Services;

namespace VPet.Plugin.LLMEP
{
    /// <summary>
    /// ImageSettingWindow.xaml 的交互逻辑
    /// </summary>
    public partial class ImageSettingWindow : WindowX
    {
        private ImageMgr imageMgr;
        private ImageSettings settings;
        private ImageSettings originalSettings;
        private DispatcherTimer logUpdateTimer;

        // 标签管理相关
        private LabelManager labelManager;
        private List<StickerListItem> allStickers = new List<StickerListItem>();
        private StickerListItem editingItem;      // 标签文本框当前对应的图片（单选时）
        private bool suppressSelectionEvents;     // 程序重建列表时不触发选中事件
        private int scanGeneration;               // 新一轮扫描开始后，旧一轮的缩略图加载自行退出
        private int previewGeneration;            // 快速切换时只显示最后一次选中的预览

        // AI图片标签生成服务
        private LLMImageTaggingService aiTaggingService;
        private bool isAIProcessing = false;

        // 打标服务的事件处理器。存成字段才能在关窗时解绑——匿名 lambda 是摘不掉的，
        // 而这两个闭包捕获了 this，任务只要还在跑就会把整个窗口留在内存里。
        private EventHandler<ImageTaggingProgressEventArgs> aiProgressHandler;
        private EventHandler<ImageTaggingCompletedEventArgs> aiCompletedHandler;

        public ImageSettingWindow(ImageMgr imageMgr)
        {
            InitializeComponent();

            this.imageMgr = imageMgr;
            this.settings = imageMgr.Settings?.Clone() ?? new ImageSettings();
            this.originalSettings = imageMgr.Settings?.Clone() ?? new ImageSettings();

            // 初始化标签管理器
            try
            {
                InitializeLabelManager();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"标签管理器初始化失败: {ex.Message}");
            }

            // 初始化AI标签生成服务
            try
            {
                InitializeAITaggingService();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AI标签生成服务初始化失败: {ex.Message}");
            }

            // 加载设置到UI
            LoadSettings();

            // 启动后台异步扫描图片
            _ = RefreshImageListAsync("后台扫描完成");

            // 更新图片路径显示
            UpdateImagePath();

            // 启动日志更新定时器
            try
            {
                logUpdateTimer = new DispatcherTimer();
                logUpdateTimer.Interval = TimeSpan.FromSeconds(1);
                logUpdateTimer.Tick += LogUpdateTimer_Tick;
                logUpdateTimer.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"日志定时器启动失败: {ex.Message}");
            }
        }

        private void LogUpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (TextBoxLog == null) return;

                // 使用静态日志系统，根据设置的日志等级获取日志
                var minLevel = (VPet.Plugin.LLMEP.Utils.LogLevel)settings.LogLevel;
                var logs = imageMgr.GetLogMessages(minLevel);

                if (logs.Count > 0)
                {
                    var newText = string.Join(Environment.NewLine, logs);

                    // 仅在内容变化时刷新，避免打断用户的选择/滚动
                    if (TextBoxLog.Text != newText)
                    {
                        // 更新前记录用户是否已停留在底部（留 1px 容差）。
                        // 若用户上拉查看历史日志，则不强制滚动到底部。
                        bool wasAtBottom = LogScrollViewer == null
                            || LogScrollViewer.ScrollableHeight <= 0
                            || LogScrollViewer.VerticalOffset >= LogScrollViewer.ScrollableHeight - 1;

                        TextBoxLog.Text = newText;

                        // 仅当之前停留在底部时才自动滚动到底部
                        if (wasAtBottom)
                        {
                            LogScrollViewer?.ScrollToEnd();
                        }
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(TextBoxLog.Text) || TextBoxLog.Text == "日志将显示在这里...")
                    {
                        var levelName = ((VPet.Plugin.LLMEP.Utils.LogLevel)settings.LogLevel).ToString();
                        TextBoxLog.Text = $"暂无 {levelName} 级别及以上的日志。\n\n提示：\n- 调整日志等级可以查看更多或更少的日志\n- 开启Debug日志可以查看详细的HTTP请求信息\n- 日志会实时显示在这里";
                    }
                }
            }
            catch (Exception ex)
            {
                // 忽略更新错误
                System.Diagnostics.Debug.WriteLine($"日志更新失败: {ex.Message}");
            }
        }

        private void LoadSettings()
        {
            // 基本功能开关
            CheckBoxEnabled.IsChecked = settings.IsEnabled;
            CheckBoxBuiltInImages.IsChecked = settings.EnableBuiltInImages;
            CheckBoxDIYImages.IsChecked = settings.EnableDIYImages;

            // 时间触发设置
            CheckBoxTimeTrigger.IsChecked = settings.UseTimeTrigger;
            SliderDisplayDuration.Value = settings.DisplayDuration;
            SliderDisplayInterval.Value = settings.DisplayInterval;
            CheckBoxRandomInterval.IsChecked = settings.UseRandomInterval;

            // 气泡触发设置
            CheckBoxBubbleTrigger.IsChecked = settings.UseBubbleTrigger;
            SliderBubbleTriggerProbability.Value = settings.BubbleTriggerProbability;

            // 调试设置
            CheckBoxDebugMode.IsChecked = settings.DebugMode;
            ComboBoxLogLevel.SelectedIndex = settings.LogLevel;
            CheckBoxFileLogging.IsChecked = settings.EnableFileLogging;

            // LLM情感分析设置
            if (settings.EmotionAnalysis != null)
            {
                CheckBoxEmotionAnalysis.IsChecked = settings.EmotionAnalysis.EnableLLMEmotionAnalysis;
                CheckBoxAccurateImageMatching.IsChecked = settings.UseAccurateImageMatching;
                CheckBoxVisionModel.IsChecked = settings.EmotionAnalysis.IsVisionModel;

                // 设置提供商
                switch (settings.EmotionAnalysis.Provider)
                {
                    case EmotionAnalysis.LLMProvider.OpenAI:
                        ComboBoxLLMProvider.SelectedIndex = 0;
                        break;
                    case EmotionAnalysis.LLMProvider.Gemini:
                        ComboBoxLLMProvider.SelectedIndex = 1;
                        break;
                    case EmotionAnalysis.LLMProvider.Ollama:
                        ComboBoxLLMProvider.SelectedIndex = 2;
                        break;
                    case EmotionAnalysis.LLMProvider.Free:
                        ComboBoxLLMProvider.SelectedIndex = 3;
                        break;
                    default:
                        ComboBoxLLMProvider.SelectedIndex = 0;
                        break;
                }

                // 加载各提供商的配置
                TextBoxOpenAIKey.Text = settings.EmotionAnalysis.OpenAIApiKey ?? "";
                TextBoxOpenAIBaseUrl.Text = settings.EmotionAnalysis.OpenAIBaseUrl ?? "https://api.openai.com/v1";
                ComboBoxOpenAIModel.Text = settings.EmotionAnalysis.OpenAIModel ?? "gpt-3.5-turbo";

                TextBoxGeminiKey.Text = settings.EmotionAnalysis.GeminiApiKey ?? "";
                TextBoxGeminiBaseUrl.Text = settings.EmotionAnalysis.GeminiBaseUrl ?? "https://generativelanguage.googleapis.com/v1beta";
                ComboBoxGeminiModel.Text = settings.EmotionAnalysis.GeminiModel ?? "gemini-pro";

                TextBoxOllamaBaseUrl.Text = settings.EmotionAnalysis.OllamaBaseUrl ?? "http://localhost:11434";
                ComboBoxOllamaModel.Text = settings.EmotionAnalysis.OllamaModel ?? "llama2";

                // 加载代理设置
                var proxyMode = settings.EmotionAnalysis.ProxyMode ?? "System";
                foreach (ComboBoxItem item in ComboBoxProxyMode.Items)
                {
                    if ((string)item.Tag == proxyMode)
                    {
                        ComboBoxProxyMode.SelectedItem = item;
                        break;
                    }
                }
                if (ComboBoxProxyMode.SelectedItem == null)
                {
                    ComboBoxProxyMode.SelectedIndex = 0;
                }
                TextBoxProxyAddress.Text = settings.EmotionAnalysis.ProxyAddress ?? "";
                UpdateProxyAddressVisibility(proxyMode);
            }

            // 加载AI图片标签生成设置
            CheckBoxAIImageTagging.IsChecked = settings.EnableAIImageTagging;

            // 加载在线表情包设置
            if (settings.OnlineSticker != null)
            {
                CheckBoxOnlineSticker.IsChecked = settings.OnlineSticker.IsEnabled;
                CheckBoxUseBuiltInCredentials.IsChecked = settings.OnlineSticker.UseBuiltInCredentials;
                TextBoxOnlineServiceUrl.Text = settings.OnlineSticker.ServiceUrl ?? "";
                TextBoxOnlineApiKey.Text = settings.OnlineSticker.ApiKey ?? "";
                SliderOnlineDisplayDuration.Value = settings.OnlineSticker.DisplayDurationSeconds;
                SliderOnlineTagCount.Value = settings.OnlineSticker.TagCount;
                SliderOnlineCacheDuration.Value = settings.OnlineSticker.CacheDurationMinutes;
                CheckBoxOnlinePreferOnline.IsChecked = settings.OnlineSticker.PreferOnlineStickers;
                CheckBoxOnlineInEmotion.IsChecked = settings.OnlineSticker.EnableInEmotionAnalysis;
                CheckBoxOnlineInRandom.IsChecked = settings.OnlineSticker.EnableInRandomDisplay;
                CheckBoxOnlineInBubble.IsChecked = settings.OnlineSticker.EnableInBubbleTrigger;
            }

            // 更新UI显示状态
            UpdateTriggerModeUI();
            UpdateLLMProviderUI();
            UpdateOnlineStickerUI();

            // 预加载Free配置信息（异步，不阻塞UI）
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new System.Action(() =>
                    {
                        LoadFreeConfigInfo();
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
                catch { }
            });
        }

        private void UpdateImagePath()
        {
            if (TextBlockImagePath != null)
            {
                TextBlockImagePath.Text = Utils.DiyStickerStorage.RootPath;
            }
        }

        /// <summary>
        /// 更新触发模式UI显示状态
        /// </summary>
        private void UpdateTriggerModeUI()
        {
            // 根据开关状态调整设置区域的可见性和可用性
            bool useTimeTrigger = CheckBoxTimeTrigger.IsChecked == true;
            bool useBubbleTrigger = CheckBoxBubbleTrigger.IsChecked == true;

            // 时间触发设置区域
            TimeTriggerSettings.IsEnabled = useTimeTrigger;
            TimeTriggerSettings.Opacity = useTimeTrigger ? 1.0 : 0.5;

            // 气泡触发设置区域
            BubbleTriggerSettings.IsEnabled = useBubbleTrigger;
            BubbleTriggerSettings.Opacity = useBubbleTrigger ? 1.0 : 0.5;
        }

        /// <summary>
        /// 更新在线表情包UI显示状态
        /// </summary>
        private void UpdateOnlineStickerUI()
        {
            try
            {
                bool isOnlineEnabled = settings?.OnlineSticker?.IsEnabled == true;
                bool useBuiltInCredentials = settings?.OnlineSticker?.UseBuiltInCredentials == true;

                // 更新主要配置组的启用状态
                if (GroupBoxOnlineStickerConfig != null)
                {
                    GroupBoxOnlineStickerConfig.IsEnabled = isOnlineEnabled;
                    GroupBoxOnlineStickerConfig.Opacity = isOnlineEnabled ? 1.0 : 0.5;
                }

                if (GroupBoxOnlineStickerDisplay != null)
                {
                    GroupBoxOnlineStickerDisplay.IsEnabled = isOnlineEnabled;
                    GroupBoxOnlineStickerDisplay.Opacity = isOnlineEnabled ? 1.0 : 0.5;
                }

                if (GroupBoxOnlineStickerUsage != null)
                {
                    GroupBoxOnlineStickerUsage.IsEnabled = isOnlineEnabled;
                    GroupBoxOnlineStickerUsage.Opacity = isOnlineEnabled ? 1.0 : 0.5;
                }

                if (GroupBoxOnlineStickerTest != null)
                {
                    GroupBoxOnlineStickerTest.IsEnabled = isOnlineEnabled;
                    GroupBoxOnlineStickerTest.Opacity = isOnlineEnabled ? 1.0 : 0.5;
                }

                // 更新自定义服务配置的显示状态
                if (PanelCustomService != null)
                {
                    PanelCustomService.Visibility = (isOnlineEnabled && !useBuiltInCredentials) ? Visibility.Visible : Visibility.Collapsed;
                }

                // 更新状态文本
                if (TextBlockOnlineStatus != null)
                {
                    if (isOnlineEnabled)
                    {
                        TextBlockOnlineStatus.Text = useBuiltInCredentials ? "使用内置凭证" : "使用自定义服务";
                    }
                    else
                    {
                        TextBlockOnlineStatus.Text = "功能已禁用";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新在线表情包UI失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新LLM提供商UI显示状态
        /// </summary>
        private void UpdateLLMProviderUI()
        {
            if (ComboBoxLLMProvider.SelectedItem is ComboBoxItem selectedItem)
            {
                string providerTag = selectedItem.Tag?.ToString()?.ToLowerInvariant() ?? "openai";

                // 根据选择的提供商显示对应的配置面板
                PanelOpenAI.Visibility = providerTag == "openai" ? Visibility.Visible : Visibility.Collapsed;
                PanelGemini.Visibility = providerTag == "gemini" ? Visibility.Visible : Visibility.Collapsed;
                PanelOllama.Visibility = providerTag == "ollama" ? Visibility.Visible : Visibility.Collapsed;
                PanelFree.Visibility = providerTag == "free" ? Visibility.Visible : Visibility.Collapsed;

                // 如果切换到Free提供商，加载Free配置信息
                if (providerTag == "free")
                {
                    LoadFreeConfigInfo();
                }
            }
        }

        /// <summary>
        /// 加载Free配置信息（描述和提供者）
        /// </summary>
        private void LoadFreeConfigInfo()
        {
            try
            {
                var freeClient = new EmotionAnalysis.LLMClient.FreeClient(imageMgr: imageMgr);

                if (TextBlockFreeDescription != null)
                {
                    TextBlockFreeDescription.Text = "ℹ️ " + freeClient.GetDescription();
                }

                if (TextBlockFreeProvider != null)
                {
                    TextBlockFreeProvider.Text = freeClient.GetProvider();
                }
            }
            catch (Exception ex)
            {
                imageMgr?.LogDebug("ImageSettingWindow", $"加载Free配置信息失败: {ex.Message}");
            }
        }

        // 基本设置事件处理
        private void CheckBoxEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null && sender is CheckBox checkBox)
            {
                settings.IsEnabled = checkBox.IsChecked == true;
            }
        }

        private void CheckBoxBuiltInImages_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null && sender is CheckBox checkBox)
            {
                settings.EnableBuiltInImages = checkBox.IsChecked == true;
            }
        }

        private void CheckBoxDIYImages_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null && sender is CheckBox checkBox)
            {
                settings.EnableDIYImages = checkBox.IsChecked == true;
            }
        }

        private void CheckBoxTimeTrigger_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null && sender is CheckBox checkBox)
            {
                settings.UseTimeTrigger = checkBox.IsChecked == true;
                UpdateTriggerModeUI();
            }
        }

        private void CheckBoxBubbleTrigger_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null && sender is CheckBox checkBox)
            {
                settings.UseBubbleTrigger = checkBox.IsChecked == true;
                UpdateTriggerModeUI();
            }
        }

        private void SliderDisplayDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (settings != null && sender is Slider slider)
            {
                settings.DisplayDuration = (int)slider.Value;
            }
        }

        private void SliderDisplayInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (settings != null && sender is Slider slider)
            {
                settings.DisplayInterval = (int)slider.Value;
            }
        }

        private void CheckBoxRandomInterval_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null && sender is CheckBox checkBox)
            {
                settings.UseRandomInterval = checkBox.IsChecked == true;
            }
        }

        private void SliderBubbleTriggerProbability_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (settings != null && sender is Slider slider)
            {
                settings.BubbleTriggerProbability = (int)slider.Value;
            }
        }

        private void CheckBoxDebugMode_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null && sender is CheckBox checkBox)
            {
                settings.DebugMode = checkBox.IsChecked == true;
            }
        }

        private void CheckBoxEmotionAnalysis_Changed(object sender, RoutedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && sender is CheckBox checkBox)
            {
                settings.EmotionAnalysis.EnableLLMEmotionAnalysis = checkBox.IsChecked == true;
            }
        }

        private void CheckBoxAccurateImageMatching_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null && sender is CheckBox checkBox)
            {
                settings.UseAccurateImageMatching = checkBox.IsChecked == true;
            }
        }

        private void CheckBoxVisionModel_Changed(object sender, RoutedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && sender is CheckBox checkBox)
            {
                settings.EmotionAnalysis.IsVisionModel = checkBox.IsChecked == true;
            }
        }

        private void ComboBoxProxyMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis == null || ComboBoxProxyMode.SelectedItem is not ComboBoxItem item)
                return;

            var mode = (string)item.Tag;
            settings.EmotionAnalysis.ProxyMode = mode;
            UpdateProxyAddressVisibility(mode);
        }

        private void TextBoxProxyAddress_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && sender is TextBox textBox)
            {
                settings.EmotionAnalysis.ProxyAddress = textBox.Text;
            }
        }

        private void UpdateProxyAddressVisibility(string mode)
        {
            var visibility = mode == "Custom" ? Visibility.Visible : Visibility.Collapsed;
            TextBlockProxyAddress.Visibility = visibility;
            TextBoxProxyAddress.Visibility = visibility;
        }

        private void CheckBoxAIImageTagging_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null && sender is CheckBox checkBox)
            {
                settings.EnableAIImageTagging = checkBox.IsChecked == true;
            }
        }

        private void ComboBoxLLMProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis == null || !(sender is ComboBox comboBox) || comboBox.SelectedItem == null)
                return;

            var selectedItem = comboBox.SelectedItem as ComboBoxItem;
            if (selectedItem != null)
            {
                string providerTag = selectedItem.Tag?.ToString()?.ToLowerInvariant() ?? "openai";

                // 转换为枚举
                switch (providerTag)
                {
                    case "openai":
                        settings.EmotionAnalysis.Provider = EmotionAnalysis.LLMProvider.OpenAI;
                        break;
                    case "gemini":
                        settings.EmotionAnalysis.Provider = EmotionAnalysis.LLMProvider.Gemini;
                        break;
                    case "ollama":
                        settings.EmotionAnalysis.Provider = EmotionAnalysis.LLMProvider.Ollama;
                        break;
                    case "free":
                        settings.EmotionAnalysis.Provider = EmotionAnalysis.LLMProvider.Free;
                        break;
                }

                UpdateLLMProviderUI();
            }
        }

        // 日志等级控制事件处理
        private void ComboBoxLogLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (settings != null && sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                if (int.TryParse(selectedItem.Tag.ToString(), out int logLevel))
                {
                    settings.LogLevel = logLevel;

                    // 更新静态日志系统
                    Utils.Logger.SetLogLevel((VPet.Plugin.LLMEP.Utils.LogLevel)logLevel);
                }
            }
        }

        private void SwitchFileLogging_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null && sender is CheckBox checkBox)
            {
                settings.EnableFileLogging = checkBox.IsChecked == true;

                // 更新静态日志系统
                Utils.Logger.EnableFileLogging = settings.EnableFileLogging;
            }
        }

        // 在线表情包设置事件处理
        private void CheckBoxOnlineSticker_Changed(object sender, RoutedEventArgs e)
        {
            if (settings?.OnlineSticker != null && sender is CheckBox checkBox)
            {
                settings.OnlineSticker.IsEnabled = checkBox.IsChecked == true;
                UpdateOnlineStickerUI();
            }
        }

        private void CheckBoxUseBuiltInCredentials_Changed(object sender, RoutedEventArgs e)
        {
            if (settings?.OnlineSticker != null && sender is CheckBox checkBox)
            {
                settings.OnlineSticker.UseBuiltInCredentials = checkBox.IsChecked == true;
                UpdateOnlineStickerUI();
            }
        }

        private void TextBoxOnlineServiceUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (settings?.OnlineSticker != null && sender is TextBox textBox)
            {
                settings.OnlineSticker.ServiceUrl = textBox.Text;
            }
        }

        private void TextBoxOnlineApiKey_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (settings?.OnlineSticker != null && sender is TextBox textBox)
            {
                settings.OnlineSticker.ApiKey = textBox.Text;
            }
        }

        private void SliderOnlineDisplayDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (settings?.OnlineSticker != null && sender is Slider slider)
            {
                settings.OnlineSticker.DisplayDurationSeconds = (int)slider.Value;
            }
        }

        private void SliderOnlineTagCount_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (settings?.OnlineSticker != null && sender is Slider slider)
            {
                settings.OnlineSticker.TagCount = (int)slider.Value;
            }
        }

        private void SliderOnlineCacheDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (settings?.OnlineSticker != null && sender is Slider slider)
            {
                settings.OnlineSticker.CacheDurationMinutes = (int)slider.Value;
            }
        }

        private void CheckBoxOnlinePreferOnline_Changed(object sender, RoutedEventArgs e)
        {
            if (settings?.OnlineSticker != null && sender is CheckBox checkBox)
            {
                settings.OnlineSticker.PreferOnlineStickers = checkBox.IsChecked == true;
            }
        }

        private void CheckBoxOnlineInEmotion_Changed(object sender, RoutedEventArgs e)
        {
            if (settings?.OnlineSticker != null && sender is CheckBox checkBox)
            {
                settings.OnlineSticker.EnableInEmotionAnalysis = checkBox.IsChecked == true;
            }
        }

        private void CheckBoxOnlineInRandom_Changed(object sender, RoutedEventArgs e)
        {
            if (settings?.OnlineSticker != null && sender is CheckBox checkBox)
            {
                settings.OnlineSticker.EnableInRandomDisplay = checkBox.IsChecked == true;
            }
        }

        private void CheckBoxOnlineInBubble_Changed(object sender, RoutedEventArgs e)
        {
            if (settings?.OnlineSticker != null && sender is CheckBox checkBox)
            {
                settings.OnlineSticker.EnableInBubbleTrigger = checkBox.IsChecked == true;
            }
        }

        private async void ButtonTestOnlineConnection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                button.IsEnabled = false;
                string originalContent = button.Content?.ToString();
                button.Content = "⏳ 测试中...";

                try
                {
                    // 先应用当前设置
                    imageMgr.ApplySettings(settings);

                    // 测试连接
                    bool result = await imageMgr.TestOnlineStickerConnectionAsync();

                    if (result)
                    {
                        if (TextBlockOnlineStatus != null)
                            TextBlockOnlineStatus.Text = "✅ 连接成功";
                        MessageBox.Show("在线表情包服务连接成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        if (TextBlockOnlineStatus != null)
                            TextBlockOnlineStatus.Text = "❌ 连接失败";
                        MessageBox.Show("在线表情包服务连接失败，请检查网络和配置。", "失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    if (TextBlockOnlineStatus != null)
                        TextBlockOnlineStatus.Text = "❌ 连接异常";
                    MessageBox.Show($"测试连接时出现异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    button.IsEnabled = true;
                    button.Content = originalContent;
                }
            }
        }

        private async void ButtonTestOnlineSticker_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                button.IsEnabled = false;
                string originalContent = button.Content?.ToString();
                button.Content = "⏳ 测试中...";

                try
                {
                    // 先应用当前设置
                    imageMgr.ApplySettings(settings);

                    // 测试显示在线表情包
                    bool result = await imageMgr.ShowOnlineRandomStickerAsync();

                    if (result)
                    {
                        if (TextBlockOnlineStatus != null)
                            TextBlockOnlineStatus.Text = "✅ 表情包显示成功";
                        MessageBox.Show("在线表情包测试成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        if (TextBlockOnlineStatus != null)
                            TextBlockOnlineStatus.Text = "❌ 表情包显示失败";
                        MessageBox.Show("在线表情包测试失败，请检查服务连接和配置。", "失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    if (TextBlockOnlineStatus != null)
                        TextBlockOnlineStatus.Text = "❌ 测试异常";
                    MessageBox.Show($"测试在线表情包时出现异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    button.IsEnabled = true;
                    button.Content = originalContent;
                }
            }
        }

        // LLM配置事件处理
        private void TextBoxOpenAIKey_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && sender is TextBox textBox)
            {
                settings.EmotionAnalysis.OpenAIApiKey = textBox.Text;
            }
        }

        private void TextBoxOpenAIBaseUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && sender is TextBox textBox)
            {
                settings.EmotionAnalysis.OpenAIBaseUrl = textBox.Text;
            }
        }

        private void ComboBoxOpenAIModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && sender is ComboBox comboBox && comboBox.SelectedItem != null)
            {
                var selectedItem = comboBox.SelectedItem as ComboBoxItem;
                if (selectedItem != null)
                {
                    settings.EmotionAnalysis.OpenAIModel = selectedItem.Content?.ToString();
                }
            }
        }

        private void ComboBoxOpenAIModel_LostFocus(object sender, RoutedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && sender is ComboBox comboBox)
            {
                settings.EmotionAnalysis.OpenAIModel = comboBox.Text;
            }
        }

        private async void ButtonFetchOpenAIModels_Click(object sender, RoutedEventArgs e)
        {
            await FetchModelsAsync(
                LLMProvider.OpenAI,
                TextBoxOpenAIKey.Text?.Trim(),
                TextBoxOpenAIBaseUrl.Text?.Trim(),
                ComboBoxOpenAIModel,
                sender as Button,
                "https://api.openai.com/v1"
            );
        }

        private void TextBoxGeminiKey_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && sender is TextBox textBox)
            {
                settings.EmotionAnalysis.GeminiApiKey = textBox.Text;
            }
        }

        private void TextBoxGeminiBaseUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && sender is TextBox textBox)
            {
                settings.EmotionAnalysis.GeminiBaseUrl = textBox.Text;
            }
        }

        private void ComboBoxGeminiModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && sender is ComboBox comboBox && comboBox.SelectedItem != null)
            {
                var selectedItem = comboBox.SelectedItem as ComboBoxItem;
                if (selectedItem != null)
                {
                    settings.EmotionAnalysis.GeminiModel = selectedItem.Content?.ToString();
                }
            }
        }

        private void ComboBoxGeminiModel_LostFocus(object sender, RoutedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && sender is ComboBox comboBox)
            {
                settings.EmotionAnalysis.GeminiModel = comboBox.Text;
            }
        }

        private async void ButtonFetchGeminiModels_Click(object sender, RoutedEventArgs e)
        {
            await FetchModelsAsync(
                LLMProvider.Gemini,
                TextBoxGeminiKey.Text?.Trim(),
                TextBoxGeminiBaseUrl.Text?.Trim(),
                ComboBoxGeminiModel,
                sender as Button,
                "https://generativelanguage.googleapis.com/v1beta"
            );
        }

        private void TextBoxOllamaBaseUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && sender is TextBox textBox)
            {
                settings.EmotionAnalysis.OllamaBaseUrl = textBox.Text;
            }
        }

        private void ComboBoxOllamaModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && sender is ComboBox comboBox && comboBox.SelectedItem != null)
            {
                var selectedItem = comboBox.SelectedItem as ComboBoxItem;
                if (selectedItem != null)
                {
                    settings.EmotionAnalysis.OllamaModel = selectedItem.Content?.ToString();
                }
            }
        }

        private void ComboBoxOllamaModel_LostFocus(object sender, RoutedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && sender is ComboBox comboBox)
            {
                settings.EmotionAnalysis.OllamaModel = comboBox.Text;
            }
        }

        private async void ButtonFetchOllamaModels_Click(object sender, RoutedEventArgs e)
        {
            await FetchModelsAsync(
                LLMProvider.Ollama,
                null, // Ollama不需要API Key
                TextBoxOllamaBaseUrl.Text?.Trim(),
                ComboBoxOllamaModel,
                sender as Button,
                "http://localhost:11434"
            );
        }

        // 按钮事件处理
        private void ButtonOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 目录在 文档\VPetLLM\Emotion，不存在就建出来再打开
                string expressionPath = Utils.DiyStickerStorage.EnsureRoot();

                if (Directory.Exists(expressionPath))
                {
                    Process.Start("explorer.exe", expressionPath);
                }
                else
                {
                    MessageBox.Show($"无法创建表情包目录：{expressionPath}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开目录：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ButtonClearLog_Click(object sender, RoutedEventArgs e)
        {
            imageMgr.ClearLogs();
            if (TextBoxLog != null)
            {
                TextBoxLog.Clear();
                TextBoxLog.Text = "日志已清空。\n";
            }
        }

        private void ButtonSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 应用设置
                imageMgr.ApplySettings(settings);

                // 保存设置到文件
                imageMgr.SaveSettings();

                // 显示成功提示（不关闭窗口）
                MessageBox.Show("设置已保存！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);

                // 更新原始设置，避免关闭时提示未保存
                originalSettings = settings.Clone();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存设置失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ButtonReset_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确定要重置为默认设置吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                settings = new ImageSettings(); // 创建默认设置
                LoadSettings();
            }
        }

        private void ButtonCancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void ButtonTestDisplay_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 先应用当前设置（但不保存到文件）
                imageMgr.ApplySettings(settings);

                // 调用手动显示方法
                imageMgr.TestDisplayImage();

                // 提示用户
                imageMgr.LogMessage("测试显示：已触发表情包显示");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"测试显示失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                imageMgr.LogMessage($"测试显示失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 统一的模型获取方法
        /// </summary>
        private async System.Threading.Tasks.Task FetchModelsAsync(
            LLMProvider provider,
            string apiKey,
            string baseUrl,
            ComboBox comboBox,
            Button button,
            string defaultUrl)
        {
            // 验证 API Key（Ollama 和 Free 除外）
            if (provider != LLMProvider.Ollama && provider != LLMProvider.Free && string.IsNullOrEmpty(apiKey))
            {
                MessageBox.Show("请先输入 API Key", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 使用默认 URL 如果用户没有填写
            if (string.IsNullOrEmpty(baseUrl))
            {
                baseUrl = defaultUrl;
            }

            button.IsEnabled = false;
            string originalContent = button.Content?.ToString();
            button.Content = "⏳ 获取中...";

            try
            {
                // 应用当前代理设置后再创建客户端
                if (settings?.EmotionAnalysis != null)
                {
                    EmotionAnalysis.LLMHttpClientFactory.Configure(settings.EmotionAnalysis);
                }

                // 创建对应的客户端
                ILLMClient client = provider switch
                {
                    LLMProvider.OpenAI => new OpenAIClient(apiKey, baseUrl, imageMgr: imageMgr),
                    LLMProvider.Gemini => new GeminiClient(apiKey, baseUrl, imageMgr: imageMgr),
                    LLMProvider.Ollama => new OllamaClient(baseUrl, imageMgr: imageMgr),
                    LLMProvider.Free => new FreeClient(imageMgr: imageMgr),
                    _ => throw new NotSupportedException($"不支持的提供商: {provider}")
                };

                // 获取模型列表
                var models = await client.GetAvailableModelsAsync();

                // 更新下拉框
                comboBox.Items.Clear();
                foreach (var model in models)
                {
                    var item = new ComboBoxItem
                    {
                        Content = model.Name,
                        ToolTip = string.IsNullOrEmpty(model.Description) ? model.Id : $"{model.Id}\n{model.Description}"
                    };
                    comboBox.Items.Add(item);
                }

                if (comboBox.Items.Count > 0)
                {
                    comboBox.SelectedIndex = 0;
                    MessageBox.Show($"成功获取 {models.Count} 个模型", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("未找到可用模型", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                HandleFetchModelsError(ex, provider);
            }
            finally
            {
                button.IsEnabled = true;
                button.Content = originalContent;
            }
        }

        /// <summary>
        /// 处理获取模型列表时的错误
        /// </summary>
        private void HandleFetchModelsError(Exception ex, LLMProvider provider)
        {
            var errorMsg = ex.Message.ToLower();

            // 判断是否为端点不支持错误
            bool isEndpointNotSupported = errorMsg.Contains("404") ||
                                          errorMsg.Contains("无法访问") ||
                                          errorMsg.Contains("not found") ||
                                          errorMsg.Contains("不支持");

            if (isEndpointNotSupported && provider == LLMProvider.OpenAI)
            {
                // OpenAI 兼容 API 的特殊提示
                string commonModels = "• OpenAI: gpt-3.5-turbo, gpt-4, gpt-4-turbo-preview\n" +
                                     "• Claude: claude-3-opus, claude-3-sonnet, claude-3-haiku\n" +
                                     "• 国内: qwen-turbo, qwen-max, glm-4, moonshot-v1-8k\n" +
                                     "• 开源: llama3, mistral, mixtral-8x7b";

                MessageBox.Show(
                    "当前 API 端点不支持自动获取模型列表，请手动输入模型名称。\n\n" +
                    "常用模型名称：\n" + commonModels + "\n\n" +
                    "提示：\n" +
                    "• OpenRouter: https://openrouter.ai/api/v1\n" +
                    "• OneAPI: http://your-domain/v1",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (provider == LLMProvider.Ollama)
            {
                MessageBox.Show($"获取模型列表失败：{ex.Message}\n\n请确保 Ollama 服务已启动", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show($"获取模型列表失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 初始化标签管理器
        /// </summary>
        private void InitializeLabelManager()
        {
            try
            {
                // DIY 图片和标签都在 文档\VPetLLM\Emotion
                Utils.DiyStickerStorage.EnsureRoot();
                labelManager = new LabelManager();
                labelManager.LoadLabels();

                allStickers = new List<StickerListItem>();
                editingItem = null;

                Utils.Logger.Debug("LabelManager", "标签管理器初始化完成");
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LabelManager", $"初始化标签管理器失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化AI标签生成服务
        /// </summary>
        private void InitializeAITaggingService()
        {
            try
            {
                string pluginDir = imageMgr.LoaddllPath();
                aiTaggingService = new LLMImageTaggingService(imageMgr, labelManager, pluginDir);

                // 订阅进度事件
                aiProgressHandler = (s, e) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (TextBlockAIProcessingStatus != null)
                        {
                            TextBlockAIProcessingStatus.Text = $"状态: {e.Status} ({e.CurrentIndex}/{e.TotalCount})";
                        }
                        if (TextBlockStatus != null)
                        {
                            TextBlockStatus.Text = $"AI处理中: {e.CurrentImage}";
                        }
                    }));
                };
                aiTaggingService.ProgressChanged += aiProgressHandler;

                // 订阅完成事件
                aiCompletedHandler = (s, e) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        isAIProcessing = false;
                        UpdateAIProcessingUI();

                        // 取消和跑完是两回事：取消时报"生成完成"会让用户以为
                        // 剩下的图片也处理过了
                        if (e.IsCancelled)
                        {
                            string cancelMessage = $"AI标签生成已取消。\n取消前已处理:\n成功: {e.SuccessCount} 张\n失败: {e.FailedCount} 张\n计划处理: {e.TotalCount} 张";
                            MessageBox.Show(cancelMessage, "已取消", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                        else
                        {
                            string message = $"AI标签生成完成！\n成功: {e.SuccessCount} 张\n失败: {e.FailedCount} 张\n总计: {e.TotalCount} 张";
                            MessageBox.Show(message, "处理完成", MessageBoxButton.OK, MessageBoxImage.Information);
                        }

                        if (TextBlockAIProcessingStatus != null)
                        {
                            TextBlockAIProcessingStatus.Text = e.IsCancelled
                                ? $"状态: 已取消 (成功 {e.SuccessCount}, 失败 {e.FailedCount})"
                                : $"状态: 处理完成 (成功 {e.SuccessCount}, 失败 {e.FailedCount})";
                        }
                        if (TextBlockStatus != null)
                        {
                            TextBlockStatus.Text = e.IsCancelled ? "AI处理已取消" : "AI处理完成";
                        }

                        // 刷新列表以显示新标签，并让桌宠用上新的心情设置
                        _ = RefreshImageListAsync("AI处理后已刷新");
                        imageMgr.ReloadStickerLibrary();
                    }));
                };
                aiTaggingService.ProcessingCompleted += aiCompletedHandler;

                Utils.Logger.Debug("LabelManager", "AI标签生成服务初始化完成");
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LabelManager", $"初始化AI标签生成服务失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新AI处理UI状态
        /// </summary>
        private void UpdateAIProcessingUI()
        {
            if (ButtonStartAIProcessing != null)
            {
                ButtonStartAIProcessing.Content = isAIProcessing ? "⏹️ 停止处理" : "🤖 开始AI处理";
            }
        }

        /// <summary>
        /// 开始/停止AI处理按钮点击事件
        /// </summary>
        private async void ButtonStartAIProcessing_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (isAIProcessing)
                {
                    // 停止处理
                    aiTaggingService?.StopProcessing();
                    isAIProcessing = false;
                    UpdateAIProcessingUI();

                    if (TextBlockAIProcessingStatus != null)
                    {
                        TextBlockAIProcessingStatus.Text = "状态: 已停止";
                    }
                    return;
                }

                // 检查是否启用了AI标签生成功能
                if (!settings.EnableAIImageTagging)
                {
                    MessageBox.Show("请先启用\"允许AI识别图片并生成标签\"选项", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 检查是否启用了视觉模型
                if (!settings.EmotionAnalysis.IsVisionModel)
                {
                    MessageBox.Show("请先在LLM设置中启用\"是可读取图片的模型(Vision)\"选项", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 检查LLM配置是否有效
                if (string.IsNullOrEmpty(settings.EmotionAnalysis.OpenAIApiKey) && 
                    string.IsNullOrEmpty(settings.EmotionAnalysis.GeminiApiKey) &&
                    settings.EmotionAnalysis.Provider != LLMProvider.Free)
                {
                    MessageBox.Show("请先配置有效的LLM API密钥", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 开始处理
                isAIProcessing = true;
                UpdateAIProcessingUI();

                if (TextBlockAIProcessingStatus != null)
                {
                    TextBlockAIProcessingStatus.Text = "状态: 准备开始...";
                }

                // 保存当前设置到ImageMgr
                imageMgr.ApplySettings(settings);

                // 异步启动处理
                await System.Threading.Tasks.Task.Run(async () =>
                {
                    await aiTaggingService.StartProcessingAsync(settings);
                });
            }
            catch (Exception ex)
            {
                isAIProcessing = false;
                UpdateAIProcessingUI();
                MessageBox.Show($"启动AI处理失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Utils.Logger.Error("LabelManager", $"启动AI处理失败: {ex.Message}");
            }
        }

        #region 表情包列表（单一文件夹 文档\VPetLLM\Emotion）

        /// <summary>
        /// 列表里的一张 DIY 表情包。缩略图在后台按 88px 解码后再填进来。
        /// </summary>
        public sealed class StickerListItem : INotifyPropertyChanged
        {
            private ImageSource _thumbnail;
            private string _moodSummary;

            public ImageInfo Info { get; init; }
            public string FileName => Info.FileName;

            public string MoodSummary
            {
                get => _moodSummary;
                set { _moodSummary = value; OnChanged(); }
            }

            public ImageSource Thumbnail
            {
                get => _thumbnail;
                set { _thumbnail = value; OnChanged(); }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void OnChanged([CallerMemberName] string name = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>心情复选框，按 LabelManager.MoodTags 的顺序。</summary>
        private IEnumerable<CheckBox> MoodCheckBoxes =>
            new[] { CheckBoxMoodHappy, CheckBoxMoodNormal, CheckBoxMoodPoor, CheckBoxMoodIll }.Where(cb => cb != null);

        private List<StickerListItem> SelectedStickers =>
            ListBoxImages?.SelectedItems.Cast<StickerListItem>().ToList() ?? new List<StickerListItem>();

        /// <summary>
        /// 重新扫描目录并刷新列表；保持原来的选中项。扫描和缩略图解码都在后台。
        /// </summary>
        private async System.Threading.Tasks.Task RefreshImageListAsync(string statusPrefix, IEnumerable<string> selectFileNames = null)
        {
            if (labelManager == null || ListBoxImages == null)
                return;

            CommitTagsEdit();
            int generation = ++scanGeneration;
            if (TextBlockStatus != null) TextBlockStatus.Text = "正在扫描图片...";

            List<ImageInfo> images;
            try
            {
                images = await System.Threading.Tasks.Task.Run(() => labelManager.ScanImages());
            }
            catch (Exception ex)
            {
                if (TextBlockStatus != null) TextBlockStatus.Text = "扫描失败";
                Utils.Logger.Error("LabelManager", $"扫描图片失败: {ex.Message}");
                return;
            }

            if (generation != scanGeneration)
                return; // 期间又触发了一次刷新，以那次为准

            var keepSelected = new HashSet<string>(
                selectFileNames ?? SelectedStickers.Select(i => i.FileName), StringComparer.OrdinalIgnoreCase);

            allStickers = images
                .Select(info => new StickerListItem { Info = info, MoodSummary = BuildMoodSummary(info.RelativePath) })
                .ToList();
            ApplyMoodFilter(keepSelected);

            if (TextBlockStatus != null)
                TextBlockStatus.Text = $"{statusPrefix}，共 {allStickers.Count} 张表情包（{Utils.DiyStickerStorage.RootPath}）";
            Utils.Logger.Info("LabelManager", $"{statusPrefix}: {allStickers.Count} 张图片");

            _ = LoadThumbnailsAsync(allStickers, generation);
        }

        /// <summary>
        /// 按"按心情筛选"下拉框过滤列表。
        /// </summary>
        private void ApplyMoodFilter(ISet<string> keepSelected)
        {
            if (ListBoxImages == null)
                return;

            var filter = (ComboBoxMoodFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
            IEnumerable<StickerListItem> items = allStickers;
            if (filter == LabelManager.GeneralTag)
                items = items.Where(i => labelManager.GetImageMoods(i.Info.RelativePath).Count == 0);
            else if (filter != "all")
                items = items.Where(i => labelManager.GetImageMoods(i.Info.RelativePath).Contains(filter));

            suppressSelectionEvents = true;
            try
            {
                var list = items.ToList();
                ListBoxImages.ItemsSource = list;
                if (keepSelected != null && keepSelected.Count > 0)
                {
                    foreach (var item in list.Where(i => keepSelected.Contains(i.FileName)))
                        ListBoxImages.SelectedItems.Add(item);
                    var first = list.FirstOrDefault(i => keepSelected.Contains(i.FileName));
                    if (first != null)
                        ListBoxImages.ScrollIntoView(first);
                }
            }
            finally
            {
                suppressSelectionEvents = false;
            }

            UpdateDetailsPanel();
        }

        private string BuildMoodSummary(string relativePath)
        {
            var moods = labelManager.GetImageMoods(relativePath);
            var summary = moods.Count == 0
                ? "泛用（所有心情）"
                : "心情: " + string.Join("、", moods.Select(LabelManager.MoodDisplayName));
            var tagCount = labelManager.GetImageNormalTags(relativePath).Count;
            return tagCount > 0 ? $"{summary} · {tagCount} 个标签" : summary;
        }

        /// <summary>
        /// 后台解码缩略图（88px，冻结后交给 UI），不在 UI 线程上解大图。
        /// </summary>
        private async System.Threading.Tasks.Task LoadThumbnailsAsync(List<StickerListItem> items, int generation)
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var item in items)
                {
                    if (generation != scanGeneration)
                        return;

                    var thumbnail = DecodeBitmap(item.Info.FullPath, 88);
                    if (thumbnail == null)
                        continue;

                    Dispatcher.BeginInvoke(new Action(() => item.Thumbnail = thumbnail), DispatcherPriority.Background);
                }
            });
        }

        private static BitmapSource DecodeBitmap(string path, int decodeWidth)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                bitmap.DecodePixelWidth = decodeWidth;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private void ListBoxImages_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressSelectionEvents)
                return;

            // 先把上一张的标签编辑落盘，再切换
            CommitTagsEdit();
            UpdateDetailsPanel();
        }

        private void ComboBoxMoodFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (labelManager == null || ListBoxImages == null)
                return;

            CommitTagsEdit();
            ApplyMoodFilter(new HashSet<string>(SelectedStickers.Select(i => i.FileName), StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 根据当前选中项刷新右侧面板。单选可编辑标签；多选时只批量设置心情。
        /// </summary>
        private void UpdateDetailsPanel()
        {
            var selected = SelectedStickers;
            if (selected.Count == 0)
            {
                editingItem = null;
                HideImageDetails();
                return;
            }

            if (PanelImageDetails != null) PanelImageDetails.Visibility = Visibility.Visible;
            if (PanelEmptyState != null) PanelEmptyState.Visibility = Visibility.Collapsed;

            var primary = ListBoxImages.SelectedItem as StickerListItem ?? selected[0];

            if (selected.Count == 1)
            {
                editingItem = primary;
                if (TextBlockImageTitle != null) TextBlockImageTitle.Text = $"🖼️ {primary.FileName}";
                if (TextBlockFileName != null) TextBlockFileName.Text = $"文件名: {primary.FileName}";
                if (TextBlockFileSize != null) TextBlockFileSize.Text = $"大小: {primary.Info.FormattedSize}";
                if (TextBoxImageTags != null)
                {
                    TextBoxImageTags.IsEnabled = true;
                    TextBoxImageTags.Text = string.Join(", ", labelManager.GetImageNormalTags(primary.Info.RelativePath));
                }
                if (TextBlockTagsHint != null) TextBlockTagsHint.Text = "用逗号分隔多个标签，如：开心,笑脸,高兴（用于情感分析精确匹配）";
                if (TextBlockStatus != null) TextBlockStatus.Text = $"正在编辑: {primary.FileName}";
            }
            else
            {
                editingItem = null;
                if (TextBlockImageTitle != null) TextBlockImageTitle.Text = $"🖼️ 已选择 {selected.Count} 张表情包";
                if (TextBlockFileName != null) TextBlockFileName.Text = "勾选或取消心情，会同时应用到所有选中的图片";
                if (TextBlockFileSize != null) TextBlockFileSize.Text = "";
                if (TextBoxImageTags != null)
                {
                    TextBoxImageTags.IsEnabled = false;
                    TextBoxImageTags.Text = "";
                }
                if (TextBlockTagsHint != null) TextBlockTagsHint.Text = "多选时不能编辑标签，请单独选择一张图片";
                if (TextBlockStatus != null) TextBlockStatus.Text = $"已选择 {selected.Count} 张表情包";
            }

            // 心情复选框：全部都有=勾，全部都没有=不勾，部分有=半选
            foreach (var cb in MoodCheckBoxes)
            {
                var mood = cb.Tag?.ToString();
                int count = selected.Count(i => labelManager.GetImageMoods(i.Info.RelativePath).Contains(mood));
                cb.IsChecked = count == 0 ? false : count == selected.Count ? true : (bool?)null;
            }

            ShowPreview(primary);
        }

        /// <summary>
        /// 预览图在后台按 480px 解码；先用缩略图占位，解完且仍是这张时再换上。
        /// </summary>
        private async void ShowPreview(StickerListItem item)
        {
            if (ImagePreview == null)
                return;

            int token = ++previewGeneration;
            ImagePreview.Source = item.Thumbnail;

            var preview = await System.Threading.Tasks.Task.Run(() => DecodeBitmap(item.Info.FullPath, 480));
            if (token == previewGeneration && preview != null)
                ImagePreview.Source = preview;
        }

        /// <summary>
        /// 勾选/取消一个心情：应用到所有选中的图片，立即保存并让桌宠生效。
        /// 用 Click 而不是 Checked/Unchecked：程序里设置 IsChecked 不会误触发保存。
        /// </summary>
        private void MoodCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb || labelManager == null)
                return;

            var mood = cb.Tag?.ToString();
            var selected = SelectedStickers;
            if (string.IsNullOrEmpty(mood) || selected.Count == 0)
                return;

            bool enable = cb.IsChecked == true;
            cb.IsChecked = enable; // 半选状态点一下就变成全选

            foreach (var item in selected)
            {
                var moods = labelManager.GetImageMoods(item.Info.RelativePath);
                if (enable && !moods.Contains(mood))
                    moods.Add(mood);
                else if (!enable)
                    moods.Remove(mood);
                labelManager.SetImageMoods(item.Info.RelativePath, moods);
                item.MoodSummary = BuildMoodSummary(item.Info.RelativePath);
            }

            SaveLabelsAndApply(
                $"已{(enable ? "设置" : "取消")}「{LabelManager.MoodDisplayName(mood)}」：{selected.Count} 张表情包");
        }

        /// <summary>
        /// 把标签文本框的修改写回（切换选中、失去焦点、保存、关窗时调用）。没改动就什么也不做。
        /// </summary>
        private void CommitTagsEdit()
        {
            var item = editingItem;
            if (item == null || TextBoxImageTags == null || !TextBoxImageTags.IsEnabled || labelManager == null)
                return;

            var newTags = (TextBoxImageTags.Text ?? "")
                .Split(new[] { ',', '，', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct()
                .ToList();
            var oldTags = labelManager.GetImageNormalTags(item.Info.RelativePath);

            if (newTags.OrderBy(t => t).SequenceEqual(oldTags.OrderBy(t => t)))
                return;

            labelManager.SetImageNormalTags(item.Info.RelativePath, newTags);
            item.MoodSummary = BuildMoodSummary(item.Info.RelativePath);
            SaveLabelsAndApply($"已保存标签: {item.FileName}");
        }

        private void TextBoxImageTags_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitTagsEdit();
        }

        /// <summary>
        /// 写标签文件并让 ImageMgr 重新加载表情包库，改动立刻对桌宠生效。
        /// </summary>
        private bool SaveLabelsAndApply(string status)
        {
            try
            {
                labelManager.SaveLabels();
                imageMgr.ReloadStickerLibrary();
                if (TextBlockStatus != null) TextBlockStatus.Text = status;
                return true;
            }
            catch (Exception ex)
            {
                if (TextBlockStatus != null) TextBlockStatus.Text = "保存失败";
                Utils.Logger.Error("LabelManager", $"保存标签失败: {ex.Message}");
                MessageBox.Show($"保存标签失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// 刷新按钮
        /// </summary>
        private async void ButtonScanImages_Click(object sender, RoutedEventArgs e)
        {
            await RefreshImageListAsync("刷新完成");
        }

        /// <summary>
        /// 保存按钮：标签和心情本来就是改完即存，这里兜底把文本框里未提交的修改写进去。
        /// </summary>
        private void ButtonSaveLabels_Click(object sender, RoutedEventArgs e)
        {
            CommitTagsEdit();
            if (SaveLabelsAndApply("标签保存成功"))
            {
                MessageBox.Show("标签已成功保存到文件！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                Utils.Logger.Info("LabelManager", "用户保存标签成功");
            }
        }

        /// <summary>
        /// 导入图片：复制进 文档\VPetLLM\Emotion，导入后默认是泛用，选中它们方便直接勾心情。
        /// </summary>
        private async void ButtonImportImages_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要导入的表情包",
                Filter = "表情包图片|*.png;*.gif;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) != true)
                return;

            var imported = new List<string>();
            var failed = new List<string>();
            foreach (var file in dialog.FileNames)
            {
                if (!Utils.DiyStickerStorage.IsImageFile(file))
                {
                    failed.Add($"{Path.GetFileName(file)}（不支持的格式）");
                    continue;
                }
                try
                {
                    imported.Add(Utils.DiyStickerStorage.Import(file));
                }
                catch (Exception ex)
                {
                    failed.Add($"{Path.GetFileName(file)}（{ex.Message}）");
                }
            }

            if (imported.Count > 0)
            {
                // 回到"全部"，否则新导入的泛用图可能被当前筛选藏起来
                if (ComboBoxMoodFilter != null) ComboBoxMoodFilter.SelectedIndex = 0;
                await RefreshImageListAsync($"已导入 {imported.Count} 张，默认为泛用，可在右侧勾选心情", imported);
                imageMgr.ReloadStickerLibrary();
            }

            if (failed.Count > 0)
            {
                MessageBox.Show("以下文件未能导入：\n" + string.Join("\n", failed), "导入", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 隐藏图片详情
        /// </summary>
        private void HideImageDetails()
        {
            if (PanelImageDetails != null) PanelImageDetails.Visibility = Visibility.Collapsed;
            if (PanelEmptyState != null) PanelEmptyState.Visibility = Visibility.Visible;
            if (TextBlockImageTitle != null) TextBlockImageTitle.Text = "🖼️ 选择图片查看预览";
            if (ImagePreview != null) ImagePreview.Source = null;
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            // 标签文本框里还没失焦的修改，关窗时也要落盘
            CommitTagsEdit();

            base.OnClosed(e);

            // 停止定时器
            if (logUpdateTimer != null)
            {
                logUpdateTimer.Stop();
                logUpdateTimer = null;
            }

            // 取消仍在运行的 AI 批量打标。
            //
            // 不取消的话，任务会继续跑（每张图之间还有 3 秒间隔），并通过
            // ProgressChanged / ProcessingCompleted 上的匿名 lambda 把整个窗口
            // 一直留在内存里；跑完还会从这个已经关闭的窗口弹出"处理完成"对话框。
            if (aiTaggingService != null)
            {
                if (aiTaggingService.IsProcessing)
                {
                    aiTaggingService.StopProcessing();
                    Utils.Logger.Info("LabelManager", "设置窗口关闭，已取消进行中的 AI 标签生成");
                }

                // 必须解绑：取消只是让循环 break，之后仍会走到 OnProcessingCompleted，
                // 处理器还挂着的话就会从这个已关闭的窗口弹出"处理完成"对话框。
                if (aiProgressHandler != null)
                {
                    aiTaggingService.ProgressChanged -= aiProgressHandler;
                    aiProgressHandler = null;
                }
                if (aiCompletedHandler != null)
                {
                    aiTaggingService.ProcessingCompleted -= aiCompletedHandler;
                    aiCompletedHandler = null;
                }

                aiTaggingService = null;
            }

            // 如果用户没有保存，恢复原始设置
            if (!settings.Equals(imageMgr.Settings))
            {
                // 这里可以添加提示用户是否保存的逻辑
            }
        }
    }
}