using System.Collections.Generic;
using System.Threading.Tasks;

namespace VPet.Plugin.Image.EmotionAnalysis
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
        /// 根据情感关键词查找匹配的图片
        /// </summary>
        Task<List<string>> FindMatchingImagesAsync(List<string> emotions, int topK = 3);
    }
}
