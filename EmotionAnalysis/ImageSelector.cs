using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VPet.Plugin.LLMEP.Utils;

namespace VPet.Plugin.LLMEP.EmotionAnalysis
{
    /// <summary>
    /// 图片选择器 - 根据情感选择并显示图片
    /// </summary>
    public class ImageSelector
    {
        private readonly ImageMgr _imageMgr;
        private readonly IVectorRetriever _vectorRetriever;
        private readonly Random _random;
        private readonly Dictionary<string, string> _imagePathCache; // filename -> full path

        public ImageSelector(ImageMgr imageMgr, IVectorRetriever vectorRetriever)
        {
            _imageMgr = imageMgr;
            _vectorRetriever = vectorRetriever;
            _random = new Random();
            _imagePathCache = new Dictionary<string, string>();
        }

        /// <summary>
        /// 构建图片路径缓存
        /// </summary>
        public void BuildImagePathCache()
        {
            try
            {
                Utils.Logger.Debug("ImageSelector", "开始构建图片路径缓存");
                _imagePathCache.Clear();
                string dllPath = _imageMgr.LoaddllPath();

                // 扫描VPet_Expression目录
                string builtInPath = Path.Combine(dllPath, "VPet_Expression");
                if (Directory.Exists(builtInPath))
                {
                    Utils.Logger.Debug("ImageSelector", $"扫描内置表情包目录: {builtInPath}");
                    ScanDirectory(builtInPath);
                }
                else
                {
                    Utils.Logger.Warning("ImageSelector", $"内置表情包目录不存在: {builtInPath}");
                }

                // 扫描DIY表情包目录（文档\VPetLLM\Emotion）
                string diyPath = DiyStickerStorage.RootPath;
                if (Directory.Exists(diyPath))
                {
                    Utils.Logger.Debug("ImageSelector", $"扫描DIY表情包目录: {diyPath}");
                    ScanDirectory(diyPath);
                }
                else
                {
                    Utils.Logger.Debug("ImageSelector", $"DIY表情包目录不存在: {diyPath}");
                }

                Utils.Logger.Info("ImageSelector", $"图片路径缓存构建完成，共 {_imagePathCache.Count} 张图片");
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("ImageSelector", $"构建缓存失败: {ex.Message}");
            }
        }

