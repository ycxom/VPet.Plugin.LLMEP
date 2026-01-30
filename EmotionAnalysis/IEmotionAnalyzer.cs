using System.Collections.Generic;
using System.Threading.Tasks;

namespace VPet.Plugin.Image.EmotionAnalysis
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
    }
}
