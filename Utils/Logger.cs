using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VPet.Plugin.Image.Utils
{
    /// <summary>
    /// 日志等级
    /// </summary>
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3
    }

    /// <summary>
    /// 日志条目
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Category { get; set; }
        public string Message { get; set; }

        public override string ToString()
        {
            return $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level}] [{Category}] {Message}";
        }
    }

    /// <summary>
    /// 分离的日志管理器
    /// </summary>
    public static class Logger
    {
        private static readonly List<LogEntry> _logEntries = new List<LogEntry>();
        private static readonly object _lock = new object();
        private static readonly int _maxEntries = 1000;

        public static LogLevel MinLogLevel { get; set; } = LogLevel.Info;
        public static bool EnableFileLogging { get; set; } = true;
        public static string LogFilePath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VPet.Plugin.Image.log");

        /// <summary>
        /// 记录调试日志
        /// </summary>
        public static void Debug(string category, string message)
        {
            Log(LogLevel.Debug, category, message);
        }

        /// <summary>
        /// 记录信息日志
        /// </summary>
        public static void Info(string category, string message)
        {
            Log(LogLevel.Info, category, message);
        }

        /// <summary>
        /// 记录警告日志
        /// </summary>
        public static void Warning(string category, string message)
        {
            Log(LogLevel.Warning, category, message);
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        public static void Error(string category, string message)
        {
            Log(LogLevel.Error, category, message);
        }

        /// <summary>
        /// 记录日志
        /// </summary>
        private static void Log(LogLevel level, string category, string message)
        {
            // 检查日志等级过滤
            if (level < MinLogLevel)
                return;

            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Category = category ?? "General",
                Message = message ?? ""
            };

            lock (_lock)
            {
                _logEntries.Add(entry);

                // 保持日志条目数量在限制内
                if (_logEntries.Count > _maxEntries)
                {
                    _logEntries.RemoveAt(0);
                }
            }

            // 写入文件
            if (EnableFileLogging)
            {
                WriteToFile(entry);
            }
        }

        /// <summary>
        /// 写入日志文件
        /// </summary>
        private static void WriteToFile(LogEntry entry)
        {
            try
            {
                File.AppendAllText(LogFilePath, entry.ToString() + Environment.NewLine);
            }
            catch
            {
                // 忽略文件写入错误，避免影响主功能
            }
        }

        /// <summary>
        /// 获取所有日志条目
        /// </summary>
        public static List<LogEntry> GetLogEntries()
        {
            lock (_lock)
            {
                return _logEntries.ToList();
            }
        }

        /// <summary>
        /// 获取指定等级的日志条目
        /// </summary>
        public static List<LogEntry> GetLogEntries(LogLevel minLevel)
        {
            lock (_lock)
            {
                return _logEntries.Where(e => e.Level >= minLevel).ToList();
            }
        }

        /// <summary>
        /// 获取指定分类的日志条目
        /// </summary>
        public static List<LogEntry> GetLogEntries(string category)
        {
            lock (_lock)
            {
                return _logEntries.Where(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }

        /// <summary>
        /// 获取格式化的日志文本
        /// </summary>
        public static List<string> GetFormattedLogs(LogLevel minLevel = LogLevel.Info)
        {
            var entries = GetLogEntries(minLevel);
            return entries.Select(e => e.ToString()).ToList();
        }

        /// <summary>
        /// 清空日志
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _logEntries.Clear();
            }
        }

        /// <summary>
        /// 设置日志等级
        /// </summary>
        public static void SetLogLevel(LogLevel level)
        {
            MinLogLevel = level;
            Info("Logger", $"日志等级设置为: {level}");
        }

        /// <summary>
        /// 获取日志统计信息
        /// </summary>
        public static string GetStatistics()
        {
            lock (_lock)
            {
                var debugCount = _logEntries.Count(e => e.Level == LogLevel.Debug);
                var infoCount = _logEntries.Count(e => e.Level == LogLevel.Info);
                var warningCount = _logEntries.Count(e => e.Level == LogLevel.Warning);
                var errorCount = _logEntries.Count(e => e.Level == LogLevel.Error);

                return $"总计: {_logEntries.Count} | Debug: {debugCount} | Info: {infoCount} | Warning: {warningCount} | Error: {errorCount}";
            }
        }
    }
}