using System;
using System.Threading.Tasks;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.LLMEP.EmotionAnalysis
{
    /// <summary>
    /// 语音捕获器 - 捕获VPet的说话内容
    /// </summary>
    public class SpeechCapturer
    {
        private readonly IMainWindow _mainWindow;
        private readonly IEmotionAnalyzer _emotionAnalyzer;
        private readonly ImageSelector _imageSelector;
        private readonly ImageMgr _imageMgr;
        private bool _isInitialized = false;

        // 通过属性获取当前设置，确保总是使用最新值
        private ImageSettings Settings => _imageMgr?.Settings;

        public SpeechCapturer(
            IMainWindow mainWindow,
            IEmotionAnalyzer emotionAnalyzer,
            ImageSelector imageSelector,
            ImageMgr imageMgr = null)
        {
            _mainWindow = mainWindow;
            _emotionAnalyzer = emotionAnalyzer;
            _imageSelector = imageSelector;
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

                // 检查是否处于独占会话（气泡捕获屏蔽）
                if (_imageMgr?.ImageCoordinator != null && !_imageMgr.ImageCoordinator.IsBubbleCaptureEnabled())
                {
                    _imageMgr.LogInfo("SpeechCapturer", "独占会话期间，屏蔽气泡文本捕获");
                    return;
                }

                // 检查功能是否启用
                var settings = Settings;
                if (settings?.EmotionAnalysis == null || !settings.EmotionAnalysis.EnableLLMEmotionAnalysis)
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
                if (settings.UseBubbleTrigger)
                {
                    _imageMgr.LogDebug("SpeechCapturer", "气泡触发已启用，进行概率检查");

                    // 在入口处进行概率检查，未命中时直接返回，不进行任何处理
                    if (!settings.ShouldTriggerBubble())
                    {
                        _imageMgr.LogDebug("SpeechCapturer", $"概率未命中 ({settings.BubbleTriggerProbability}%)，跳过LLM分析和图片显示");
                        return;
                    }

                    // 概率命中，通知 ImageMgr 显示图片
                    _imageMgr.LogDebug("SpeechCapturer", $"概率命中 ({settings.BubbleTriggerProbability}%)，通知 ImageMgr 显示图片");
                    _imageMgr?.HandleBubbleProbabilityFromSpeechCapturer();
                }
                else
                {
                    // 气泡触发禁用时，直接进行情感分析并显示图片
                    _imageMgr.LogDebug("SpeechCapturer", "气泡触发已禁用，直接进行情感分析");
                    _ = ProcessSpeechAsync(text);
                }

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
