using System;
using System.IO;
using System.Text.Json;

namespace VPet.Plugin.Image
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
                DebugMode = this.DebugMode
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
                   DebugMode == other.DebugMode;
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
    }
}
