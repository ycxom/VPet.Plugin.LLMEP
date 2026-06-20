using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace VPet.Plugin.LLMEP
{
    public partial class ImageUI : UserControl
    {
        // 存储原始图片数据和类型信息
        private byte[] _imageData;
        private bool _isGif;

        public ImageUI()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;

            // 尺寸已在 XAML 的 Border 中设置（MaxWidth/MaxHeight = 200）
            // 相对于 VPet 的大小，200 像素是一个合适的聊天气泡尺寸
        }

        public ImageUI(ImageMgr mgr) : this()
        {
            // Insert into Main.UIGrid at second-to-last position
            var uiGrid = mgr.MW.Main.UIGrid;
            var insertIndex = uiGrid.Children.Count > 0 ? uiGrid.Children.Count - 1 : 0;
            uiGrid.Children.Insert(insertIndex, this);
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
            Visibility = Visibility.Collapsed;
        }
    }
}