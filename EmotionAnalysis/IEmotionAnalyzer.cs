using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

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
        /// 分析情感并返回匹配的图片（支持精确标签匹配）
        /// </summary>
        /// <param name="text">要分析的文本</param>
        /// <returns>匹配的图片，如果没有匹配则返回null</returns>
        Task<BitmapImage> AnalyzeEmotionAndGetImageAsync(string text);
    }
}
