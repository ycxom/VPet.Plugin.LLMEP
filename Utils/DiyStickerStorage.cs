#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VPet.Plugin.LLMEP.Utils
{
    /// <summary>
    /// DIY 表情包的统一存放位置：文档\VPetLLM\Emotion\，单层目录，不再分子目录。
    ///
    /// 图片直接平铺在这个目录里；每张图用于哪些心情，全部由 diy_labels.json 里的
    /// 心情标签（happy / normal / poor / ill）决定，在插件设置的「标签管理」里勾选。
    /// 一个心情都不勾 = 泛用，任何心情下都可能出现。
    ///
    /// 以前放在 MOD 目录的 DIY_Expression\（按心情分子目录 + 标签系统的任意子目录）和
    /// plugin\data\diy_labels.json：创意工坊目录归 Steam 管，更新时会用包里的示例
    /// diy_labels.json 覆盖用户标签。启动时会把旧数据一次性收拢到这里。
    /// </summary>
    public static class DiyStickerStorage
    {
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };

        /// <summary>旧的按心情分类子目录名 → 心情标签。收拢时据此补心情标签，用户原有分类不丢。</summary>
        private static readonly Dictionary<string, string> LegacyMoodFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Happy"] = "happy",
            ["Normal"] = "normal",
            ["Nomal"] = "normal",
            ["PoorCondition"] = "poor",
            ["Ill"] = "ill",
        };

        private const string LabelsFileName = "diy_labels.json";
        private const string LegacyModFolderName = "DIY_Expression";
        private const string ReadmeFileName = "README.txt";

        /// <summary>文档\VPetLLM\Emotion</summary>
        public static string RootPath { get; } = ResolveRoot();

        /// <summary>标签文件：{ "文件名": ["标签", ...] }</summary>
        public static string LabelsFilePath => Path.Combine(RootPath, LabelsFileName);

        private const string DataFolderName = "data";

        /// <summary>
        /// 插件自己的数据（设置、情感分析缓存、日志）：文档\VPetLLM\Emotion\data。
        /// 和图片分开放，用户往 Emotion 里拖图时看不到这些文件；表情包整理也不会碰这个目录。
        /// </summary>
        public static string DataPath => Path.Combine(RootPath, DataFolderName);

        /// <summary>以前写在 MOD 目录 plugin\data\（更早是 MOD 根目录）的插件数据文件。</summary>
        private static readonly string[] PluginDataFiles =
        {
            "settings.json", "emotion_cache.json", "emotion_cache.version", "VPet.Plugin.LLMEP.log"
        };

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static string ResolveRoot()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents))
            {
                documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            }
            return Path.Combine(documents, "VPetLLM", "Emotion");
        }

        /// <summary>确保目录存在，并放一份使用说明（已有目录从旧的分子目录布局升级上来时也补上）。</summary>
        public static string EnsureRoot()
        {
            try
            {
                Directory.CreateDirectory(RootPath);
                var readmePath = Path.Combine(RootPath, ReadmeFileName);
                if (!File.Exists(readmePath))
                {
                    File.WriteAllText(readmePath,
                        "LLM表情包 - DIY 表情包目录\r\n\r\n" +
                        "把表情包图片直接放进这个文件夹即可（PNG、GIF 动图、JPG/JPEG、BMP），不需要建子文件夹。\r\n\r\n" +
                        "每张图片用于哪些心情，在插件设置的「🏷️ 标签管理」里勾选：开心 / 正常 / 状态不佳 / 生病，可多选。\r\n" +
                        "一个都不勾的图片作为泛用表情包，任何心情下都可能出现。\r\n\r\n" +
                        "diy_labels.json 保存图片的标签和心情设置，data 文件夹保存插件设置和缓存，请勿手动删除。\r\n");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("DiyStickerStorage", $"创建 DIY 表情包目录失败: {RootPath}, {ex.Message}");
            }
            return RootPath;
        }

        /// <summary>确保插件数据目录存在并返回它。</summary>
        public static string EnsureDataPath()
        {
            Directory.CreateDirectory(DataPath);
            return DataPath;
        }

        /// <summary>
        /// 把设置、情感分析缓存、日志从 MOD 目录迁到 <see cref="DataPath"/>。可重复调用。
        /// 必须在读取设置之前调用：创意工坊更新会清掉 MOD 目录里不属于发布包的文件，
        /// 设置放那里等于每次更新都可能被重置。
        /// 新位置已有同名文件时以新位置为准，旧文件改名 .migrated 留作备份。
        /// </summary>
        /// <param name="modDirectory">MOD 根目录（info.lps 所在目录）</param>
        public static void MigratePluginData(string modDirectory)
        {
            if (string.IsNullOrEmpty(modDirectory))
                return;

            try
            {
                EnsureDataPath();
            }
            catch (Exception ex)
            {
                Logger.Error("DiyStickerStorage", $"创建插件数据目录失败: {DataPath}, {ex.Message}");
                return;
            }

            // plugin\data 是上一代位置，MOD 根目录是更早的位置；同名时以较新的为准
            var oldDirectories = new[] { Path.Combine(modDirectory, "plugin", "data"), modDirectory };
            foreach (var name in PluginDataFiles)
            {
                foreach (var oldDirectory in oldDirectories)
                {
                    var source = Path.Combine(oldDirectory, name);
                    if (!File.Exists(source))
                        continue;

                    var target = Path.Combine(DataPath, name);
                    try
                    {
                        if (File.Exists(target))
                        {
                            RetireFile(source);
                            Logger.Info("DiyStickerStorage", $"{name} 新位置已存在，旧文件已改名 .migrated: {source}");
                        }
                        else
                        {
                            File.Move(source, target);
                            Logger.Info("DiyStickerStorage", $"已迁移 {name}: {source} -> {target}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("DiyStickerStorage", $"迁移 {name} 失败（下次启动重试）: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>目录里的全部表情包图片（只看顶层）。任意线程可调用。</summary>
        public static List<string> EnumerateImages()
        {
            try
            {
                if (!Directory.Exists(RootPath))
                    return new List<string>();

                return Directory.EnumerateFiles(RootPath, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsImageFile)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Logger.Warning("DiyStickerStorage", $"枚举 DIY 表情包失败: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>判断文件扩展名是否是支持的表情包图片。</summary>
        public static bool IsImageFile(string path)
        {
            var ext = Path.GetExtension(path);
            return ImageExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 把外部图片复制进目录。同名且内容相同时直接复用；同名不同内容时自动改名。
        /// </summary>
        /// <returns>目录里的文件名</returns>
        public static string Import(string sourcePath)
        {
            EnsureRoot();
            var fileName = Path.GetFileName(sourcePath);
            var target = Path.Combine(RootPath, fileName);

            if (File.Exists(target))
            {
                if (SameContent(sourcePath, target))
                    return fileName;
                target = UniqueTarget(fileName);
            }

            File.Copy(sourcePath, target);
            return Path.GetFileName(target);
        }

        /// <summary>
        /// 一次性迁移/整理，可重复调用：
        /// 1. 旧 MOD 目录 DIY_Expression\ 里的图片收拢到本目录；
        /// 2. 本目录下的子目录里的图片也收拢到顶层（统一单目录管理）；
        /// 3. 旧的 plugin\data\diy_labels.json、label.json 里属于被搬图片的标签合并进来，
        ///    原来所在的心情子目录（Happy / Normal / PoorCondition / Ill）转成对应心情标签。
        /// </summary>
        /// <param name="modDirectory">MOD 根目录（info.lps 所在目录）</param>
        public static void Migrate(string modDirectory)
        {
            EnsureRoot();

            Dictionary<string, List<string>> labels;
            try
            {
                labels = File.Exists(LabelsFilePath) ? ReadLabels(LabelsFilePath) : new Dictionary<string, List<string>>();
            }
            catch (Exception ex)
            {
                // 标签文件坏了就别动它，免得覆盖掉用户还能手动修复的数据
                Logger.Error("DiyStickerStorage", $"读取标签文件失败，跳过整理: {LabelsFilePath}, {ex.Message}");
                return;
            }

            bool changed = false;

            // 1. 旧 MOD 目录
            if (!string.IsNullOrEmpty(modDirectory))
            {
                var oldRoot = Path.Combine(modDirectory, LegacyModFolderName);
                var oldLabelsPath = Path.Combine(modDirectory, "plugin", "data", LabelsFileName);
                var sourceLabels = ReadLabelsQuietly(oldLabelsPath);
                MergeLegacyLabelJson(Path.Combine(oldRoot, "label.json"), sourceLabels);

                changed |= Flatten(oldRoot, includeTopLevel: true, sourceLabels, labels, "旧 MOD 目录");
                RetireFile(oldLabelsPath);
                RetireFile(Path.Combine(oldRoot, "label.json"));
                RemoveEmptyDirectories(oldRoot, includeRoot: true);
            }

            // 2. 本目录下的子目录（以及更早的 label.json 格式）
            var rootLegacy = new Dictionary<string, List<string>>();
            MergeLegacyLabelJson(Path.Combine(RootPath, "label.json"), rootLegacy);
            foreach (var entry in rootLegacy)
            {
                changed |= MergeTags(labels, NormalizeKey(entry.Key), entry.Value);
            }
            RetireFile(Path.Combine(RootPath, "label.json"));

            changed |= Flatten(RootPath, includeTopLevel: false, labels, labels, "子目录");
            RemoveEmptyDirectories(RootPath, includeRoot: false);

            if (changed || !File.Exists(LabelsFilePath))
            {
                try
                {
                    File.WriteAllText(LabelsFilePath, JsonSerializer.Serialize(labels, WriteOptions));
                }
                catch (Exception ex)
                {
                    Logger.Error("DiyStickerStorage", $"写入标签文件失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 把 sourceRoot 下的图片移到 RootPath 顶层，标签按旧相对路径从 sourceLabels 认领，
        /// 改键为新文件名后并入 targetLabels。
        /// </summary>
        private static bool Flatten(string sourceRoot, bool includeTopLevel,
            Dictionary<string, List<string>> sourceLabels, Dictionary<string, List<string>> targetLabels, string what)
        {
            if (!Directory.Exists(sourceRoot))
                return false;

            List<string> files;
            try
            {
                var dataPrefix = DataPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                    .Where(IsImageFile)
                    .Where(f => !f.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase)) // 插件数据目录不参与整理
                    .Where(f => includeTopLevel || !string.Equals(Path.GetDirectoryName(f), sourceRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception ex)
            {
                Logger.Warning("DiyStickerStorage", $"扫描{what}失败: {sourceRoot}, {ex.Message}");
                return false;
            }

            bool changed = false;
            int moved = 0, renamed = 0, duplicates = 0, failed = 0;

            foreach (var source in files)
            {
                try
                {
                    var oldKey = NormalizeKey(Path.GetRelativePath(sourceRoot, source));
                    var fileName = Path.GetFileName(source);
                    var target = Path.Combine(RootPath, fileName);

                    if (File.Exists(target) && SameContent(source, target))
                    {
                        File.Delete(source);
                        duplicates++;
                    }
                    else
                    {
                        if (File.Exists(target))
                        {
                            target = UniqueTarget(fileName);
                            renamed++;
                        }
                        File.Move(source, target);
                        moved++;
                    }

                    var newKey = Path.GetFileName(target);

                    // 认领标签：旧相对路径上的标签 + 原心情子目录对应的心情标签
                    var tags = new List<string>();
                    if (sourceLabels.TryGetValue(oldKey, out var oldTags) && oldTags != null)
                        tags.AddRange(oldTags);
                    var firstSegment = oldKey.Contains('/') ? oldKey.Substring(0, oldKey.IndexOf('/')) : null;
                    if (firstSegment != null && LegacyMoodFolders.TryGetValue(firstSegment, out var moodTag))
                        tags.Add(moodTag);

                    if (ReferenceEquals(sourceLabels, targetLabels) && !string.Equals(oldKey, newKey, StringComparison.Ordinal))
                    {
                        if (targetLabels.Remove(oldKey))
                            changed = true;
                    }

                    changed |= MergeTags(targetLabels, newKey, tags);
                    changed = true;
                }
                catch (Exception ex)
                {
                    // 多开时另一个实例可能正在搬同一个文件；下次启动会再试
                    failed++;
                    Logger.Warning("DiyStickerStorage", $"收拢 DIY 文件失败: {source}, {ex.Message}");
                }
            }

            if (moved + duplicates + failed > 0)
            {
                Logger.Info("DiyStickerStorage",
                    $"已把{what}里的表情包收拢到 {RootPath}: 移动 {moved} 个（其中同名改名 {renamed} 个），重复已清理 {duplicates} 个，失败 {failed} 个");
            }
            return changed;
        }

        private static bool MergeTags(Dictionary<string, List<string>> labels, string key, IEnumerable<string> tags)
        {
            var incoming = tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
            if (incoming.Count == 0)
                return false;

            if (!labels.TryGetValue(key, out var existing) || existing == null)
            {
                labels[key] = incoming.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return true;
            }

            bool changed = false;
            foreach (var tag in incoming)
            {
                if (!existing.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    existing.Add(tag);
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>更早的 label.json 格式：{ images: [{ filename, labels }] }，并入 dict（键为相对路径）。</summary>
        private static void MergeLegacyLabelJson(string path, Dictionary<string, List<string>> into)
        {
            if (!File.Exists(path))
                return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
                    return;

                foreach (var image in images.EnumerateArray())
                {
                    if (!image.TryGetProperty("filename", out var fn) || fn.ValueKind != JsonValueKind.String)
                        continue;
                    if (!image.TryGetProperty("labels", out var lb) || lb.ValueKind != JsonValueKind.Array)
                        continue;

                    var key = NormalizeKey(fn.GetString() ?? "");
                    var tags = lb.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
                    if (key.Length > 0)
                        MergeTags(into, key, tags);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("DiyStickerStorage", $"读取旧格式标签失败: {path}, {ex.Message}");
            }
        }

        /// <summary>迁移过的旧文件改名为 .migrated 留作备份，下次启动不会再合并一遍。</summary>
        private static void RetireFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Move(path, path + ".migrated", overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Warning("DiyStickerStorage", $"旧文件改名失败: {path}, {ex.Message}");
            }
        }

        private static string NormalizeKey(string relativePath) => relativePath.Replace('\\', '/').TrimStart('/');

        private static string UniqueTarget(string fileName)
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            for (int i = 2; ; i++)
            {
                var candidate = Path.Combine(RootPath, $"{name} ({i}){ext}");
                if (!File.Exists(candidate))
                    return candidate;
            }
        }

        private static Dictionary<string, List<string>> ReadLabels(string path)
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json)
                   ?? new Dictionary<string, List<string>>();
        }

        private static Dictionary<string, List<string>> ReadLabelsQuietly(string path)
        {
            try
            {
                return File.Exists(path) ? ReadLabels(path) : new Dictionary<string, List<string>>();
            }
            catch (Exception ex)
            {
                Logger.Warning("DiyStickerStorage", $"读取旧标签失败: {path}, {ex.Message}");
                return new Dictionary<string, List<string>>();
            }
        }

        private static bool SameContent(string a, string b)
        {
            var infoA = new FileInfo(a);
            var infoB = new FileInfo(b);
            if (infoA.Length != infoB.Length)
                return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }

        /// <summary>插件历次自动生成的子目录说明（心情子目录的 README.md、示例 General 目录的 README.txt）。</summary>
        private static readonly string[] SkeletonFiles = { "README.md", "README.txt" };

        /// <summary>
        /// 删掉搬空了的目录。旧骨架里只剩说明文件的子目录也一并清掉。
        /// </summary>
        private static void RemoveEmptyDirectories(string root, bool includeRoot)
        {
            try
            {
                if (!Directory.Exists(root))
                    return;

                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                {
                    // 插件数据目录即使暂时是空的也要留着，设置保存要用
                    if (string.Equals(dir.TrimEnd('\\', '/'), DataPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        continue;

                    var entries = Directory.EnumerateFileSystemEntries(dir).ToList();
                    if (entries.Count == 1 && SkeletonFiles.Contains(Path.GetFileName(entries[0]), StringComparer.OrdinalIgnoreCase))
                    {
                        File.Delete(entries[0]);
                        entries.Clear();
                    }
                    if (entries.Count == 0)
                        Directory.Delete(dir);
                }

                if (includeRoot && !Directory.EnumerateFileSystemEntries(root).Any())
                    Directory.Delete(root);
            }
            catch
            {
                // 清不掉空目录不影响功能
            }
        }
    }
}
