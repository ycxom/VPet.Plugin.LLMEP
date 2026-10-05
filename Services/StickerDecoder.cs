#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace VPet.Plugin.LLMEP.Services
{
    /// <summary>
    /// 解码完成、可以直接挂到界面上的表情包：每帧都已缩到显示尺寸并 Freeze。
    /// </summary>
    public sealed class DecodedSticker
    {
        public DecodedSticker(BitmapSource[] frames, TimeSpan[] delays, byte[] rawBytes, bool isGif)
        {
            Frames = frames;
            Delays = delays;
            RawBytes = rawBytes;
            IsGif = isGif;
        }

        public BitmapSource[] Frames { get; }

        /// <summary>每帧的停留时长，与 <see cref="Frames"/> 一一对应。</summary>
        public TimeSpan[] Delays { get; }

        /// <summary>原始文件字节，留给右键菜单的复制/保存用。</summary>
        public byte[] RawBytes { get; }

        public bool IsGif { get; }

        public bool IsAnimated => Frames.Length > 1;
    }

    /// <summary>
    /// 在后台线程把表情包解码成显示尺寸的冻结帧。
    ///
    /// 以前是把 BitmapImage 直接交给 WpfAnimatedGif：它在 UI 线程上按**原尺寸**
    /// 解码并合成所有帧（1000×1000×22 帧 ≈ 84MB），而 VPet 11073 起宠物的每次换帧
    /// 和 Display 都要同步进 UI 线程，于是表情包一出来宠物就僵住，释放时的大对象 GC
    /// 再顿一次。这里用宿主自带的 SkiaSharp 在后台解码、按显示尺寸缩小，UI 线程
    /// 只剩"把冻结好的帧挂上去"这一件小事。
    /// </summary>
    public static class StickerDecoder
    {
        /// <summary>帧数上限，防止异常大的 GIF 把内存吃光；超出部分直接截断。</summary>
        private const int MaxFrames = 300;

        /// <summary>
        /// 浏览器的通行做法：GIF 帧延时写 0 或小于 20ms 时按 100ms 播放，
        /// 否则这类图会以显示器刷新率狂转。
        /// </summary>
        private static readonly TimeSpan MinFrameDelay = TimeSpan.FromMilliseconds(20);
        private static readonly TimeSpan DefaultFrameDelay = TimeSpan.FromMilliseconds(100);

        public static DecodedSticker DecodeFile(string path, int maxPixelSize)
        {
            return Decode(File.ReadAllBytes(path), maxPixelSize);
        }

        /// <param name="data">图片文件字节（PNG/JPG/GIF/WebP…）</param>
        /// <param name="maxPixelSize">长边的最大像素数，超出则等比缩小</param>
        public static DecodedSticker Decode(byte[] data, int maxPixelSize)
        {
            if (data == null || data.Length == 0)
                throw new InvalidDataException("图片数据为空");

            using var skData = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(skData)
                ?? throw new InvalidDataException("无法识别的图片格式");

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0)
                throw new InvalidDataException($"图片尺寸无效: {info.Width}x{info.Height}");

            double scale = Math.Min(1.0, Math.Max(1, maxPixelSize) / (double)Math.Max(info.Width, info.Height));
            int targetWidth = Math.Max(1, (int)Math.Round(info.Width * scale));
            int targetHeight = Math.Max(1, (int)Math.Round(info.Height * scale));

            var fullInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            bool isGif = codec.EncodedFormat == SKEncodedImageFormat.Gif;
            int frameCount = Math.Min(codec.FrameCount, MaxFrames);

            if (frameCount <= 1)
            {
                using var bitmap = new SKBitmap(fullInfo);
                EnsureDecoded(codec.GetPixels(fullInfo, bitmap.GetPixels()));
                var frame = ToFrozenBitmap(bitmap, info.Width, targetWidth, targetHeight);
                return new DecodedSticker(new[] { frame }, new[] { TimeSpan.Zero }, data, isGif);
            }

            return DecodeAnimation(codec, fullInfo, frameCount, targetWidth, targetHeight, data, isGif);
        }

        private static DecodedSticker DecodeAnimation(
            SKCodec codec, SKImageInfo fullInfo, int frameCount,
            int targetWidth, int targetHeight, byte[] data, bool isGif)
        {
            var frameInfos = codec.FrameInfo;

            // GIF 的帧多是"在前一帧上叠加"，所以要留着底图；但只留到最后一个用它的帧为止，
            // 否则全尺寸画布会和原来一样堆满内存。
            var lastUse = new int[frameCount];
            for (int i = 0; i < frameCount; i++)
                lastUse[i] = -1;
            for (int i = 0; i < frameCount; i++)
            {
                int required = frameInfos[i].RequiredFrame;
                if (required >= 0 && required < frameCount)
                    lastUse[required] = i;
            }

            var canvases = new SKBitmap?[frameCount];
            var frames = new List<BitmapSource>(frameCount);
            var delays = new List<TimeSpan>(frameCount);

            try
            {
                for (int i = 0; i < frameCount; i++)
                {
                    int required = frameInfos[i].RequiredFrame;
                    SKBitmap canvas;
                    if (required >= 0 && required < frameCount && canvases[required] != null)
                    {
                        canvas = canvases[required]!.Copy();
                    }
                    else
                    {
                        canvas = new SKBitmap(fullInfo);
                        canvas.Erase(SKColors.Transparent);
                        required = -1;
                    }

                    var result = codec.GetPixels(fullInfo, canvas.GetPixels(), new SKCodecOptions(i, required));
                    if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                    {
                        canvas.Dispose();
                        if (i == 0)
                            EnsureDecoded(result);
                        break; // 文件截断：保留已解出的帧照常播放
                    }

                    canvases[i] = canvas;
                    frames.Add(ToFrozenBitmap(canvas, fullInfo.Width, targetWidth, targetHeight));

                    var delay = TimeSpan.FromMilliseconds(frameInfos[i].Duration);
                    delays.Add(delay < MinFrameDelay ? DefaultFrameDelay : delay);

                    for (int k = 0; k <= i; k++)
                    {
                        if (canvases[k] != null && lastUse[k] <= i)
                        {
                            canvases[k]!.Dispose();
                            canvases[k] = null;
                        }
                    }
                }
            }
            finally
            {
                foreach (var canvas in canvases)
                    canvas?.Dispose();
            }

            return new DecodedSticker(frames.ToArray(), delays.ToArray(), data, isGif);
        }

        private static void EnsureDecoded(SKCodecResult result)
        {
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                throw new InvalidDataException($"图片解码失败: {result}");
        }

        /// <summary>
        /// 缩到目标尺寸并转成冻结的 WPF 位图。DPI 按缩放比例调整，
        /// 让它在 WPF 里的自然尺寸（DIP）和原图一致——界面上的大小不因解码缩小而变。
        /// </summary>
        private static BitmapSource ToFrozenBitmap(SKBitmap source, int originalWidth, int targetWidth, int targetHeight)
        {
            SKBitmap scaled = source;
            if (targetWidth != source.Width || targetHeight != source.Height)
            {
                var targetInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                scaled = source.Resize(targetInfo, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                    ?? throw new InvalidOperationException("图片缩放失败");
            }

            try
            {
                double dpi = 96.0 * targetWidth / originalWidth;
                var bitmap = BitmapSource.Create(
                    scaled.Width, scaled.Height, dpi, dpi, PixelFormats.Pbgra32, null,
                    scaled.GetPixels(), scaled.ByteCount, scaled.RowBytes);
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                if (!ReferenceEquals(scaled, source))
                    scaled.Dispose();
            }
        }
    }
}
