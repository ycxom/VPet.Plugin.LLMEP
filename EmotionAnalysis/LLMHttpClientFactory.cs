using System;
using System.Net;
using System.Net.Http;

namespace VPet.Plugin.LLMEP.EmotionAnalysis
{
    /// <summary>
    /// LLM 请求的 HttpClient 工厂，按插件代理设置构建。
    /// 背景：HttpClientHandler 默认 UseProxy=true，Proxy=null 时会静默使用系统代理，
    /// 因此"直连"必须显式 UseProxy=false，代理行为需由配置决定而非默认值泄漏。
    /// </summary>
    public static class LLMHttpClientFactory
    {
        private static string _proxyMode = "System";
        private static string _proxyAddress = "";

        /// <summary>
        /// 应用代理设置（在设置加载/变更、客户端创建前调用）
        /// </summary>
        public static void Configure(EmotionAnalysisSettings settings)
        {
            _proxyMode = settings?.ProxyMode ?? "System";
            _proxyAddress = settings?.ProxyAddress ?? "";
        }

        /// <summary>
        /// 按当前代理配置创建 HttpClient
        /// </summary>
        public static HttpClient Create(TimeSpan? timeout = null)
        {
            HttpClientHandler handler;
            switch (_proxyMode)
            {
                case "Direct":
                    handler = new HttpClientHandler { UseProxy = false, Proxy = null };
                    break;

                case "Custom":
                    if (!string.IsNullOrWhiteSpace(_proxyAddress))
                    {
                        try
                        {
                            var address = _proxyAddress.Contains("://") ? _proxyAddress : $"http://{_proxyAddress}";
                            handler = new HttpClientHandler
                            {
                                Proxy = new WebProxy(new Uri(address)),
                                UseProxy = true
                            };
                        }
                        catch
                        {
                            // 地址无效则回退直连
                            handler = new HttpClientHandler { UseProxy = false, Proxy = null };
                        }
                    }
                    else
                    {
                        handler = new HttpClientHandler { UseProxy = false, Proxy = null };
                    }
                    break;

                case "System":
                default:
                    // UseProxy=true 且 Proxy=null 即跟随系统代理
                    handler = new HttpClientHandler();
                    break;
            }

            var client = new HttpClient(handler);
            if (timeout.HasValue)
            {
                client.Timeout = timeout.Value;
            }
            return client;
        }
    }
}
