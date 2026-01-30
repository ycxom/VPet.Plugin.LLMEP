using System.Windows;
using System.Windows.Controls;

namespace VPet.Plugin.Image
{
    public partial class ImageUI : UserControl
    {
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
    }
}