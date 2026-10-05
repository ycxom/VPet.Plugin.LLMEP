using System.Collections.Generic;
using System.Threading.Tasks;

namespace VPet.Plugin.LLMEP.EmotionAnalysis
{
    /// <summary>
    /// 向量检索器接口
    /// </summary>
    public interface IVectorRetriever
    {
        /// <summary>
        /// 加载标签文件
        /// </summary>
        void LoadLabels(string labelFilePath);

        /// <summary>
        /// 直接加载"文件名 → 标签"（DIY 表情包的 diy_labels.json 格式）
        /// </summary>
        void LoadLabels(IDictionary<string, List<string>> imageLabels, string source);

        /// <summary>
        /// 根据情感关键词查找匹配的图片
        /// </summary>
        Task<List<string>> FindMatchingImagesAsync(List<string> emotions, int topK = 3);
    }
}
