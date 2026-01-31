using System;
using System.Threading.Tasks;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.LLMEP
{
    /// <summary>
    /// 气泡文本监听器 - 监听VPet说话内容
    /// 使用推荐的 SayProcess 方法，参考了 VPet.Plugin.VPetTTS 的实现
    /// </summary>
    public class BubbleTextListener
    {
        private readonly IMainWindow _mainWindow;
        private readonly ImageMgr _imageMgr;
        private bool _isInitialized = false;

        // 事件：当捕获到文本时触发
        public event EventHandler<string> TextCaptured;

        public BubbleTextListener(IMainWindow mainWindow, ImageMgr imageMgr)
        {
            _mainWindow = mainWindow;
            _imageMgr = imageMgr;
        }

        /// <summary>
        /// 初始化监听器
        /// 使用推荐的 SayProcess 方法
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
                return;

            try
            {
                // 使用 SayProcess（推荐方法，与 VPetTTS 相同）
                _mainWindow.Main.SayProcess.Add(OnSayProcess);
                _imageMgr.LogMessage("已注册 SayProcess 监听器");

                _isInitialized = true;
                _imageMgr.LogMessage("气泡文本监听器初始化完成");
            }
            catch (Exception ex)
            {
                _imageMgr.LogMessage($"初始化气泡文本监听器失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理监听器
        /// </summary>
        public void Cleanup()
        {
            if (!_isInitialized)
                return;

            try
            {
                // 注销 SayProcess
                _mainWindow.Main.SayProcess.Remove(OnSayProcess);
                _imageMgr.LogMessage("已注销 SayProcess 监听器");

                _isInitialized = false;
                _imageMgr.LogMessage("气泡文本监听器已清理");
            }
            catch (Exception ex)
            {
                _imageMgr.LogMessage($"清理气泡文本监听器失败: {ex.Message}");
            }
        }

        /// <summary>
        /// SayProcess 回调
        /// 与 VPetTTS 使用相同的方法
        /// </summary>
        private async void OnSayProcess(SayInfo sayInfo)
        {
            try
            {
                if (sayInfo == null)
                {
                    _imageMgr.LogMessage("SayProcess: sayInfo 为空，跳过处理");
                    return;
                }

                _imageMgr.LogMessage("SayProcess: 开始获取气泡文本");

                // 获取文本内容
                string text = await sayInfo.GetSayText().ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(text))
                {
                    _imageMgr.LogMessage("SayProcess: 获取到的文本为空或空白，跳过处理");
                    return;
                }

                // 记录捕获的完整文本（如果不是调试模式，则截断显示）
                if (_imageMgr.Settings.DebugMode)
                {
                    _imageMgr.LogMessage($"SayProcess: 成功捕获气泡文本 [长度: {text.Length}]: {text}");
                }
                else
                {
                    string preview = text.Length > 50 ? text.Substring(0, 50) + "..." : text;
                    _imageMgr.LogMessage($"SayProcess: 成功捕获气泡文本 [长度: {text.Length}]: {preview}");
                }

                // 触发事件
                _imageMgr.LogMessage("SayProcess: 触发 TextCaptured 事件");
                TextCaptured?.Invoke(this, text);
                _imageMgr.LogMessage("SayProcess: TextCaptured 事件处理完成");
            }
            catch (Exception ex)
            {
                _imageMgr.LogMessage($"SayProcess 回调出错: {ex.Message}");
                _imageMgr.LogMessage($"SayProcess 错误堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 检查监听器是否已初始化
        /// </summary>
        public bool IsInitialized => _isInitialized;
    }
}