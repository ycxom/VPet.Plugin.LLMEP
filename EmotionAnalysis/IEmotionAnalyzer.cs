using System.Collections.Generic;
using System.Threading.Tasks;

namespace VPet.Plugin.LLMEP.EmotionAnalysis
{
    /// <summary>
    /// 情感分析器接口
    /// </summary>
    public interface IEmotionAnalyzer
    {
        /// <summary>
        /// 分析文本的情感，返回情感关键词列表
        /// </summary>
        Task<List<string>> AnalyzeEmotionAsync(string text);

        /// <summary>
        /// 分析情感并返回匹配图片的路径（支持精确标签匹配）。
        /// 只返回路径，解码交给 ImageMgr 在后台按显示尺寸完成。
        /// </summary>
        /// <param name="text">要分析的文本</param>
        /// <returns>匹配图片的完整路径；在线表情包已直接显示或没有匹配时返回null</returns>
        Task<string> AnalyzeEmotionAndGetImagePathAsync(string text);
    }
}
