using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

public partial class ImagePreviewWindow : Window
{
    public ImagePreviewWindow(string imageName, ImageSource imageSource)
    {
        InitializeComponent();

        Rect workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(MinWidth, workArea.Width - 80);
        MaxHeight = Math.Max(MinHeight, workArea.Height - 80);

        PreviewImage.Source = imageSource;
        if (imageSource is BitmapSource bitmap)
        {
            PreviewImage.Width = bitmap.PixelWidth;
            PreviewImage.Height = bitmap.PixelHeight;
            Title = $"{imageName} ({bitmap.PixelWidth} x {bitmap.PixelHeight})";
        }
        else
        {
            Title = imageName;
        }
    }
}
