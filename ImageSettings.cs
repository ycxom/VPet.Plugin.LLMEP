using System;
using System.IO;
using System.Text.Json;
using VPet.Plugin.LLMEP.EmotionAnalysis;
using VPet.Plugin.LLMEP.Utils;

namespace VPet.Plugin.LLMEP
{
    /// <summary>
    /// 表情包插件设置类
    /// </summary>
    public class ImageSettings
    {
        /// <summary>
        /// 是否启用插件
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 是否启用内置表情包（VPet_Expression文件夹）
        /// </summary>
        public bool EnableBuiltInImages { get; set; } = true;

        /// <summary>
        /// 是否启用DIY表情包（DIY_Expression文件夹）
        /// </summary>
        public bool EnableDIYImages { get; set; } = true;

        /// <summary>
        /// 显示时长（秒）
        /// </summary>
        public int DisplayDuration { get; set; } = 6;

        /// <summary>
        /// 显示间隔（分钟）
        /// </summary>
        public int DisplayInterval { get; set; } = 3;

        /// <summary>
        /// 是否使用随机间隔
        /// </summary>
        public bool UseRandomInterval { get; set; } = true;

        /// <summary>
        /// 调试模式
        /// </summary>
        public bool DebugMode { get; set; } = true;

        /// <summary>
        /// 日志等级：0=Debug, 1=Info, 2=Warning, 3=Error
        /// </summary>
        public int LogLevel { get; set; } = 1; // 默认Info级别

        /// <summary>
        /// 是否启用文件日志
        /// </summary>
        public bool EnableFileLogging { get; set; } = false;

        /// <summary>
        /// 情感分析设置
        /// </summary>
        public EmotionAnalysisSettings EmotionAnalysis { get; set; } = new EmotionAnalysisSettings();

        /// <summary>
        /// 是否启用精确表情包匹配
        /// </summary>
        public bool UseAccurateImageMatching { get; set; } = true;

        /// <summary>
        /// 是否启用时间触发模式
        /// </summary>
        public bool UseTimeTrigger { get; set; } = true;

        /// <summary>
        /// 气泡触发模式：true=启用基于概率的气泡触发，false=禁用气泡触发
        /// </summary>
        public bool UseBubbleTrigger { get; set; } = true;

        /// <summary>
        /// 气泡触发概率：VPet每次说话时显示表情包的概率（百分比，1-100）
        /// </summary>
        public int BubbleTriggerProbability { get; set; } = 20;

        /// <summary>
        /// 精确匹配模式版本号：用于检测UseAccurateImageMatching设置变化，变化时清空缓存
        /// </summary>
        public int AccurateMatchingVersion { get; set; } = 1;

        /// <summary>
        /// 克隆设置对象
        /// </summary>
        public ImageSettings Clone()
        {
            return new ImageSettings
            {
                IsEnabled = this.IsEnabled,
                EnableBuiltInImages = this.EnableBuiltInImages,
                EnableDIYImages = this.EnableDIYImages,
                DisplayDuration = this.DisplayDuration,
                DisplayInterval = this.DisplayInterval,
                UseRandomInterval = this.UseRandomInterval,
                DebugMode = this.DebugMode,
                LogLevel = this.LogLevel,
                EnableFileLogging = this.EnableFileLogging,
                EmotionAnalysis = this.EmotionAnalysis?.Clone(),
                UseTimeTrigger = this.UseTimeTrigger,
                UseBubbleTrigger = this.UseBubbleTrigger,
                BubbleTriggerProbability = this.BubbleTriggerProbability,
                UseAccurateImageMatching = this.UseAccurateImageMatching,
                AccurateMatchingVersion = this.AccurateMatchingVersion
            };
        }

        /// <summary>
        /// 比较两个设置对象是否相等
        /// </summary>
        public bool Equals(ImageSettings other)
        {
            if (other == null) return false;

            return IsEnabled == other.IsEnabled &&
                   EnableBuiltInImages == other.EnableBuiltInImages &&
                   EnableDIYImages == other.EnableDIYImages &&
                   DisplayDuration == other.DisplayDuration &&
                   DisplayInterval == other.DisplayInterval &&
                   UseRandomInterval == other.UseRandomInterval &&
                   DebugMode == other.DebugMode &&
                   UseTimeTrigger == other.UseTimeTrigger &&
                   UseBubbleTrigger == other.UseBubbleTrigger &&
                   BubbleTriggerProbability == other.BubbleTriggerProbability &&
                   UseAccurateImageMatching == other.UseAccurateImageMatching &&
                   AccurateMatchingVersion == other.AccurateMatchingVersion &&
                   (EmotionAnalysis == null && other.EmotionAnalysis == null ||
                    EmotionAnalysis != null && EmotionAnalysis.Equals(other.EmotionAnalysis));
        }

        /// <summary>
        /// 从文件加载设置
        /// </summary>
        public static ImageSettings LoadFromFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var settings = JsonSerializer.Deserialize<ImageSettings>(json);
                    return settings ?? new ImageSettings();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VPet表情包] 加载设置失败: {ex.Message}");
            }

            return new ImageSettings();
        }

        /// <summary>
        /// 保存设置到文件
        /// </summary>
        public void SaveToFile(string filePath)
        {
            try
            {
                // 确保目录存在
                string directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VPet表情包] 保存设置失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 获取实际的显示间隔（毫秒）
        /// </summary>
        public int GetDisplayIntervalMs()
        {
            if (UseRandomInterval)
            {
                // 随机1-3分钟
                var random = new Random();
                int minutes = random.Next(1, 4);
                return minutes * 60 * 1000;
            }
            else
            {
                // 固定间隔
                return DisplayInterval * 60 * 1000;
            }
        }

        /// <summary>
        /// 获取显示时长（毫秒）
        /// </summary>
        public int GetDisplayDurationMs()
        {
            return DisplayDuration * 1000;
        }

        /// <summary>
        /// 检查是否应该触发气泡表情包（基于概率）
        /// </summary>
        public bool ShouldTriggerBubble()
        {
            if (!UseBubbleTrigger || BubbleTriggerProbability <= 0)
                return false;

            var random = new Random();
            int randomValue = random.Next(1, 101); // 1-100
            return randomValue <= BubbleTriggerProbability;
        }

        /// <summary>
        /// 检查并更新精确匹配模式版本（当UseAccurateImageMatching设置变化时）
        /// </summary>
        /// <param name="previousSettings">之前的设置</param>
        /// <returns>是否发生了变化</returns>
        public bool UpdateAccurateMatchingVersionIfChanged(ImageSettings previousSettings)
        {
            if (previousSettings == null || 
                UseAccurateImageMatching != previousSettings.UseAccurateImageMatching)
            {
                AccurateMatchingVersion++;
                Utils.Logger.Info("ImageSettings", $"精确匹配模式设置发生变化，版本号更新为: {AccurateMatchingVersion}");
                return true;
            }
            return false;
        }
    }
}
