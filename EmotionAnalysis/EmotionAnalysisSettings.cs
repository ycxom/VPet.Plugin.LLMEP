namespace VPet.Plugin.LLMEP.EmotionAnalysis
{
    /// <summary>
    /// LLM提供商枚举
    /// </summary>
    public enum LLMProvider
    {
        OpenAI,
        Gemini,
        Ollama,
        Free
    }

    /// <summary>
    /// 情感分析设置
    /// </summary>
    public class EmotionAnalysisSettings
    {
        // 功能开关
        public bool EnableLLMEmotionAnalysis { get; set; } = true;

        // LLM提供商
        public LLMProvider Provider { get; set; } = LLMProvider.Free;

        // OpenAI设置
        public string OpenAIApiKey { get; set; } = "";
        public string OpenAIBaseUrl { get; set; } = "https://api.openai.com/v1";
        public string OpenAIModel { get; set; } = "gpt-3.5-turbo";
        public string OpenAIEmbeddingModel { get; set; } = "text-embedding-3-small";

        // Gemini设置
        public string GeminiApiKey { get; set; } = "";
        public string GeminiBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
        public string GeminiModel { get; set; } = "gemini-pro";
        public string GeminiEmbeddingModel { get; set; } = "embedding-001";

        // Ollama设置
        public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
        public string OllamaModel { get; set; } = "llama2";

        // Free设置（使用VPetLLM的Free配置）
        public string FreeModel { get; set; } = "bymbym";

        /// <summary>
        /// 是否是支持视觉的模型（可读取图片）
        /// </summary>
        public bool IsVisionModel { get; set; } = false;

        // 性能设置
        public int MinRequestIntervalMs { get; set; } = 10000; // 10秒
        public int CacheExpirationHours { get; set; } = 1;
        public int MaxCacheSize { get; set; } = 1000;
        public int TopKMatches { get; set; } = 3;

        /// <summary>
        /// 克隆设置
        /// </summary>
        public EmotionAnalysisSettings Clone()
        {
            return new EmotionAnalysisSettings
            {
                EnableLLMEmotionAnalysis = this.EnableLLMEmotionAnalysis,
                Provider = this.Provider,
                OpenAIApiKey = this.OpenAIApiKey,
                OpenAIBaseUrl = this.OpenAIBaseUrl,
                OpenAIModel = this.OpenAIModel,
                OpenAIEmbeddingModel = this.OpenAIEmbeddingModel,
                GeminiApiKey = this.GeminiApiKey,
                GeminiBaseUrl = this.GeminiBaseUrl,
                GeminiModel = this.GeminiModel,
                GeminiEmbeddingModel = this.GeminiEmbeddingModel,
                OllamaBaseUrl = this.OllamaBaseUrl,
                OllamaModel = this.OllamaModel,
                FreeModel = this.FreeModel,
                IsVisionModel = this.IsVisionModel,
                MinRequestIntervalMs = this.MinRequestIntervalMs,
                CacheExpirationHours = this.CacheExpirationHours,
                MaxCacheSize = this.MaxCacheSize,
                TopKMatches = this.TopKMatches
            };
        }

        /// <summary>
        /// 比较两个设置是否相等
        /// </summary>
        public bool Equals(EmotionAnalysisSettings other)
        {
            if (other == null) return false;

            return EnableLLMEmotionAnalysis == other.EnableLLMEmotionAnalysis &&
                   Provider == other.Provider &&
                   OpenAIApiKey == other.OpenAIApiKey &&
                   OpenAIBaseUrl == other.OpenAIBaseUrl &&
                   OpenAIModel == other.OpenAIModel &&
                   OpenAIEmbeddingModel == other.OpenAIEmbeddingModel &&
                   GeminiApiKey == other.GeminiApiKey &&
                   GeminiBaseUrl == other.GeminiBaseUrl &&
                   GeminiModel == other.GeminiModel &&
                   GeminiEmbeddingModel == other.GeminiEmbeddingModel &&
                   OllamaBaseUrl == other.OllamaBaseUrl &&
                   OllamaModel == other.OllamaModel &&
                   FreeModel == other.FreeModel &&
                   IsVisionModel == other.IsVisionModel &&
                   MinRequestIntervalMs == other.MinRequestIntervalMs &&
                   CacheExpirationHours == other.CacheExpirationHours &&
                   MaxCacheSize == other.MaxCacheSize &&
                   TopKMatches == other.TopKMatches;
        }
    }
}
