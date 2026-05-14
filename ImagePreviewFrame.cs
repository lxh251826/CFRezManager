using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

public sealed record ImagePreviewFrame(string Name, ImageSource Source)
{
    public string DisplayName
    {
        get
        {
            return Source is BitmapSource bitmap
                ? $"{Name}  {bitmap.PixelWidth} x {bitmap.PixelHeight}"
                : Name;
        }
    }
}
