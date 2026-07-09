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
    /// 每张图片单独打包成一个 zip entry 再 AES 加密，落成 Temp 目录下互不关联的 &lt;id&gt;.vpc 文件；
    /// 标签索引单独加密成一个很小的 index.dat。所有文件都以自定义魔术头开头——双击打不开，
    /// 改后缀当 zip 解压也读不出东西，降低服务端图库被顺手整体搬走的风险。
    /// 内存里只常驻标签索引（几十 KB 量级）和一个有限大小的最近使用图片 LRU，
    /// 图片字节按需从磁盘解密读取，不会把几百张图全部常驻内存。
    /// </summary>
    public class OnlineStickerImageCache
    {
        private const int MagicHeaderLength = 8;
        private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("VPLLMSC1"); // VPetLLM Sticker Cache v1
        private static readonly byte[] EncryptionKey = DeriveKey("VPetLLM-OnlineStickerCache-DoNotRedistribute-v1");

        private const int CacheExpirationDays = 7;
        private const int MaxCachedFiles = 500;
        private const int MaxMemoryCachedImages = 30; // 内存 LRU 上限张数，避免几百张图全部常驻内存

        private readonly string _cacheDir;
        private readonly string _indexPath;
        private readonly object _lock = new();
        private readonly Dictionary<string, CacheIndexEntry> _index = new();

        // 简单 LRU：最近访问过的解密图片字节，超过上限淘汰最久未用的一张
        private readonly LinkedList<string> _lruOrder = new();
        private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new();
        private readonly Dictionary<string, byte[]> _lruBytes = new();

        private readonly Random _random = new();

        public OnlineStickerImageCache()
        {
            _cacheDir = Path.Combine(Path.GetTempPath(), "VPetLLM_OnlineStickerCache");
            Directory.CreateDirectory(_cacheDir);
            _indexPath = Path.Combine(_cacheDir, "index.dat");
            LoadIndex();
        }

        public bool TryGet(string id, out byte[] bytes)
        {
            lock (_lock)
            {
                if (TouchLru(id, out var cached))
                {
                    bytes = cached;
                    BumpAccess(id);
                    return true;
                }
            }

            if (LoadImageFromDisk(id, out var loaded))
            {
                lock (_lock)
                {
                    InsertLru(id, loaded);
                    BumpAccess(id);
                }
                bytes = loaded;
                Logger.Debug("OnlineStickerImageCache", $"缓存命中: {id}");
                return true;
            }

            bytes = Array.Empty<byte>();
            return false;
        }

        public void Save(string id, byte[] bytes, string? contentType, IEnumerable<string>? tags = null)
        {
            try
            {
                var ext = ExtensionFromContentType(contentType, bytes);
                SaveImageToDisk(id, bytes, ext);

                lock (_lock)
                {
                    _index[id] = new CacheIndexEntry
                    {
                        Id = id,
                        Tags = (tags ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList(),
                        Extension = ext,
                        CachedAt = DateTime.UtcNow,
                        LastAccessedAt = DateTime.UtcNow
                    };
                    InsertLru(id, bytes);
                    CleanupIfNeeded();
                    PersistIndex();
                }

                Logger.Debug("OnlineStickerImageCache", $"已缓存: {id} ({bytes.Length} 字节)");
            }
            catch (Exception ex)
            {
                Logger.Warning("OnlineStickerImageCache", $"写入缓存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 在本地缓存里按标签做离线检索（网络不可用时的兜底）：
        /// 统计每个候选与查询标签的重合数，取重合最多的一批中随机挑一个，再按需从磁盘读取
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

            string pickedId;
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
                pickedId = best[_random.Next(best.Count)].Entry.Id;
            }

            if (!TryGet(pickedId, out var pickedBytes))
            {
                // 索引里有记录但磁盘文件已经不在了，顺手把陈旧索引项去掉
                lock (_lock)
                {
                    _index.Remove(pickedId);
                    PersistIndex();
                }
                return false;
            }

            id = pickedId;
            bytes = pickedBytes;
            Logger.Debug("OnlineStickerImageCache", $"离线缓存命中: {pickedId}");
            return true;
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

        // ---------- 内存 LRU（调用方需持有 _lock） ----------

        private bool TouchLru(string id, out byte[] bytes)
        {
            if (_lruBytes.TryGetValue(id, out var cached))
            {
                var node = _lruNodes[id];
                _lruOrder.Remove(node);
                _lruOrder.AddFirst(node);
                bytes = cached;
                return true;
            }
            bytes = Array.Empty<byte>();
            return false;
        }

        private void InsertLru(string id, byte[] bytes)
        {
            if (_lruNodes.TryGetValue(id, out var existing))
            {
                _lruOrder.Remove(existing);
            }

            var node = new LinkedListNode<string>(id);
            _lruOrder.AddFirst(node);
            _lruNodes[id] = node;
            _lruBytes[id] = bytes;

            while (_lruOrder.Count > MaxMemoryCachedImages)
            {
                var last = _lruOrder.Last!;
                _lruOrder.RemoveLast();
                _lruNodes.Remove(last.Value);
                _lruBytes.Remove(last.Value);
            }
        }

        private void BumpAccess(string id)
        {
            if (_index.TryGetValue(id, out var entry))
            {
                entry.LastAccessedAt = DateTime.UtcNow;
            }
        }

        // ---------- 磁盘读写：每张图片独立一个加密文件，按需解密，不整体常驻内存 ----------

        private string GetImagePath(string id) => Path.Combine(_cacheDir, id + ".vpc");

        private void SaveImageToDisk(string id, byte[] imageBytes, string ext)
        {
            using var zipStream = new MemoryStream();
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("data" + ext, CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                entryStream.Write(imageBytes, 0, imageBytes.Length);
            }

            var iv = RandomNumberGenerator.GetBytes(16);
            var encrypted = Encrypt(zipStream.ToArray(), iv);
            File.WriteAllBytes(GetImagePath(id), WrapEnvelope(iv, encrypted));
        }

        private bool LoadImageFromDisk(string id, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            var path = GetImagePath(id);

            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                var raw = File.ReadAllBytes(path);
                if (!TryUnwrapEnvelope(raw, out var iv, out var cipherText))
                {
                    return false;
                }

                var zipBytes = Decrypt(cipherText, iv);
                using var ms = new MemoryStream(zipBytes);
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                var entry = zip.Entries.FirstOrDefault();
                if (entry == null)
                {
                    return false;
                }

                using var entryStream = entry.Open();
                using var buffer = new MemoryStream();
                entryStream.CopyTo(buffer);
                bytes = buffer.ToArray();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning("OnlineStickerImageCache", $"读取缓存图片失败: {id}, {ex.Message}");
                return false;
            }
        }

        // ---------- 标签索引：单独一个很小的加密文件，全程常驻内存 ----------

        private void LoadIndex()
        {
            try
            {
                if (!File.Exists(_indexPath))
                {
                    return;
                }

                var raw = File.ReadAllBytes(_indexPath);
                if (!TryUnwrapEnvelope(raw, out var iv, out var cipherText))
                {
                    Logger.Warning("OnlineStickerImageCache", "缓存索引文件头不匹配，忽略旧索引");
                    return;
                }

                var json = Encoding.UTF8.GetString(Decrypt(cipherText, iv));
                var entries = JsonSerializer.Deserialize<List<CacheIndexEntry>>(json);
                if (entries != null)
                {
                    foreach (var e in entries)
                    {
                        _index[e.Id] = e;
                    }
                }

                Logger.Info("OnlineStickerImageCache", $"已加载离线缓存索引: {_index.Count} 条");
            }
            catch (Exception ex)
            {
                Logger.Warning("OnlineStickerImageCache", $"加载缓存索引失败: {ex.Message}");
                _index.Clear();
            }
        }

        /// <summary>调用方需持有 _lock</summary>
        private void PersistIndex()
        {
            try
            {
                var json = JsonSerializer.Serialize(_index.Values.ToList());
                var iv = RandomNumberGenerator.GetBytes(16);
                var encrypted = Encrypt(Encoding.UTF8.GetBytes(json), iv);
                File.WriteAllBytes(_indexPath, WrapEnvelope(iv, encrypted));
            }
            catch (Exception ex)
            {
                Logger.Warning("OnlineStickerImageCache", $"保存缓存索引失败: {ex.Message}");
            }
        }

        // ---------- 魔术头信封：所有落盘文件都长这样，裸眼/解压工具都认不出 ----------

        private static byte[] WrapEnvelope(byte[] iv, byte[] cipherText)
        {
            using var output = new MemoryStream();
            output.Write(MagicHeader, 0, MagicHeader.Length);
            output.Write(iv, 0, iv.Length);
            output.Write(cipherText, 0, cipherText.Length);
            return output.ToArray();
        }

        private static bool TryUnwrapEnvelope(byte[] raw, out byte[] iv, out byte[] cipherText)
        {
            iv = Array.Empty<byte>();
            cipherText = Array.Empty<byte>();

            if (raw.Length <= MagicHeaderLength + 16)
            {
                return false;
            }

            for (var i = 0; i < MagicHeaderLength; i++)
            {
                if (raw[i] != MagicHeader[i])
                {
                    return false;
                }
            }

            iv = new byte[16];
            Array.Copy(raw, MagicHeaderLength, iv, 0, 16);
            cipherText = new byte[raw.Length - MagicHeaderLength - 16];
            Array.Copy(raw, MagicHeaderLength + 16, cipherText, 0, cipherText.Length);
            return true;
        }

        // ---------- 加解密 ----------

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

        /// <summary>调用方需持有 _lock。清理过期（7天未访问）或超出数量上限的缓存条目，含对应磁盘文件</summary>
        private void CleanupIfNeeded()
        {
            var now = DateTime.UtcNow;
            var expiredIds = _index.Values
                .Where(e => (now - e.LastAccessedAt).TotalDays > CacheExpirationDays)
                .Select(e => e.Id)
                .ToList();

            foreach (var id in expiredIds)
            {
                RemoveEntry(id);
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
                    RemoveEntry(id);
                }
            }
        }

        /// <summary>调用方需持有 _lock</summary>
        private void RemoveEntry(string id)
        {
            _index.Remove(id);

            if (_lruNodes.TryGetValue(id, out var node))
            {
                _lruOrder.Remove(node);
                _lruNodes.Remove(id);
                _lruBytes.Remove(id);
            }

            try
            {
                var path = GetImagePath(id);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("OnlineStickerImageCache", $"删除过期缓存文件失败: {id}, {ex.Message}");
            }
        }
    }
}
