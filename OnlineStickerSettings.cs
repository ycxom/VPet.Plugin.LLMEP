namespace VPet.Plugin.LLMEP
{
    /// <summary>
    /// 在线网络表情包库设置
    /// </summary>
    public class OnlineStickerSettings
    {
        /// <summary>
        /// 是否启用在线网络表情包库
        /// </summary>
        public bool IsEnabled { get; set; } = false;

        /// <summary>
        /// 是否使用内置凭证
        /// </summary>
        public bool UseBuiltInCredentials { get; set; } = true;

        /// <summary>
        /// 自定义服务地址（当不使用内置凭证时）
        /// </summary>
        public string ServiceUrl { get; set; } = "";

        /// <summary>
        /// 自定义 API Key（当不使用内置凭证时）
        /// </summary>
        public string ApiKey { get; set; } = "";

        /// <summary>
        /// 标签数量（用于系统提示词）
        /// </summary>
        public int TagCount { get; set; } = 10;

        /// <summary>
        /// 缓存持续时间（分钟）
        /// </summary>
        public int CacheDurationMinutes { get; set; } = 5;

        /// <summary>
        /// 显示持续时间（秒）
        /// </summary>
        public int DisplayDurationSeconds { get; set; } = 6;

        /// <summary>
        /// 在线表情包优先级：true=优先使用在线表情包，false=作为本地表情包的补充
        /// </summary>
        public bool PreferOnlineStickers { get; set; } = false;

        /// <summary>
        /// 是否在情感分析中启用在线表情包
        /// </summary>
        public bool EnableInEmotionAnalysis { get; set; } = true;

        /// <summary>
        /// 是否在随机显示中启用在线表情包
        /// </summary>
        public bool EnableInRandomDisplay { get; set; } = true;

        /// <summary>
        /// 是否在气泡触发中启用在线表情包
        /// </summary>
        public bool EnableInBubbleTrigger { get; set; } = true;

        /// <summary>
        /// 克隆设置对象
        /// </summary>
        public OnlineStickerSettings Clone()
        {
            return new OnlineStickerSettings
            {
                IsEnabled = this.IsEnabled,
                UseBuiltInCredentials = this.UseBuiltInCredentials,
                ServiceUrl = this.ServiceUrl,
                ApiKey = this.ApiKey,
                TagCount = this.TagCount,
                CacheDurationMinutes = this.CacheDurationMinutes,
                DisplayDurationSeconds = this.DisplayDurationSeconds,
                PreferOnlineStickers = this.PreferOnlineStickers,
                EnableInEmotionAnalysis = this.EnableInEmotionAnalysis,
                EnableInRandomDisplay = this.EnableInRandomDisplay,
                EnableInBubbleTrigger = this.EnableInBubbleTrigger
            };
        }

        /// <summary>
        /// 比较两个设置对象是否相等
        /// </summary>
        public bool Equals(OnlineStickerSettings other)
        {
            if (other == null) return false;

            return IsEnabled == other.IsEnabled &&
                   UseBuiltInCredentials == other.UseBuiltInCredentials &&
                   ServiceUrl == other.ServiceUrl &&
                   ApiKey == other.ApiKey &&
                   TagCount == other.TagCount &&
                   CacheDurationMinutes == other.CacheDurationMinutes &&
                   DisplayDurationSeconds == other.DisplayDurationSeconds &&
                   PreferOnlineStickers == other.PreferOnlineStickers &&
                   EnableInEmotionAnalysis == other.EnableInEmotionAnalysis &&
                   EnableInRandomDisplay == other.EnableInRandomDisplay &&
                   EnableInBubbleTrigger == other.EnableInBubbleTrigger;
        }

        /// <summary>
        /// 验证并修正设置值到有效范围
        /// </summary>
        public void Validate()
        {
            // TagCount: 最小 1，最大 100
            if (TagCount < 1)
                TagCount = 1;
            if (TagCount > 100)
                TagCount = 100;

            // DisplayDurationSeconds: 1-60 秒
            if (DisplayDurationSeconds < 1)
                DisplayDurationSeconds = 1;
            if (DisplayDurationSeconds > 60)
                DisplayDurationSeconds = 60;

            // CacheDurationMinutes: 最小 1 分钟
            if (CacheDurationMinutes < 1)
                CacheDurationMinutes = 1;

            // 自定义凭证时验证 ServiceUrl
            if (!UseBuiltInCredentials && string.IsNullOrWhiteSpace(ServiceUrl))
                ServiceUrl = "";
        }

        /// <summary>
        /// 获取有效的服务地址
        /// </summary>
        public string GetEffectiveServiceUrl()
        {
            if (UseBuiltInCredentials)
            {
                return Services.OnlineStickerCredentials.GetBuiltInServiceUrl();
            }
            return ServiceUrl;
        }

        /// <summary>
        /// 获取有效的 API Key
        /// </summary>
        public string GetEffectiveApiKey()
        {
            if (UseBuiltInCredentials)
            {
                return Services.OnlineStickerCredentials.GetBuiltInApiKey();
            }
            return ApiKey;
        }
    }
}