using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VPet.Plugin.LLMEP.EmotionAnalysis
{
    /// <summary>
    /// 缓存条目
    /// </summary>
    public class CacheEntry
    {
        public string TextHash { get; set; }
        public List<string> Emotions { get; set; }
        public int HitCount { get; set; }
        public DateTime LastUsed { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// 缓存管理器 - 两层缓存架构，支持7天过期
    /// </summary>
    public class CacheManager
    {
        private readonly Dictionary<string, CacheEntry> _memoryCache; // 内存缓存（快速）
        private readonly Dictionary<string, CacheEntry> _persistentCache; // 持久化缓存
        private readonly string _cachePath;
        private readonly string _versionPath; // 版本文件路径
        private int _lastKnownVersion = 1; // 上次已知的精确匹配模式版本
        private const int MAX_MEMORY_CACHE_SIZE = 100;
        private const int MAX_PERSISTENT_CACHE_SIZE = 1000;
        private const int CACHE_EXPIRATION_DAYS = 7; // 7天过期
        private DateTime _lastSaveTime;
        private readonly TimeSpan _saveInterval = TimeSpan.FromMinutes(5);

        public CacheManager(string cachePath)
        {
            _cachePath = cachePath;
            _versionPath = Path.ChangeExtension(cachePath, ".version");
            _memoryCache = new Dictionary<string, CacheEntry>();
            _persistentCache = new Dictionary<string, CacheEntry>();
            _lastSaveTime = DateTime.Now;
        }

        /// <summary>
        /// 加载缓存
        /// </summary>
        public void Load()
        {
            try
            {
                // 加载版本信息
                LoadVersion();

                if (File.Exists(_cachePath))
                {
                    var json = File.ReadAllText(_cachePath);
                    var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json);

                    if (entries != null)
                    {
                        var now = DateTime.Now;
                        var validEntries = 0;
                        var expiredEntries = 0;

                        foreach (var entry in entries)
                        {
                            // 检查是否过期（7天未使用）
                            var daysSinceLastUse = (now - entry.LastUsed).TotalDays;
                            if (daysSinceLastUse <= CACHE_EXPIRATION_DAYS)
                            {
                                _persistentCache[entry.TextHash] = entry;
                                validEntries++;
                            }
                            else
                            {
                                expiredEntries++;
                            }
                        }

                        Console.WriteLine($"[Cache] Loaded {validEntries} valid entries, removed {expiredEntries} expired entries");
                        Utils.Logger.Info("CacheManager", $"加载了 {validEntries} 个有效缓存条目，移除了 {expiredEntries} 个过期条目");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cache] Load error: {ex.Message}");
                Utils.Logger.Warning("CacheManager", $"加载缓存失败: {ex.Message}");
                // 如果加载失败，从空缓存开始
            }
        }

        /// <summary>
        /// 加载版本信息
        /// </summary>
        private void LoadVersion()
        {
            try
            {
                if (File.Exists(_versionPath))
                {
                    var versionText = File.ReadAllText(_versionPath);
                    if (int.TryParse(versionText, out int version))
                    {
                        _lastKnownVersion = version;
                        Utils.Logger.Debug("CacheManager", $"加载缓存版本: {_lastKnownVersion}");
                    }
                }
            }
            catch (Exception ex)
            {
                Utils.Logger.Warning("CacheManager", $"加载版本信息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 保存版本信息
        /// </summary>
        private void SaveVersion()
        {
            try
            {
                File.WriteAllText(_versionPath, _lastKnownVersion.ToString());
                Utils.Logger.Debug("CacheManager", $"保存缓存版本: {_lastKnownVersion}");
            }
            catch (Exception ex)
            {
                Utils.Logger.Warning("CacheManager", $"保存版本信息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查版本并在需要时清空缓存
        /// </summary>
        /// <param name="currentVersion">当前的精确匹配模式版本</param>
        /// <returns>是否清空了缓存</returns>
        public bool CheckVersionAndClearIfNeeded(int currentVersion)
        {
            if (currentVersion != _lastKnownVersion)
            {
                Utils.Logger.Info("CacheManager", $"检测到精确匹配模式版本变化: {_lastKnownVersion} -> {currentVersion}，清空所有缓存");

                // 清空内存缓存
                _memoryCache.Clear();

                // 清空持久化缓存
                _persistentCache.Clear();

                // 更新版本号
                _lastKnownVersion = currentVersion;

                // 立即保存空缓存和新版本
                Save();
                SaveVersion();

                Utils.Logger.Info("CacheManager", "缓存已清空，版本已更新");
                return true;
            }
            return false;
        }

        /// <summary>
        /// 保存缓存
        /// </summary>
        public void Save()
        {
            try
            {
                var now = DateTime.Now;

                // 合并内存缓存和持久化缓存，过滤过期条目
                var validEntries = _persistentCache.Values
                    .Where(e => (now - e.LastUsed).TotalDays <= CACHE_EXPIRATION_DAYS)
                    .ToList();

                // 按使用频率排序，保留前1000个
                var topEntries = validEntries
                    .OrderByDescending(e => e.HitCount)
                    .ThenByDescending(e => e.LastUsed)
                    .Take(MAX_PERSISTENT_CACHE_SIZE)
                    .ToList();

                var json = JsonSerializer.Serialize(topEntries, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(_cachePath, json);
                _lastSaveTime = DateTime.Now;

                var removedCount = validEntries.Count - topEntries.Count;
                Console.WriteLine($"[Cache] Saved {topEntries.Count} entries, removed {removedCount} low-priority entries");
                Utils.Logger.Info("CacheManager", $"保存了 {topEntries.Count} 个缓存条目，移除了 {removedCount} 个低优先级条目");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cache] Save error: {ex.Message}");
                Utils.Logger.Error("CacheManager", $"保存缓存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 尝试从缓存获取情感
        /// </summary>
        public bool TryGetEmotion(string text, out List<string> emotions)
        {
            var hash = ComputeHash(text);
            var now = DateTime.Now;

            // 先查内存缓存
            if (_memoryCache.TryGetValue(hash, out var entry))
            {
                // 检查是否过期
                if ((now - entry.LastUsed).TotalDays > CACHE_EXPIRATION_DAYS)
                {
                    _memoryCache.Remove(hash);
                    _persistentCache.Remove(hash);
                    emotions = null;
                    Console.WriteLine($"[Cache] Expired entry removed: {text}");
                    Utils.Logger.Debug("CacheManager", $"移除过期缓存条目: {text}");
                    return false;
                }

                entry.HitCount++;
                entry.LastUsed = now;
                emotions = entry.Emotions;
                Console.WriteLine($"[Cache] Memory hit: {text} -> {string.Join(", ", emotions)}");
                Utils.Logger.Debug("CacheManager", $"内存缓存命中: {text} -> [{string.Join(", ", emotions)}]");
                return true;
            }

            // 再查持久化缓存
            if (_persistentCache.TryGetValue(hash, out entry))
            {
                // 检查是否过期
                if ((now - entry.LastUsed).TotalDays > CACHE_EXPIRATION_DAYS)
                {
                    _persistentCache.Remove(hash);
                    emotions = null;
                    Console.WriteLine($"[Cache] Expired entry removed: {text}");
                    Utils.Logger.Debug("CacheManager", $"移除过期缓存条目: {text}");
                    return false;
                }

                entry.HitCount++;
                entry.LastUsed = now;
                emotions = entry.Emotions;

                // 提升到内存缓存
                AddToMemoryCache(hash, entry);

                Console.WriteLine($"[Cache] Persistent hit: {text} -> {string.Join(", ", emotions)}");
                Utils.Logger.Debug("CacheManager", $"持久化缓存命中: {text} -> [{string.Join(", ", emotions)}]");
                return true;
            }

            emotions = null;
            return false;
        }

        /// <summary>
        /// 缓存情感分析结果
        /// </summary>
        public void CacheEmotion(string text, List<string> emotions)
        {
            var hash = ComputeHash(text);
            var now = DateTime.Now;

            var entry = new CacheEntry
            {
                TextHash = hash,
                Emotions = emotions,
                HitCount = 1,
                LastUsed = now,
                CreatedAt = now
            };

            // 添加到内存缓存
            AddToMemoryCache(hash, entry);

            // 添加到持久化缓存
            _persistentCache[hash] = entry;

            Console.WriteLine($"[Cache] Cached: {text} -> {string.Join(", ", emotions)}");
            Utils.Logger.Info("CacheManager", $"缓存新结果: {text} -> [{string.Join(", ", emotions)}]");

            // 立即保存缓存以确保数据持久化
            Save();
        }

        /// <summary>
        /// 添加到内存缓存（LRU淘汰）
        /// </summary>
        private void AddToMemoryCache(string hash, CacheEntry entry)
        {
            if (_memoryCache.Count >= MAX_MEMORY_CACHE_SIZE)
            {
                // LRU淘汰：移除最久未使用的条目
                var oldestKey = _memoryCache
                    .OrderBy(kvp => kvp.Value.LastUsed)
                    .First()
                    .Key;
                _memoryCache.Remove(oldestKey);
            }

            _memoryCache[hash] = entry;
        }

        /// <summary>
        /// 计算文本的SHA256哈希
        /// </summary>
        private string ComputeHash(string text)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(text.ToLower().Trim());
                var hash = sha256.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }
    }
}
