using System.Collections.Generic;
using System.Threading.Tasks;

namespace VPet.Plugin.LLMEP.EmotionAnalysis
{
    /// <summary>
    /// 模型信息基类
    /// </summary>
    public class ModelInfo
    {
        /// <summary>
        /// 模型ID/名称
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// 模型显示名称
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 模型描述或额外信息
        /// </summary>
        public string Description { get; set; }
    }

    /// <summary>
    /// LLM客户端接口，支持OpenAI、Gemini、Ollama等兼容OpenAI API的端点
    /// </summary>
    public interface ILLMClient
    {
        /// <summary>
        /// 发送请求到LLM并获取响应
        /// </summary>
        Task<string> SendRequestAsync(string prompt);

        /// <summary>
        /// 获取文本的向量嵌入
        /// </summary>
        Task<float[]> GetEmbeddingAsync(string text);

        /// <summary>
        /// 获取可用的模型列表
        /// </summary>
        /// <returns>模型信息列表</returns>
        Task<List<ModelInfo>> GetAvailableModelsAsync();
    }
}