        private void ScanDirectory(string directory)
        {
            try
            {
                string[] supportedFormats = { "*.png", "*.jpg", "*.gif", "*.jpeg" };
                int foundCount = 0;

                foreach (string format in supportedFormats)
                {
                    var files = Directory.GetFiles(directory, format, SearchOption.AllDirectories);
                    foreach (var filePath in files)
                    {
                        string filename = Path.GetFileName(filePath);
                        if (!_imagePathCache.ContainsKey(filename))
                        {
                            _imagePathCache[filename] = filePath;
                            foundCount++;
                        }
                    }
                }

                Utils.Logger.Debug("ImageSelector", $"在 {directory} 中找到 {foundCount} 张图片");
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("ImageSelector", $"扫描目录失败 {directory}: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据情感选择并显示图片（支持精确标签匹配）
        /// </summary>
        public async Task SelectAndDisplayWithEmotionAnalysisAsync(IEmotionAnalyzer emotionAnalyzer, string text)
        {
            try
            {
                Utils.Logger.Debug("ImageSelector", "=== 开始情感分析和图片选择 ===");
                Utils.Logger.Debug("ImageSelector", $"分析文本: {text.Substring(0, Math.Min(text.Length, 100))}...");

                // 检查情感分析器是否为空
                if (emotionAnalyzer == null)
                {
                    Utils.Logger.Error("ImageSelector", "错误 - 情感分析器为空");
                    return;
                }

                Utils.Logger.Debug("ImageSelector", "准备调用情感分析器...");

                // 使用情感分析器获取匹配的图片
                var selectedImage = await emotionAnalyzer.AnalyzeEmotionAndGetImagePathAsync(text);

                Utils.Logger.Debug("ImageSelector", "情感分析器调用完成");

                if (selectedImage != null)
                {
                    Utils.Logger.Info("ImageSelector", "情感分析成功获得匹配图片");
                    await DisplayImageAsync(selectedImage);
                }
                else
                {
                    Utils.Logger.Debug("ImageSelector", "情感分析未获得匹配图片，使用降级策略");

                    // 降级：使用当前心情的随机图片（在线表情包已显示时，自动触发会被忙碌检查挡掉，不会再叠一张）
                    selectedImage = _imageMgr.GetCurrentMoodImagePath();

                    if (selectedImage != null)
                    {
                        Utils.Logger.Debug("ImageSelector", "降级策略成功");
                        await DisplayImageAsync(selectedImage);
                    }
                    else
                    {
                        Utils.Logger.Warning("ImageSelector", "降级策略失败，无图片可显示");
                    }
                }

                Utils.Logger.Debug("ImageSelector", "=== 情感分析和图片选择完成 ===");
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("ImageSelector", $"情感分析和图片选择失败: {ex.Message}");
                Utils.Logger.Debug("ImageSelector", $"错误堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 根据情感选择并显示图片
        /// </summary>
        public async Task SelectAndDisplayAsync(List<string> emotions)
        {
            try
            {
                Utils.Logger.Debug("ImageSelector", "=== 开始选择和显示图片 ===");
                Utils.Logger.Debug("ImageSelector", $"处理情感列表: {string.Join(", ", emotions)}");

                // 使用向量检索获取匹配的图片
                Utils.Logger.Debug("ImageSelector", "调用向量检索器查找匹配图片");
                var matchingImages = await _vectorRetriever.FindMatchingImagesAsync(emotions, topK: 3);

                string selectedImage = null;

                if (matchingImages != null && matchingImages.Count > 0)
                {
                    Utils.Logger.Debug("ImageSelector", $"找到 {matchingImages.Count} 张匹配的图片");

                    // 从Top 3中随机选择一张
                    string selectedFilename = matchingImages[_random.Next(matchingImages.Count)];
                    Utils.Logger.Debug("ImageSelector", $"随机选择图片: {selectedFilename}");

                    if (_imagePathCache.TryGetValue(selectedFilename, out string imagePath))
                    {
                        Utils.Logger.Debug("ImageSelector", $"找到图片路径: {imagePath}");
                        selectedImage = imagePath;
                    }
                    else
                    {
                        Utils.Logger.Warning("ImageSelector", $"在缓存中未找到图片路径: {selectedFilename}");
                    }
                }
                else
                {
                    Utils.Logger.Debug("ImageSelector", "向量检索未找到匹配的图片");
                }

                // 降级：如果没有匹配的图片，使用当前心情的随机图片
                if (selectedImage == null)
                {
                    Utils.Logger.Debug("ImageSelector", "使用降级策略，选择当前心情的随机图片");

                    // 直接调用 ImageMgr 的公共方法获取当前心情的图片
                    selectedImage = _imageMgr.GetCurrentMoodImagePath();

                    if (selectedImage != null)
                    {
                        Utils.Logger.Debug("ImageSelector", "降级策略成功，获得当前心情图片");
                    }
                    else
                    {
                        Utils.Logger.Warning("ImageSelector", "降级策略失败，当前心情没有可用图片");
                        Utils.Logger.Debug("ImageSelector", "=== 处理完成（无图片显示）===");
                        return;
                    }
                }

                // 显示选中的图片
                if (selectedImage != null)
                {
                    Utils.Logger.Debug("ImageSelector", "开始显示选中的图片");
                    await DisplayImageAsync(selectedImage);
                    Utils.Logger.Debug("ImageSelector", "图片显示完成");
                }

                Utils.Logger.Debug("ImageSelector", "=== 处理完成 ===");
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("ImageSelector", $"选择图片失败: {ex.Message}");
                Utils.Logger.Debug("ImageSelector", $"错误堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 显示图片。情感分析是自动触发：已有表情包在显示就放弃，不打断它。
        /// 解码在 ImageMgr 里后台完成，到点自动隐藏，这里不再阻塞等待。
        /// </summary>
        private async Task DisplayImageAsync(string imagePath)
        {
            try
            {
                Utils.Logger.Debug("ImageSelector", $"开始显示图片: {imagePath}");
                bool shown = await _imageMgr.ShowStickerFileAsync(
                    imagePath, _imageMgr.Settings.GetDisplayDurationMs(), interrupt: false, "情感分析");
                Utils.Logger.Debug("ImageSelector", shown ? "图片已显示" : "图片未显示（已有表情包或解码失败）");
            }
            catch (Exception ex)
            {
                Utils.Logger.Error("ImageSelector", $"显示图片失败: {ex.Message}");
                Utils.Logger.Debug("ImageSelector", $"显示错误堆栈: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 记录日志
        /// </summary>
        private void LogMessage(string message)
        {
            _imageMgr?.LogMessage(message);
        }
    }
}