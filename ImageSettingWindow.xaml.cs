using Panuon.WPF.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VPet.Plugin.LLMEP.EmotionAnalysis;
using VPet.Plugin.LLMEP.EmotionAnalysis.LLMClient;

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
        private Dictionary<string, List<ImageInfo>> scannedImages;
        private ImageInfo currentSelectedImage;

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

            // 加载设置到UI
            LoadSettings();

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
                    // 更新日志显示
                    TextBoxLog.Text = string.Join(Environment.NewLine, logs);

                    // 自动滚动到底部
                    LogScrollViewer?.ScrollToEnd();
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
            }

            // 更新UI显示状态
            UpdateTriggerModeUI();
            UpdateLLMProviderUI();
        }

        private void UpdateImagePath()
        {
            if (TextBlockImagePath != null)
            {
                try
                {
                    string dllPath = imageMgr.LoaddllPath();
                    string fullPath = Path.Combine(dllPath, "DIY_Expression");
                    TextBlockImagePath.Text = fullPath;
                }
                catch
                {
                    TextBlockImagePath.Text = "DIY_Expression/";
                }
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
                string dllPath = imageMgr.LoaddllPath();
                string expressionPath = Path.Combine(dllPath, "DIY_Expression");

                if (Directory.Exists(expressionPath))
                {
                    Process.Start("explorer.exe", expressionPath);
                }
                else
                {
                    MessageBox.Show("表情包目录不存在，请检查插件安装是否正确。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            // 验证 API Key（Ollama 除外）
            if (provider != LLMProvider.Ollama && string.IsNullOrEmpty(apiKey))
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
                // 创建对应的客户端
                ILLMClient client = provider switch
                {
                    LLMProvider.OpenAI => new OpenAIClient(apiKey, baseUrl, imageMgr: imageMgr),
                    LLMProvider.Gemini => new GeminiClient(apiKey, baseUrl, imageMgr: imageMgr),
                    LLMProvider.Ollama => new OllamaClient(baseUrl, imageMgr: imageMgr),
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

        #region 标签管理相关方法

        /// <summary>
        /// 初始化标签管理器
        /// </summary>
        private void InitializeLabelManager()
        {
            try
            {
                // 使用ImageMgr的LoaddllPath方法获取正确的插件根目录
                string pluginDir = imageMgr.LoaddllPath();
                labelManager = new LabelManager(pluginDir);
                labelManager.LoadLabels();
                labelManager.CreateEmptyLabelFileIfNotExists();
                labelManager.CreateExampleDirectories(); // 创建示例目录结构
                
                scannedImages = new Dictionary<string, List<ImageInfo>>();
                currentSelectedImage = null;
                
                Utils.Logger.Debug("LabelManager", "标签管理器初始化完成");
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LabelManager", $"初始化标签管理器失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 扫描图片按钮点击事件
        /// </summary>
        private void ButtonScanImages_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (TextBlockStatus != null) TextBlockStatus.Text = "正在扫描图片...";
                
                // 扫描图片
                scannedImages = labelManager.ScanImages();
                
                // 更新UI
                UpdateImageTree();
                
                int totalImages = scannedImages.Values.Sum(list => list.Count);
                if (TextBlockStatus != null) TextBlockStatus.Text = $"扫描完成，找到 {totalImages} 张图片，分布在 {scannedImages.Count} 个目录中";
                
                Utils.Logger.Info("LabelManager", $"用户扫描图片完成: {totalImages} 张图片");
            }
            catch (Exception ex)
            {
                if (TextBlockStatus != null) TextBlockStatus.Text = "扫描失败";
                MessageBox.Show($"扫描图片失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Utils.Logger.Error("LabelManager", $"扫描图片失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 保存标签按钮点击事件
        /// </summary>
        private void ButtonSaveLabels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 保存当前编辑的标签
                SaveCurrentImageTags();
                
                // 保存到文件
                labelManager.SaveLabels();
                
                if (TextBlockStatus != null) TextBlockStatus.Text = "标签保存成功";
                MessageBox.Show("标签已成功保存到文件！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                
                Utils.Logger.Info("LabelManager", "用户保存标签成功");
            }
            catch (Exception ex)
            {
                if (TextBlockStatus != null) TextBlockStatus.Text = "保存失败";
                MessageBox.Show($"保存标签失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Utils.Logger.Error("LabelManager", $"保存标签失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新图片树视图
        /// </summary>
        private void UpdateImageTree()
        {
            if (TreeViewImages == null) return;

            TreeViewImages.Items.Clear();
            
            if (scannedImages.Count == 0)
            {
                return;
            }

            foreach (var directory in scannedImages.Keys.OrderBy(k => k))
            {
                var dirItem = new TreeViewItem
                {
                    Header = $"📁 {directory} ({scannedImages[directory].Count})",
                    Tag = directory,
                    IsExpanded = true
                };

                foreach (var image in scannedImages[directory].OrderBy(img => img.FileName))
                {
                    var imageItem = new TreeViewItem
                    {
                        Header = $"🖼️ {image.FileName}",
                        Tag = image
                    };
                    
                    dirItem.Items.Add(imageItem);
                }
                
                TreeViewImages.Items.Add(dirItem);
            }
        }

        /// <summary>
        /// 图片树选择变化事件
        /// </summary>
        private void TreeViewImages_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            try
            {
                if (e.NewValue is TreeViewItem selectedItem && selectedItem.Tag is ImageInfo imageInfo)
                {
                    // 保存之前选中图片的标签
                    SaveCurrentImageTags();
                    
                    // 显示新选中的图片
                    ShowImageDetails(imageInfo);
                    currentSelectedImage = imageInfo;
                }
                else
                {
                    // 选中的是目录或其他项
                    HideImageDetails();
                    currentSelectedImage = null;
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LabelManager", $"选择图片时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示图片详情
        /// </summary>
        private void ShowImageDetails(ImageInfo imageInfo)
        {
            try
            {
                // 更新标题
                if (TextBlockImageTitle != null) TextBlockImageTitle.Text = $"🖼️ {imageInfo.FileName}";
                
                // 显示图片预览
                if (ImagePreview != null)
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(imageInfo.FullPath);
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    ImagePreview.Source = bitmap;
                }
                
                // 显示文件信息
                if (TextBlockFileName != null) TextBlockFileName.Text = $"文件名: {imageInfo.FileName}\n路径: {imageInfo.RelativePath}";
                if (TextBlockFileSize != null) TextBlockFileSize.Text = $"大小: {imageInfo.FormattedSize}";
                
                // 显示标签
                if (TextBoxImageTags != null)
                {
                    var tags = labelManager.GetImageTags(imageInfo.RelativePath);
                    // 分离心情标签和普通标签
                    var emotionTags = new[] { "general", "happy", "normal", "poor", "ill" };
                    var normalTags = tags.Where(tag => !emotionTags.Contains(tag.ToLower())).ToList();
                    var emotionTag = tags.FirstOrDefault(tag => emotionTags.Contains(tag.ToLower()));
                    
                    TextBoxImageTags.Text = string.Join(", ", normalTags);
                    
                    // 设置心情选择
                    if (ComboBoxEmotion != null)
                    {
                        var selectedIndex = emotionTag?.ToLower() switch
                        {
                            "happy" => 1,
                            "normal" => 2,
                            "poor" => 3,
                            "ill" => 4,
                            _ => 0 // general 或未设置
                        };
                        ComboBoxEmotion.SelectedIndex = selectedIndex;
                    }
                }
                
                // 显示详情面板
                if (PanelImageDetails != null) PanelImageDetails.Visibility = Visibility.Visible;
                if (PanelEmptyState != null) PanelEmptyState.Visibility = Visibility.Collapsed;
                
                if (TextBlockStatus != null) TextBlockStatus.Text = $"正在编辑: {imageInfo.FileName}";
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("LabelManager", $"显示图片详情失败: {ex.Message}");
                MessageBox.Show($"无法显示图片：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            if (TextBlockStatus != null) TextBlockStatus.Text = "准备就绪";
        }

        /// <summary>
        /// 保存当前图片的标签
        /// </summary>
        private void SaveCurrentImageTags()
        {
            if (currentSelectedImage != null)
            {
                try
                {
                    var allTags = new List<string>();
                    
                    // 添加普通标签
                    if (TextBoxImageTags != null && !string.IsNullOrEmpty(TextBoxImageTags.Text))
                    {
                        var tagsText = TextBoxImageTags.Text.Trim();
                        var normalTags = tagsText.Split(new char[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                                               .Select(tag => tag.Trim())
                                               .Where(tag => !string.IsNullOrEmpty(tag))
                                               .ToList();
                        allTags.AddRange(normalTags);
                    }
                    
                    // 添加心情标签
                    if (ComboBoxEmotion != null && ComboBoxEmotion.SelectedItem is ComboBoxItem selectedItem)
                    {
                        var emotionTag = selectedItem.Tag?.ToString();
                        if (!string.IsNullOrEmpty(emotionTag) && emotionTag != "general")
                        {
                            allTags.Add(emotionTag);
                        }
                    }
                    
                    labelManager.SetImageTags(currentSelectedImage.RelativePath, allTags);
                    currentSelectedImage.Tags = allTags;
                    
                    Utils.Logger.Debug("LabelManager", $"保存图片标签: {currentSelectedImage.FileName} -> [{string.Join(", ", allTags)}]");
                }
                catch (Exception ex)
                {
                    Utils.Logger.Error("LabelManager", $"保存当前图片标签失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 心情选择变化事件
        /// </summary>
        private void ComboBoxEmotion_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 当心情选择变化时，自动保存
            if (currentSelectedImage != null)
            {
                SaveCurrentImageTags();
            }
        }

        /// <summary>
        /// 图片标签文本变化事件
        /// </summary>
        private void TextBoxImageTags_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 实时保存标签变化（可选）
            // 这里可以添加防抖逻辑，避免频繁保存
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // 停止定时器
            if (logUpdateTimer != null)
            {
                logUpdateTimer.Stop();
                logUpdateTimer = null;
            }

            // 如果用户没有保存，恢复原始设置
            if (!settings.Equals(imageMgr.Settings))
            {
                // 这里可以添加提示用户是否保存的逻辑
            }
        }
    }
}