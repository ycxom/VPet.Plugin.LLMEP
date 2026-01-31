using System;
using System.Text;

namespace VPet.Plugin.LLMEP.Services
{
    /// <summary>
    /// 在线表情包服务的内置凭证
    /// 使用与 StickerPlugin 相同的服务地址和 API Key
    /// </summary>
    public static class OnlineStickerCredentials
    {
        // 使用与 StickerPlugin 相同的混淆凭证
        private static readonly string _obfuscatedUrl = "aHR0cHM6Ly9haS55Y3hvbS50b3A6ODAyNS9lbW90aWNvbnM=";
        private static readonly string _obfuscatedKey = "VlBldExMTS15Y3hvbS1JTUFHRV9WRUNUT1I=";

        /// <summary>
        /// 获取内置的服务地址
        /// </summary>
        /// <returns>内置服务地址</returns>
        public static string GetBuiltInServiceUrl()
        {
            return Deobfuscate(_obfuscatedUrl);
        }

        /// <summary>
        /// 获取内置的 API Key
        /// </summary>
        /// <returns>内置 API Key</returns>
        public static string GetBuiltInApiKey()
        {
            return Deobfuscate(_obfuscatedKey);
        }

        /// <summary>
        /// 检查内置凭证是否可用
        /// </summary>
        /// <returns>如果内置凭证可用则返回 true</returns>
        public static bool IsBuiltInCredentialsAvailable()
        {
            var serviceUrl = GetBuiltInServiceUrl();
            var apiKey = GetBuiltInApiKey();

            return !string.IsNullOrEmpty(serviceUrl) && !string.IsNullOrEmpty(apiKey);
        }

        /// <summary>
        /// 检查是否使用内置凭证
        /// </summary>
        /// <param name="url">用户配置的服务地址</param>
        /// <param name="key">用户配置的 API Key</param>
        /// <returns>如果应该使用内置凭证则返回 true</returns>
        public static bool IsUsingBuiltIn(string url, string key)
        {
            return string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key) ||
                   url == GetBuiltInServiceUrl() || key == GetBuiltInApiKey();
        }

        /// <summary>
        /// 获取服务描述信息
        /// </summary>
        /// <returns>服务描述</returns>
        public static string GetServiceDescription()
        {
            return "VPet 在线网络表情包库提供丰富的网络表情资源，支持基于情感分析的智能匹配。";
        }

        /// <summary>
        /// 获取服务提供者信息
        /// </summary>
        /// <returns>服务提供者信息</returns>
        public static string GetServiceProvider()
        {
            return "感谢提供者 QQ：790132463";
        }

        /// <summary>
        /// 混淆字符串（用于开发测试）
        /// </summary>
        /// <param name="input">要混淆的字符串</param>
        /// <returns>混淆后的字符串</returns>
        internal static string Obfuscate(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(input));
        }

        /// <summary>
        /// 解混淆字符串
        /// </summary>
        /// <param name="obfuscated">混淆的字符串</param>
        /// <returns>原始字符串</returns>
        private static string Deobfuscate(string obfuscated)
        {
            if (string.IsNullOrEmpty(obfuscated)) return "";

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(obfuscated));
            }
            catch
            {
                return "";
            }
        }
    }
}