using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

public partial class ImagePreviewWindow : Window
{
    private readonly string _imageName;
    private readonly string? _imageInfo;
    private readonly IReadOnlyList<ImagePreviewFrame> _frames;

    public ImagePreviewWindow(string imageName, ImageSource imageSource, string? imageInfo = null)
        : this(imageName, new[] { new ImagePreviewFrame("Original", imageSource) }, imageInfo)
    {
    }

    public ImagePreviewWindow(string imageName, IReadOnlyList<ImagePreviewFrame> frames, string? imageInfo = null)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one preview frame is required.", nameof(frames));
        }

        _imageName = imageName;
        _imageInfo = imageInfo;
        _frames = frames;

        InitializeComponent();

        Rect workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(MinWidth, workArea.Width - 80);
        MaxHeight = Math.Max(MinHeight, workArea.Height - 80);

        if (frames.Count > 1)
        {
            FrameSelector.ItemsSource = frames;
            FrameSelector.Visibility = Visibility.Visible;
            PreviewInfoBar.Visibility = Visibility.Visible;
        }

        SetFrame(0);
        if (frames.Count > 1)
        {
            FrameSelector.SelectedIndex = 0;
        }
    }

    private void FrameSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FrameSelector.SelectedIndex >= 0)
        {
            SetFrame(FrameSelector.SelectedIndex);
        }
    }

    private void SetFrame(int index)
    {
        if (index < 0 || index >= _frames.Count)
        {
            return;
        }

        ImagePreviewFrame frame = _frames[index];
        PreviewImage.Source = frame.Source;
        string? dimensions = GetDimensions(frame.Source);
        if (frame.Source is BitmapSource bitmap)
        {
            PreviewImage.Width = bitmap.PixelWidth;
            PreviewImage.Height = bitmap.PixelHeight;
        }
        else
        {
            PreviewImage.ClearValue(WidthProperty);
            PreviewImage.ClearValue(HeightProperty);
        }

        string? frameInfo = _frames.Count > 1
            ? dimensions is null ? frame.Name : $"{frame.Name} - {dimensions}"
            : null;
        string? infoText = CombineInfo(_imageInfo, frameInfo);
        if (string.IsNullOrWhiteSpace(infoText))
        {
            PreviewInfoText.Text = string.Empty;
            PreviewInfoBar.Visibility = _frames.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            PreviewInfoText.Text = infoText;
            PreviewInfoBar.Visibility = Visibility.Visible;
        }

        string titleFrame = dimensions is null ? frame.Name : $"{frame.Name} ({dimensions})";
        Title = _frames.Count > 1
            ? $"{_imageName} - {titleFrame}"
            : string.IsNullOrWhiteSpace(dimensions) ? _imageName : $"{_imageName} ({dimensions})";
        if (!string.IsNullOrWhiteSpace(_imageInfo))
        {
            Title += $" - {_imageInfo}";
        }
    }

    private static string? GetDimensions(ImageSource source)
    {
        return source is BitmapSource bitmap ? $"{bitmap.PixelWidth} x {bitmap.PixelHeight}" : null;
    }

    private static string? CombineInfo(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return string.IsNullOrWhiteSpace(right) ? null : right;
        }

        return string.IsNullOrWhiteSpace(right) ? left : $"{left} | {right}";
    }
}
