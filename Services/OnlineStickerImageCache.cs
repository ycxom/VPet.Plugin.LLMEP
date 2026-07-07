#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VPet.Plugin.LLMEP.Utils;

namespace VPet.Plugin.LLMEP.Services
{
    /// <summary>
    /// 缓存索引条目：记录每张缓存图片对应的标签与访问时间，供离线检索和过期淘汰使用
    /// </summary>
    public class CacheIndexEntry
    {
        public string Id { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        public string Extension { get; set; } = ".bin";
        public DateTime CachedAt { get; set; }
        public DateTime LastAccessedAt { get; set; }
    }

    /// <summary>
    /// 在线表情包磁盘缓存。
    /// 所有缓存图片和标签索引打包进一个 zip，再整体 AES 加密并在最前面注入自定义魔术头，
    /// 落成系统 Temp 目录下的单一文件：既不在 Temp 里留一堆可以直接双击打开、逐张扒走的原始图片，
    /// 也防止把扩展名改成 .zip 后被解压工具直接识别打开，降低服务端图库被顺手整体搬走的风险。
    /// 命中同一张图时直接从内存里返回，不必再次向服务端请求（无论是二进制还是 Base64）。
    /// </summary>
    public class OnlineStickerImageCache
    {
        private const int MagicHeaderLength = 8;
        private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("VPLLMSC1"); // VPetLLM Sticker Cache v1
        private static readonly byte[] EncryptionKey = DeriveKey("VPetLLM-OnlineStickerCache-DoNotRedistribute-v1");

        private readonly string _archivePath;
        private readonly object _lock = new();
        private readonly Dictionary<string, byte[]> _imageBytesById = new();
        private readonly Dictionary<string, CacheIndexEntry> _index = new();
        private readonly Random _random = new();

        private const int CacheExpirationDays = 7;
        private const int MaxCachedFiles = 500;

        public OnlineStickerImageCache()
        {
            var cacheDir = Path.Combine(Path.GetTempPath(), "VPetLLM_OnlineStickerCache");
            Directory.CreateDirectory(cacheDir);
            _archivePath = Path.Combine(cacheDir, "sticker.cache");

            Load();
        }

        public bool TryGet(string id, out byte[] bytes)
        {
            lock (_lock)
            {
                if (_imageBytesById.TryGetValue(id, out var cached))
                {
                    bytes = cached;
                    if (_index.TryGetValue(id, out var entry))
                    {
                        entry.LastAccessedAt = DateTime.UtcNow;
                    }
                    Logger.Debug("OnlineStickerImageCache", $"缓存命中: {id}");
                    return true;
                }
            }

            bytes = Array.Empty<byte>();
            return false;
        }

