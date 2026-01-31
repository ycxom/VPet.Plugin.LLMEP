using System;
using System.Threading.Tasks;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.Image.EmotionAnalysis
{
    /// <summary>
    /// 语音捕获器 - 捕获VPet的说话内容
    /// </summary>
    public class SpeechCapturer
    {
        private readonly IMainWindow _mainWindow;
        private readonly IEmotionAnalyzer _emotionAnalyzer;
        private readonly ImageSelector _imageSelector;
        private readonly ImageSettings _settings;
        private readonly ImageMgr _imageMgr;
        private bool _isInitialized = false;

        public SpeechCapturer(
            IMainWindow mainWindow,
            IEmotionAnalyzer emotionAnalyzer,
            ImageSelector imageSelector,
            ImageSettings settings,
            ImageMgr imageMgr = null)
        {
            _mainWindow = mainWindow;
            _emotionAnalyzer = emotionAnalyzer;
            _imageSelector = imageSelector;
            _settings = settings;
            _imageMgr = imageMgr;
        }

        /// <summary>
        /// 初始化并注册回调
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
                return;

            try
            {
                // 注册到VPet的说话事件
                _mainWindow.Main.SayProcess.Add(OnSay);
                _isInitialized = true;
                _imageMgr.LogInfo("SpeechCapturer", "已初始化并注册到 SayProcess");
            }
            catch (Exception ex)
            {
                _imageMgr.LogError("SpeechCapturer", $"初始化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理并注销回调
        /// </summary>
        public void Cleanup()
        {
            if (!_isInitialized)
                return;

            try
            {
                _mainWindow.Main.SayProcess.Remove(OnSay);
                _isInitialized = false;
                _imageMgr.LogInfo("SpeechCapturer", "已清理并从 SayProcess 注销");
            }
            catch (Exception ex)
            {
                _imageMgr.LogError("SpeechCapturer", $"清理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// VPet说话时的回调
        /// </summary>
        private async void OnSay(SayInfo sayInfo)
        {
            try
            {
                _imageMgr.LogDebug("SpeechCapturer", "=== 开始处理气泡文本 ===");
                
                // 检查功能是否启用
                if (_settings?.EmotionAnalysis == null || !_settings.EmotionAnalysis.EnableLLMEmotionAnalysis)
                {
                    _imageMgr.LogDebug("SpeechCapturer", "LLM情感分析未启用，跳过处理");
                    return;
                }

                // 检查 sayInfo 是否为空
                if (sayInfo == null)
                {
                    _imageMgr.LogDebug("SpeechCapturer", "sayInfo 为空，跳过处理");
                    return;
                }

                _imageMgr.LogDebug("SpeechCapturer", "开始获取气泡文本");

                // 获取文本内容
                string text = await sayInfo.GetSayText().ConfigureAwait(false);

                // 过滤空白文本
                if (string.IsNullOrWhiteSpace(text))
                {
                    _imageMgr.LogDebug("SpeechCapturer", "获取到的文本为空或空白，跳过处理");
                    return;
                }

                // 限制文本长度
                if (text.Length > 500)
                {
                    text = text.Substring(0, 500);
                    _imageMgr.LogDebug("SpeechCapturer", $"文本过长，已截断到500字符");
                }

                // 记录捕获的文本
                string textPreview = text.Length > 50 ? text.Substring(0, 50) + "..." : text;
                _imageMgr.LogInfo("SpeechCapturer", $"捕获气泡文本: {textPreview}");

                // 检查是否使用气泡触发模式
                if (_settings.UseBubbleTrigger)
                {
                    _imageMgr.LogDebug("SpeechCapturer", "气泡触发已启用，通知 ImageMgr 处理气泡概率触发");
                    // 通知 ImageMgr 处理气泡概率触发
                    _imageMgr?.HandleBubbleProbabilityFromSpeechCapturer();
                }

                // 异步处理情感分析（与气泡触发并行工作）
                _imageMgr.LogDebug("SpeechCapturer", "开始异步处理文本（情感分析模式）");
                _ = ProcessSpeechAsync(text);
                
                _imageMgr.LogDebug("SpeechCapturer", "=== 气泡文本处理启动完成 ===");
            }
            catch (Exception ex)
            {
                _imageMgr.LogError("SpeechCapturer", $"OnSay 回调出错: {ex.Message}");
                _imageMgr.LogDebug("SpeechCapturer", $"错误堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 异步处理语音文本
        /// </summary>
        private async Task ProcessSpeechAsync(string text)
        {
            try
            {
                _imageMgr.LogDebug("SpeechCapturer", "--- 开始情感分析处理 ---");
                _imageMgr.LogDebug("SpeechCapturer", $"处理文本: {text}");

                // 使用新的情感分析和图片选择方法
                _imageMgr.LogDebug("SpeechCapturer", "调用情感分析和图片选择器");
                await _imageSelector.SelectAndDisplayWithEmotionAnalysisAsync(_emotionAnalyzer, text);
                
                _imageMgr.LogDebug("SpeechCapturer", "--- 情感分析处理完成 ---");
            }
            catch (Exception ex)
            {
                _imageMgr.LogError("SpeechCapturer", $"处理语音失败: {ex.Message}");
                _imageMgr.LogDebug("SpeechCapturer", $"处理错误堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 记录日志
        /// </summary>
        private void LogMessage(string message)
        {
            // 如果有 ImageMgr 引用，使用其日志系统
            if (_imageMgr != null)
            {
                _imageMgr.LogMessage(message);
            }
            else
            {
                // 否则使用控制台输出
                Console.WriteLine($"[SpeechCapturer] {DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
            }
        }
    }
}
