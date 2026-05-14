using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

public sealed record ImagePreviewDocument(
    string ImageName,
    IReadOnlyList<ImagePreviewFrame> Frames,
    string? ImageInfo = null,
    double? AnimationFrameRate = null);

public sealed record ImagePreviewFrame(string Name, ImageSource Source)
{
    private const string FramePrefix = "Frame ";

    public string LocalizedName
    {
        get
        {
            return Name.StartsWith(FramePrefix, StringComparison.Ordinal)
                ? $"{LocalizedText.T("PreviewFrame")} {Name[FramePrefix.Length..]}"
                : Name;
        }
    }

    public string DisplayName
    {
        get
        {
            return Source is BitmapSource bitmap
                ? $"{LocalizedName}  {bitmap.PixelWidth} x {bitmap.PixelHeight}"
                : LocalizedName;
        }
    }
}
