using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace VPet.Plugin.LLMEP.Core
{
    /// <summary>
    /// Image插件协调器实现
    /// 供外部插件（如 StickerPlugin）调用，实现独占会话管理
    /// </summary>
    public class ImagePluginCoordinator : IImagePluginCoordinator
    {
        private readonly ImageMgr _imageMgr;
        private readonly ExclusiveSessionManager _sessionManager;

        public ImagePluginCoordinator(ImageMgr imageMgr)
        {
            _imageMgr = imageMgr ?? throw new ArgumentNullException(nameof(imageMgr));
            _sessionManager = new ExclusiveSessionManager();

            _imageMgr.LogInfo("ImagePluginCoordinator", "协调器已创建");
        }

        /// <summary>
        /// 启动独占会话
        /// </summary>
        public async Task<string> StartExclusiveSessionAsync(string callerId)
        {
            try
            {
                _imageMgr.LogInfo("ImagePluginCoordinator", $"启动独占会话，调用者: {callerId}");

                // 启动会话
                var sessionId = _sessionManager.StartSession(callerId);

                // 禁用气泡捕获
                _sessionManager.DisableBubbleCapture();

                _imageMgr.LogInfo("ImagePluginCoordinator", $"独占会话已启动，会话 ID: {sessionId}");
                return await Task.FromResult(sessionId);
            }
            catch (Exception ex)
            {
                _imageMgr.LogError("ImagePluginCoordinator", $"启动独占会话失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 结束独占会话
        /// </summary>
        public async Task EndExclusiveSessionAsync(string callerId, string sessionId)
        {
            try
            {
                _imageMgr.LogInfo("ImagePluginCoordinator", $"结束独占会话，调用者: {callerId}，会话 ID: {sessionId}");

                // 结束会话
                var success = _sessionManager.EndSession(callerId, sessionId);

                if (success)
                {
                    // 重新启用气泡捕获
                    _sessionManager.EnableBubbleCapture();
                    _imageMgr.LogInfo("ImagePluginCoordinator", "独占会话已结束");
                }
                else
                {
                    _imageMgr.LogWarning("ImagePluginCoordinator", "结束独占会话失败");
                }

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _imageMgr.LogError("ImagePluginCoordinator", $"结束独占会话失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 在独占会话中显示表情包（Base64）
        /// </summary>
        public async Task<string> ShowImageInSessionAsync(string sessionId, string base64Image, int durationSeconds)
        {
            try
            {
                _imageMgr.LogInfo("ImagePluginCoordinator", $"在会话 {sessionId} 中显示表情包");

                // 验证会话
                if (!_sessionManager.IsSessionActive())
                {
                    throw new InvalidOperationException("没有活跃的会话");
                }

                if (_sessionManager.GetCurrentSessionId() != sessionId)
                {
                    throw new InvalidOperationException("会话 ID 不匹配");
                }

                // 注册请求
                var requestId = _sessionManager.RegisterRequest(sessionId, "显示表情包");

                // 显示表情包
                await _imageMgr.ShowImageFromBase64Async(base64Image, durationSeconds);

                // 标记请求完成
                _sessionManager.MarkRequestComplete(requestId);

                _imageMgr.LogInfo("ImagePluginCoordinator", $"表情包显示完成，请求 ID: {requestId}");
                return requestId;
            }
            catch (Exception ex)
            {
                _imageMgr.LogError("ImagePluginCoordinator", $"显示表情包失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 检查会话是否活跃
        /// </summary>
        public bool IsSessionActive()
        {
            return _sessionManager.IsSessionActive();
        }

        /// <summary>
        /// 检查是否可以使用独占模式
        /// </summary>
        public bool CanUseExclusiveMode()
        {
            // 检查插件是否启用
            if (!_imageMgr.Settings.IsEnabled)
            {
                return false;
            }

            // 检查是否有活跃会话
            if (_sessionManager.IsSessionActive())
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 检查气泡捕获是否启用（供内部使用）
        /// </summary>
        public bool IsBubbleCaptureEnabled()
        {
            return _sessionManager.IsBubbleCaptureEnabled();
        }

        /// <summary>
        /// 定期清理超时会话（内部方法）
        /// </summary>
        internal void CheckAndCleanupTimedOutSession()
        {
            _sessionManager.CheckAndCleanupTimedOutSession();
        }
    }
}
