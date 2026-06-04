using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CFRezManager;

public partial class ImagePreviewWindow : Window
{
    private const double WindowWorkAreaMargin = 80;

    private string _imageName = string.Empty;
    private string? _imageInfo;
    private IReadOnlyList<ImagePreviewFrame> _frames = Array.Empty<ImagePreviewFrame>();
    private readonly DispatcherTimer _animationTimer = new();
    private readonly Func<int, Task<ImagePreviewDocument?>>? _loadDocumentAsync;
    private int _documentIndex;
    private int _documentCount = 1;
    private int _currentFrameIndex;
    private bool _hasAnimation;
    private bool _isDocumentLoading;
    private bool _isPlaying;
    private bool _isUpdatingFrameSelector;

    public ImagePreviewWindow(string imageName, ImageSource imageSource, string? imageInfo = null)
        : this(new ImagePreviewDocument(imageName, new[] { new ImagePreviewFrame("Original", imageSource) }, imageInfo))
    {
    }

    public ImagePreviewWindow(string imageName, IReadOnlyList<ImagePreviewFrame> frames, string? imageInfo = null)
        : this(new ImagePreviewDocument(imageName, frames, imageInfo))
    {
    }

    public ImagePreviewWindow(
        string imageName,
        IReadOnlyList<ImagePreviewFrame> frames,
        string? imageInfo,
        double? animationFrameRate)
        : this(new ImagePreviewDocument(imageName, frames, imageInfo, animationFrameRate))
    {
    }

    public ImagePreviewWindow(
        ImagePreviewDocument document,
        Func<int, Task<ImagePreviewDocument?>>? loadDocumentAsync = null,
        int documentIndex = 0,
        int documentCount = 1)
    {
        if (document.Frames.Count == 0)
        {
            throw new ArgumentException("At least one preview frame is required.", nameof(document));
        }

        InitializeComponent();

        _loadDocumentAsync = loadDocumentAsync;
        _documentCount = Math.Max(1, documentCount);
        _documentIndex = Math.Clamp(documentIndex, 0, _documentCount - 1);
        _animationTimer.Tick += AnimationTimer_Tick;
        LocalizedText.LanguageChanged += LocalizedText_LanguageChanged;
        ApplyLanguage();

        SetDocument(document);
        SetInitialWindowSize();
    }