        public void Save(string id, byte[] bytes, string? contentType, IEnumerable<string>? tags = null)
        {
            lock (_lock)
            {
                try
                {
                    var ext = ExtensionFromContentType(contentType, bytes);
                    _imageBytesById[id] = bytes;
                    _index[id] = new CacheIndexEntry
                    {
                        Id = id,
                        Tags = (tags ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList(),
                        Extension = ext,
                        CachedAt = DateTime.UtcNow,
                        LastAccessedAt = DateTime.UtcNow
                    };

                    Logger.Debug("OnlineStickerImageCache", $"已缓存: {id} ({bytes.Length} 字节)");

                    CleanupIfNeeded();
                    Persist();
                }
                catch (Exception ex)
                {
                    Logger.Warning("OnlineStickerImageCache", $"写入缓存失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 在本地缓存里按标签做离线检索（网络不可用时的兜底）：
        /// 统计每个候选与查询标签的重合数，取重合最多的一批中随机挑一个
        /// </summary>
        public bool TryFindOffline(IEnumerable<string> queryTags, out string? id, out byte[]? bytes)
        {
            id = null;
            bytes = null;

            var normalizedQuery = queryTags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim().ToLowerInvariant())
                .ToList();

            if (normalizedQuery.Count == 0)
            {
                return false;
            }

            lock (_lock)
            {
                var scored = _index.Values
                    .Select(e => new
                    {
                        Entry = e,
                        Score = e.Tags.Count(t => normalizedQuery.Contains(t.Trim().ToLowerInvariant()))
                    })
                    .Where(x => x.Score > 0)
                    .ToList();

                if (scored.Count == 0)
                {
                    return false;
                }

                var maxScore = scored.Max(x => x.Score);
                var best = scored.Where(x => x.Score == maxScore).ToList();
                var picked = best[_random.Next(best.Count)].Entry;

                if (!_imageBytesById.TryGetValue(picked.Id, out var cachedBytes))
                {
                    // 索引里有记录但图片数据已被清理，顺手把陈旧索引项去掉
                    _index.Remove(picked.Id);
                    return false;
                }

                picked.LastAccessedAt = DateTime.UtcNow;
                id = picked.Id;
                bytes = cachedBytes;
                Logger.Debug("OnlineStickerImageCache", $"离线缓存命中: {picked.Id}, 匹配标签数: {maxScore}");
                return true;
            }
        }

        /// <summary>
        /// 所有已缓存图片的标签并集，供离线时凑一份"可用标签"列表
        /// </summary>
        public List<string> GetAllCachedTags()
        {
            lock (_lock)
            {
                return _index.Values
                    .SelectMany(e => e.Tags)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        /// <summary>
        /// 读取并解密缓存归档：校验魔术头 -> AES 解密 -> 解压 zip -> 载入索引与图片数据
        /// </summary>
        private void Load()
        {
            try
            {
                if (!File.Exists(_archivePath))
                {
                    return;
                }

                var raw = File.ReadAllBytes(_archivePath);
                if (raw.Length <= MagicHeaderLength + 16)
                {
                    return; // 太短，视为损坏或空缓存
                }

                for (var i = 0; i < MagicHeaderLength; i++)
                {
                    if (raw[i] != MagicHeader[i])
                    {
                        Logger.Warning("OnlineStickerImageCache", "缓存文件头不匹配，忽略旧缓存文件");
                        return;
                    }
                }

                var iv = new byte[16];
                Array.Copy(raw, MagicHeaderLength, iv, 0, 16);
                var cipherText = new byte[raw.Length - MagicHeaderLength - 16];
                Array.Copy(raw, MagicHeaderLength + 16, cipherText, 0, cipherText.Length);

                var zipBytes = Decrypt(cipherText, iv);

                using var ms = new MemoryStream(zipBytes);
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

                var indexEntry = zip.GetEntry("index.json");
                if (indexEntry != null)
                {
                    using var reader = new StreamReader(indexEntry.Open());
                    var json = reader.ReadToEnd();
                    var entries = JsonSerializer.Deserialize<List<CacheIndexEntry>>(json);
                    if (entries != null)
                    {
                        foreach (var e in entries)
                        {
                            _index[e.Id] = e;
                        }
                    }
                }

                foreach (var entry in zip.Entries)
                {
                    if (entry.Name == "index.json")
                    {
                        continue;
                    }

                    var id = Path.GetFileNameWithoutExtension(entry.Name);
                    using var entryStream = entry.Open();
                    using var buffer = new MemoryStream();
                    entryStream.CopyTo(buffer);
                    _imageBytesById[id] = buffer.ToArray();
                }

                Logger.Info("OnlineStickerImageCache", $"已加载离线缓存: {_imageBytesById.Count} 张图片");
            }
            catch (Exception ex)
            {
                Logger.Warning("OnlineStickerImageCache", $"加载缓存失败，将从空缓存开始: {ex.Message}");
                _imageBytesById.Clear();
                _index.Clear();
            }
        }

        /// <summary>
        /// 调用方需持有 _lock。把内存里的图片和索引重新打包成 zip，加密后连同魔术头写回单一文件
        /// </summary>
        private void Persist()
        {
            try
            {
                using var zipStream = new MemoryStream();
                using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var indexJson = JsonSerializer.Serialize(_index.Values.ToList());
                    var indexZipEntry = zip.CreateEntry("index.json", CompressionLevel.Fastest);
                    using (var writer = new StreamWriter(indexZipEntry.Open()))
                    {
                        writer.Write(indexJson);
                    }

                    foreach (var kvp in _imageBytesById)
                    {
                        var ext = _index.TryGetValue(kvp.Key, out var entry) ? entry.Extension : ".bin";
                        var imageZipEntry = zip.CreateEntry(kvp.Key + ext, CompressionLevel.Fastest);
                        using var entryStream = imageZipEntry.Open();
                        entryStream.Write(kvp.Value, 0, kvp.Value.Length);
                    }
                }

                var iv = RandomNumberGenerator.GetBytes(16);
                var encrypted = Encrypt(zipStream.ToArray(), iv);

                using var output = new MemoryStream();
                output.Write(MagicHeader, 0, MagicHeader.Length);
                output.Write(iv, 0, iv.Length);
                output.Write(encrypted, 0, encrypted.Length);

                File.WriteAllBytes(_archivePath, output.ToArray());
            }
            catch (Exception ex)
            {
                Logger.Warning("OnlineStickerImageCache", $"持久化缓存失败: {ex.Message}");
            }
        }

        private static byte[] DeriveKey(string seed)
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        }

        private static byte[] Encrypt(byte[] data, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.Key = EncryptionKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            return encryptor.TransformFinalBlock(data, 0, data.Length);
        }

        private static byte[] Decrypt(byte[] data, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.Key = EncryptionKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(data, 0, data.Length);
        }

        private static string ExtensionFromContentType(string? contentType, byte[] bytes)
        {
            switch (contentType)
            {
                case "image/gif": return ".gif";
                case "image/png": return ".png";
                case "image/jpeg": return ".jpg";
                case "image/webp": return ".webp";
                case "image/bmp": return ".bmp";
            }

            // Content-Type 缺失时退化为魔术头部识别
            if (bytes.Length > 4)
            {
                if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38) return ".gif";
                if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
                if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
            }
            return ".bin";
        }

        /// <summary>
        /// 调用方需持有 _lock。清理过期（7天未访问）或超出数量上限的缓存条目
        /// </summary>
        private void CleanupIfNeeded()
        {
            var now = DateTime.UtcNow;
            var expiredIds = _index.Values
                .Where(e => (now - e.LastAccessedAt).TotalDays > CacheExpirationDays)
                .Select(e => e.Id)
                .ToList();

            foreach (var id in expiredIds)
            {
                _index.Remove(id);
                _imageBytesById.Remove(id);
            }

            if (_index.Count > MaxCachedFiles)
            {
                var overflowIds = _index.Values
                    .OrderByDescending(e => e.LastAccessedAt)
                    .Skip(MaxCachedFiles)
                    .Select(e => e.Id)
                    .ToList();

                foreach (var id in overflowIds)
                {
                    _index.Remove(id);
                    _imageBytesById.Remove(id);
                }
            }
        }
    }
}
