using System.Threading.Tasks;

namespace VPet.Plugin.LLMEP.Core
{
    /// <summary>
    /// Image插件协调器接口
    /// 供外部插件（如 StickerPlugin）调用，实现独占会话管理
    /// </summary>
    public interface IImagePluginCoordinator
    {
        /// <summary>
        /// 启动独占会话
        /// </summary>
        /// <param name="callerId">调用者 ID（如 "StickerPlugin"）</param>
        /// <returns>会话 ID</returns>
        Task<string> StartExclusiveSessionAsync(string callerId);

        /// <summary>
        /// 结束独占会话
        /// </summary>
        /// <param name="callerId">调用者 ID</param>
        /// <param name="sessionId">会话 ID</param>
        Task EndExclusiveSessionAsync(string callerId, string sessionId);

        /// <summary>
        /// 在独占会话中显示表情包（Base64）
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="base64Image">Base64 编码的图片</param>
        /// <param name="durationSeconds">显示时长（秒）</param>
        /// <returns>请求 ID</returns>
        Task<string> ShowImageInSessionAsync(string sessionId, string base64Image, int durationSeconds);

        /// <summary>
        /// 检查会话是否活跃
        /// </summary>
        bool IsSessionActive();

        /// <summary>
        /// 检查是否可以使用独占模式
        /// </summary>
        bool CanUseExclusiveMode();

        /// <summary>
        /// 检查气泡捕获是否启用（供内部使用）
        /// </summary>
        bool IsBubbleCaptureEnabled();
    }
}
