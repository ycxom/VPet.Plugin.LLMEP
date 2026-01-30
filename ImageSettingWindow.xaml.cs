using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Panuon.WPF.UI;

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