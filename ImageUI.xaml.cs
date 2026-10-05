using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using VPet.Plugin.LLMEP.Services;

namespace VPet.Plugin.LLMEP
{
    public partial class ImageUI : UserControl
    {
        /// <summary>
        /// 图片区域的最大边长（DIP）：气泡 MaxWidth/MaxHeight 200 减去两侧 8 的内边距。
        /// </summary>
        private const double MaxImageDip = 184;

        private readonly ImageMgr _mgr;

        // 存储原始图片数据和类型信息
        private byte[] _imageData;
        private bool _isGif;

        public ImageUI()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;

            // 尺寸已在 XAML 的 Grid 中设置（MaxWidth/MaxHeight = 200）
            // 相对于 VPet 的大小，200 像素是一个合适的聊天气泡尺寸
        }

        public ImageUI(ImageMgr mgr) : this()
        {
            _mgr = mgr;

            // Insert into Main.UIGrid at second-to-last position
            var uiGrid = mgr.MW.Main.UIGrid;
            var insertIndex = uiGrid.Children.Count > 0 ? uiGrid.Children.Count - 1 : 0;
            uiGrid.Children.Insert(insertIndex, this);
        }

        /// <summary>
        /// 解码时该用的长边像素数：图片区域在屏幕上实际占多少物理像素就解多大。
        /// UIGrid 在 Viewbox 里（500 单位宽），再乘上系统 DPI。必须在 UI 线程调用。
        /// </summary>
        public int GetDecodePixelSize()
        {
            double scale = 2; // 量不到时按 2 倍兜底，宁可多解一点也别糊
            try
            {
                var source = PresentationSource.FromVisual(this);
                if (source?.RootVisual is Visual root && source.CompositionTarget != null)
                {
                    var transform = TransformToAncestor(root);
                    var origin = transform.Transform(new Point(0, 0));
                    var unit = transform.Transform(new Point(100, 0));
                    double layoutScale = (unit - origin).Length / 100;
                    double measured = layoutScale * source.CompositionTarget.TransformToDevice.M11;
                    if (measured > 0 && !double.IsNaN(measured) && !double.IsInfinity(measured))
                        scale = measured;
                }
            }
            catch (InvalidOperationException)
            {
                // 还没进视觉树
            }

            return (int)Math.Clamp(Math.Ceiling(MaxImageDip * scale), 96, 1024);
        }

        /// <summary>
        /// 显示一张已解码的表情包。帧都已冻结并缩好，这里只挂关键帧动画，开销与帧数无关。
        /// </summary>
        public void ShowSticker(DecodedSticker sticker)
        {
            StopAnimation();
            SetImageData(sticker.RawBytes, sticker.IsGif);

            Image.Source = sticker.Frames[0];
            if (sticker.IsAnimated)
            {
                var animation = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                var keyTime = TimeSpan.Zero;
                for (int i = 0; i < sticker.Frames.Length; i++)
                {
                    animation.KeyFrames.Add(new DiscreteObjectKeyFrame(sticker.Frames[i], KeyTime.FromTimeSpan(keyTime)));
                    keyTime += sticker.Delays[i];
                }
                animation.Duration = new Duration(keyTime);
                animation.Freeze();
                Image.BeginAnimation(System.Windows.Controls.Image.SourceProperty, animation);
            }

            Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 隐藏并释放当前表情包的所有帧引用。
        /// </summary>
        public void ClearSticker()
        {
            Visibility = Visibility.Collapsed;
            StopAnimation();
            Image.Source = null;
            _imageData = null;
        }

        private void StopAnimation()
        {
            Image.BeginAnimation(System.Windows.Controls.Image.SourceProperty, null);
        }

        /// <summary>
        /// 设置图片数据和类型信息
        /// </summary>
        public void SetImageData(byte[] imageData, bool isGif)
        {
            _imageData = imageData;
            _isGif = isGif;
        }

        private void CopyImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_imageData != null && _imageData.Length > 0)
                {
                    CopyImageDataToClipboard(_imageData);
                }
                else if (this.Image.Source is BitmapSource bitmap)
                {
                    CopyBitmapToClipboard(bitmap);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"复制失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyImageDataToClipboard(byte[] imageData)
        {
            string tempFilePath = Path.Combine(Path.GetTempPath(), $"clipboard_{System.Guid.NewGuid()}.{(_isGif ? "gif" : "png")}");

            try
            {
                File.WriteAllBytes(tempFilePath, imageData);

                var dataObject = new System.Windows.DataObject();
                var stringCollection = new System.Collections.Specialized.StringCollection { tempFilePath };
                dataObject.SetFileDropList(stringCollection);
                System.Windows.Clipboard.SetDataObject(dataObject, false);
            }
            catch (System.Exception ex)
            {
                throw new System.Exception($"复制图片文件失败: {ex.Message}");
            }
        }

        private void CopyBitmapToClipboard(BitmapSource bitmap)
        {
            var dataObject = new System.Windows.DataObject();
            dataObject.SetData(System.Windows.DataFormats.Bitmap, bitmap);
            System.Windows.Clipboard.SetDataObject(dataObject);
        }

        private void DownloadImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool hasData = _imageData != null && _imageData.Length > 0;
                bool hasBitmap = this.Image.Source is BitmapSource;
                if (!hasData && !hasBitmap)
                    return;

                string ext = _isGif ? "gif" : "png";
                string filter = _isGif ? "GIF 图片 (*.gif)|*.gif" : "PNG 图片 (*.png)|*.png";

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = $"image_{System.DateTime.Now:yyyyMMdd_HHmmss}",
                    Filter = filter,
                    InitialDirectory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures)
                };

                if (dialog.ShowDialog() != true)
                    return;

                if (hasData)
                {
                    File.WriteAllBytes(dialog.FileName, _imageData);
                }
                else if (this.Image.Source is BitmapSource bitmap)
                {
                    using var fs = new FileStream(dialog.FileName, FileMode.Create);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(fs);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"下载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            // 走管理器：顺带作废它的自动隐藏计时，免得旧计时器之后误关下一张
            if (_mgr != null)
                _mgr.HideCurrentSticker();
            else
                ClearSticker();
        }
    }
}