    private void FrameSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUpdatingFrameSelector && FrameSelector.SelectedIndex >= 0)
        {
            SetFrame(FrameSelector.SelectedIndex);
        }
    }

    private async void PreviousImageButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateDocumentAsync(-1);
    }

    private async void NextImageButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateDocumentAsync(1);
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasAnimation)
        {
            return;
        }

        _isPlaying = !_isPlaying;
        UpdatePlayPauseButtonText();
        if (_isPlaying)
        {
            _animationTimer.Start();
        }
        else
        {
            _animationTimer.Stop();
        }
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        int nextIndex = (_currentFrameIndex + 1) % _frames.Count;
        if (FrameSelector.Visibility == Visibility.Visible)
        {
            FrameSelector.SelectedIndex = nextIndex;
        }
        else
        {
            SetFrame(nextIndex);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _animationTimer.Stop();
        LocalizedText.LanguageChanged -= LocalizedText_LanguageChanged;
        base.OnClosed(e);
    }

    private void LocalizedText_LanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyLanguage();
            return;
        }

        Dispatcher.Invoke(ApplyLanguage);
    }

    private void ApplyLanguage()
    {
        PreviousImageButton.Content = LocalizedText.T("PreviewPrevious");
        NextImageButton.Content = LocalizedText.T("PreviewNext");
        UpdatePlayPauseButtonText();
        FrameSelector.Items.Refresh();
        if (_frames.Count > 0)
        {
            SetFrame(_currentFrameIndex);
        }
    }

    private void UpdatePlayPauseButtonText()
    {
        PlayPauseButton.Content = LocalizedText.T(_isPlaying ? "PreviewPause" : "PreviewPlay");
    }

    protected override async void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Left && PreviousImageButton.IsEnabled)
        {
            e.Handled = true;
            await NavigateDocumentAsync(-1);
            return;
        }

        if (e.Key == Key.Right && NextImageButton.IsEnabled)
        {
            e.Handled = true;
            await NavigateDocumentAsync(1);
            return;
        }

        base.OnKeyDown(e);
    }

    private async Task NavigateDocumentAsync(int delta)
    {
        if (!HasDocumentNavigation ||
            _loadDocumentAsync is null ||
            _isDocumentLoading)
        {
            return;
        }

        int targetIndex = _documentIndex + delta;
        if (targetIndex < 0 || targetIndex >= _documentCount)
        {
            return;
        }

        _isDocumentLoading = true;
        UpdateNavigationState();
        try
        {
            ImagePreviewDocument? document = await _loadDocumentAsync(targetIndex);
            if (document is null || document.Frames.Count == 0)
            {
                return;
            }

            _documentIndex = targetIndex;
            SetDocument(document);
            PreviewScrollViewer.ScrollToHome();
        }
        finally
        {
            _isDocumentLoading = false;
            UpdateNavigationState();
        }
    }

    private void SetDocument(ImagePreviewDocument document)
    {
        _imageName = document.ImageName;
        _imageInfo = document.ImageInfo;
        _frames = document.Frames;
        _currentFrameIndex = 0;
        ConfigureAnimation(document);

        _isUpdatingFrameSelector = true;
        FrameSelector.ItemsSource = null;
        FrameSelector.Visibility = _frames.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_frames.Count > 1)
        {
            FrameSelector.ItemsSource = _frames;
            FrameSelector.SelectedIndex = 0;
        }

        _isUpdatingFrameSelector = false;
        SetFrame(0);
        UpdateNavigationState();
    }

    private void ConfigureAnimation(ImagePreviewDocument document)
    {
        _animationTimer.Stop();
        _hasAnimation = document.Frames.Count > 1 && document.AnimationFrameRate is > 0;
        PlayPauseButton.Visibility = _hasAnimation ? Visibility.Visible : Visibility.Collapsed;
        if (!_hasAnimation)
        {
            _isPlaying = false;
            UpdatePlayPauseButtonText();
            return;
        }

        double frameRate = Math.Clamp(document.AnimationFrameRate.GetValueOrDefault(), 1.0, 60.0);
        _animationTimer.Interval = TimeSpan.FromSeconds(1.0 / frameRate);
        _isPlaying = true;
        UpdatePlayPauseButtonText();
        _animationTimer.Start();
    }

    private void SetFrame(int index)
    {
        if (index < 0 || index >= _frames.Count)
        {
            return;
        }

        _currentFrameIndex = index;
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

        string frameName = frame.LocalizedName;
        string? frameInfo = _frames.Count > 1
            ? dimensions is null ? frameName : $"{frameName} - {dimensions}"
            : null;
        string? documentInfo = HasDocumentNavigation ? $"{_documentIndex + 1:N0} / {_documentCount:N0}" : null;
        string? infoText = CombineInfo(CombineInfo(documentInfo, _imageInfo), frameInfo);
        PreviewInfoText.Text = string.IsNullOrWhiteSpace(infoText) ? string.Empty : infoText;
        UpdateInfoBarVisibility();

        string titleFrame = dimensions is null ? frameName : $"{frameName} ({dimensions})";
        Title = _frames.Count > 1
            ? $"{_imageName} - {titleFrame}"
            : string.IsNullOrWhiteSpace(dimensions) ? _imageName : $"{_imageName} ({dimensions})";
        if (HasDocumentNavigation)
        {
            Title = $"{_documentIndex + 1:N0} / {_documentCount:N0} - {Title}";
        }

        if (!string.IsNullOrWhiteSpace(_imageInfo))
        {
            Title += $" - {_imageInfo}";
        }
    }

    private bool HasDocumentNavigation => _loadDocumentAsync is not null && _documentCount > 1;

    private void UpdateNavigationState()
    {
        Visibility visibility = HasDocumentNavigation ? Visibility.Visible : Visibility.Collapsed;
        PreviousImageButton.Visibility = visibility;
        NextImageButton.Visibility = visibility;
        PreviousImageButton.IsEnabled = HasDocumentNavigation && !_isDocumentLoading && _documentIndex > 0;
        NextImageButton.IsEnabled = HasDocumentNavigation && !_isDocumentLoading && _documentIndex < _documentCount - 1;
        UpdateInfoBarVisibility();
    }

    private void UpdateInfoBarVisibility()
    {
        PreviewInfoBar.Visibility = PreviewInfoText.Text.Length > 0 ||
                                    _frames.Count > 1 ||
                                    HasDocumentNavigation ||
                                    _hasAnimation
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetInitialWindowSize()
    {
        Rect workArea = SystemParameters.WorkArea;
        double maxWidth = Math.Max(MinWidth, workArea.Width - WindowWorkAreaMargin);
        double maxHeight = Math.Max(MinHeight, workArea.Height - WindowWorkAreaMargin);

        MaxWidth = maxWidth;
        MaxHeight = maxHeight;

        PreviewRoot.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double desiredWidth = PreviewRoot.DesiredSize.Width + GetWindowChromeWidth();
        double desiredHeight = PreviewRoot.DesiredSize.Height + GetWindowChromeHeight();

        Width = Math.Clamp(Math.Ceiling(desiredWidth), MinWidth, maxWidth);
        Height = Math.Clamp(Math.Ceiling(desiredHeight), MinHeight, maxHeight);
    }

    private static double GetWindowChromeWidth()
    {
        return SystemParameters.ResizeFrameVerticalBorderWidth * 2;
    }

    private static double GetWindowChromeHeight()
    {
        return SystemParameters.CaptionHeight + (SystemParameters.ResizeFrameHorizontalBorderHeight * 2);
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
