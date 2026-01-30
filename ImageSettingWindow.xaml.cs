using Panuon.WPF.UI;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using VPet.Plugin.Image.EmotionAnalysis;
using VPet.Plugin.Image.EmotionAnalysis.LLMClient;

namespace VPet.Plugin.Image
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

        public ImageSettingWindow(ImageMgr imageMgr)
        {
            InitializeComponent();
            this.imageMgr = imageMgr;
            this.settings = imageMgr.Settings.Clone();
            this.originalSettings = imageMgr.Settings.Clone();

            LoadSettings();
            UpdateImagePath();

            // 启动日志更新定时器
            logUpdateTimer = new DispatcherTimer();
            logUpdateTimer.Interval = TimeSpan.FromSeconds(1);
            logUpdateTimer.Tick += LogUpdateTimer_Tick;
            logUpdateTimer.Start();
        }

        private void LogUpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // 从 ImageMgr 获取日志
                var logs = imageMgr.GetLogMessages();

                if (logs.Count > 0)
                {
                    // 更新日志显示
                    TextBoxLog.Text = string.Join(Environment.NewLine, logs);

                    // 自动滚动到底部
                    LogScrollViewer.ScrollToEnd();
                }
                else
                {
                    if (string.IsNullOrEmpty(TextBoxLog.Text) || TextBoxLog.Text == "日志将显示在这里...")
                    {
                        TextBoxLog.Text = "暂无日志。\n\n提示：开启调试模式后，日志会实时显示在这里。";
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
            SwitchEnabled.IsChecked = settings.IsEnabled;
            SwitchBuiltInImages.IsChecked = settings.EnableBuiltInImages;
            SwitchDIYImages.IsChecked = settings.EnableDIYImages;
            SliderDisplayDuration.Value = settings.DisplayDuration;
            SliderDisplayInterval.Value = settings.DisplayInterval;
            SwitchRandomInterval.IsChecked = settings.UseRandomInterval;
            SwitchDebugMode.IsChecked = settings.DebugMode;

            // 加载LLM情感分析设置
            if (settings.EmotionAnalysis != null)
            {
                SwitchEmotionAnalysis.IsChecked = settings.EmotionAnalysis.EnableLLMEmotionAnalysis;

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
        }

        private void UpdateImagePath()
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

        private void SwitchEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null)
            {
                settings.IsEnabled = SwitchEnabled.IsChecked == true;
            }
        }

        private void SwitchBuiltInImages_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null)
            {
                settings.EnableBuiltInImages = SwitchBuiltInImages.IsChecked == true;
            }
        }

        private void SwitchDIYImages_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null)
            {
                settings.EnableDIYImages = SwitchDIYImages.IsChecked == true;
            }
        }

        private void SliderDisplayDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (settings != null)
            {
                settings.DisplayDuration = (int)SliderDisplayDuration.Value;
            }
        }

        private void SliderDisplayInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (settings != null)
            {
                settings.DisplayInterval = (int)SliderDisplayInterval.Value;
            }
        }

        private void SwitchRandomInterval_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null)
            {
                settings.UseRandomInterval = SwitchRandomInterval.IsChecked == true;
            }
        }

        private void SwitchDebugMode_Changed(object sender, RoutedEventArgs e)
        {
            if (settings != null)
            {
                settings.DebugMode = SwitchDebugMode.IsChecked == true;
            }
        }

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
            TextBoxLog.Clear();
            TextBoxLog.Text = "日志已清空。\n";
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

        // LLM情感分析事件处理
        private void SwitchEmotionAnalysis_Changed(object sender, RoutedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null)
            {
                settings.EmotionAnalysis.EnableLLMEmotionAnalysis = SwitchEmotionAnalysis.IsChecked == true;
            }
        }

        private void ComboBoxLLMProvider_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis == null || ComboBoxLLMProvider.SelectedItem == null)
                return;

            var selectedItem = ComboBoxLLMProvider.SelectedItem as System.Windows.Controls.ComboBoxItem;
            if (selectedItem != null)
            {
                string providerTag = selectedItem.Tag?.ToString()?.ToLowerInvariant() ?? "openai";

                // 转换为枚举
                switch (providerTag)
                {
                    case "openai":
                        settings.EmotionAnalysis.Provider = EmotionAnalysis.LLMProvider.OpenAI;
                        PanelOpenAI.Visibility = Visibility.Visible;
                        PanelGemini.Visibility = Visibility.Collapsed;
                        PanelOllama.Visibility = Visibility.Collapsed;
                        break;
                    case "gemini":
                        settings.EmotionAnalysis.Provider = EmotionAnalysis.LLMProvider.Gemini;
                        PanelOpenAI.Visibility = Visibility.Collapsed;
                        PanelGemini.Visibility = Visibility.Visible;
                        PanelOllama.Visibility = Visibility.Collapsed;
                        break;
                    case "ollama":
                        settings.EmotionAnalysis.Provider = EmotionAnalysis.LLMProvider.Ollama;
                        PanelOpenAI.Visibility = Visibility.Collapsed;
                        PanelGemini.Visibility = Visibility.Collapsed;
                        PanelOllama.Visibility = Visibility.Visible;
                        break;
                }
            }
        }

        private void TextBoxOpenAIKey_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null)
            {
                settings.EmotionAnalysis.OpenAIApiKey = TextBoxOpenAIKey.Text;
            }
        }

        private void TextBoxOpenAIBaseUrl_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null)
            {
                settings.EmotionAnalysis.OpenAIBaseUrl = TextBoxOpenAIBaseUrl.Text;
            }
        }

        private void ComboBoxOpenAIModel_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && ComboBoxOpenAIModel.SelectedItem != null)
            {
                var selectedItem = ComboBoxOpenAIModel.SelectedItem as System.Windows.Controls.ComboBoxItem;
                if (selectedItem != null)
                {
                    settings.EmotionAnalysis.OpenAIModel = selectedItem.Content?.ToString();
                }
            }
        }

        private void ComboBoxOpenAIModel_LostFocus(object sender, RoutedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null)
            {
                settings.EmotionAnalysis.OpenAIModel = ComboBoxOpenAIModel.Text;
            }
        }

        private async void ButtonFetchOpenAIModels_Click(object sender, RoutedEventArgs e)
        {
            await FetchModelsAsync(
                LLMProvider.OpenAI,
                TextBoxOpenAIKey.Text?.Trim(),
                TextBoxOpenAIBaseUrl.Text?.Trim(),
                ComboBoxOpenAIModel,
                ButtonFetchOpenAIModels,
                "https://api.openai.com/v1"
            );
        }

        private void TextBoxGeminiKey_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null)
            {
                settings.EmotionAnalysis.GeminiApiKey = TextBoxGeminiKey.Text;
            }
        }

        private void TextBoxGeminiBaseUrl_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null)
            {
                settings.EmotionAnalysis.GeminiBaseUrl = TextBoxGeminiBaseUrl.Text;
            }
        }

        private void ComboBoxGeminiModel_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && ComboBoxGeminiModel.SelectedItem != null)
            {
                var selectedItem = ComboBoxGeminiModel.SelectedItem as System.Windows.Controls.ComboBoxItem;
                if (selectedItem != null)
                {
                    settings.EmotionAnalysis.GeminiModel = selectedItem.Content?.ToString();
                }
            }
        }

        private void ComboBoxGeminiModel_LostFocus(object sender, RoutedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null)
            {
                settings.EmotionAnalysis.GeminiModel = ComboBoxGeminiModel.Text;
            }
        }

        private async void ButtonFetchGeminiModels_Click(object sender, RoutedEventArgs e)
        {
            await FetchModelsAsync(
                LLMProvider.Gemini,
                TextBoxGeminiKey.Text?.Trim(),
                TextBoxGeminiBaseUrl.Text?.Trim(),
                ComboBoxGeminiModel,
                ButtonFetchGeminiModels,
                "https://generativelanguage.googleapis.com/v1beta"
            );
        }

        private void TextBoxOllamaBaseUrl_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null)
            {
                settings.EmotionAnalysis.OllamaBaseUrl = TextBoxOllamaBaseUrl.Text;
            }
        }

        private void ComboBoxOllamaModel_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null && ComboBoxOllamaModel.SelectedItem != null)
            {
                var selectedItem = ComboBoxOllamaModel.SelectedItem as System.Windows.Controls.ComboBoxItem;
                if (selectedItem != null)
                {
                    settings.EmotionAnalysis.OllamaModel = selectedItem.Content?.ToString();
                }
            }
        }

        private void ComboBoxOllamaModel_LostFocus(object sender, RoutedEventArgs e)
        {
            if (settings?.EmotionAnalysis != null)
            {
                settings.EmotionAnalysis.OllamaModel = ComboBoxOllamaModel.Text;
            }
        }

        private async void ButtonFetchOllamaModels_Click(object sender, RoutedEventArgs e)
        {
            await FetchModelsAsync(
                LLMProvider.Ollama,
                null, // Ollama 不需要 API Key
                TextBoxOllamaBaseUrl.Text?.Trim(),
                ComboBoxOllamaModel,
                ButtonFetchOllamaModels,
                "http://localhost:11434"
            );
        }

        /// <summary>
        /// 统一的模型获取方法
        /// </summary>
        private async System.Threading.Tasks.Task FetchModelsAsync(
            LLMProvider provider,
            string apiKey,
            string baseUrl,
            System.Windows.Controls.ComboBox comboBox,
            System.Windows.Controls.Button button,
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
                    LLMProvider.OpenAI => new OpenAIClient(apiKey, baseUrl),
                    LLMProvider.Gemini => new GeminiClient(apiKey, baseUrl),
                    LLMProvider.Ollama => new OllamaClient(baseUrl),
                    _ => throw new NotSupportedException($"不支持的提供商: {provider}")
                };

                // 获取模型列表
                var models = await client.GetAvailableModelsAsync();

                // 更新下拉框
                comboBox.Items.Clear();
                foreach (var model in models)
                {
                    var item = new System.Windows.Controls.ComboBoxItem
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
