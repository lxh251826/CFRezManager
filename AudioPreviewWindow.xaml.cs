using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;

namespace CFRezManager;

public partial class AudioPreviewWindow : Window
{
    private sealed record AudioPreviewTrackItem(string Number, string Title);

    private sealed class TrackItemCollection : ObservableCollection<AudioPreviewTrackItem>
    {
        public TrackItemCollection(IEnumerable<AudioPreviewTrackItem> items)
            : base(items)
        {
        }

        public void AddRange(IEnumerable<AudioPreviewTrackItem> items)
        {
            CheckReentrancy();
            bool added = false;
            foreach (AudioPreviewTrackItem item in items)
            {
                Items.Add(item);
                added = true;
            }

            if (!added)
            {
                return;
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    private enum AudioRepeatMode
    {
        Directory,
        Single,
        StopAtEnd
    }

    private const string PlayIconData = "M 3 1 L 18 10 L 3 19 Z";
    private const string PauseIconData = "M 4 1 L 8 1 L 8 19 L 4 19 Z M 12 1 L 16 1 L 16 19 L 12 19 Z";
    private const string RepeatDirectoryIconData = "M 5 4 L 16 4 L 19 7 M 19 7 L 16 10 M 19 7 L 5 7 M 19 15 L 8 15 L 5 12 M 5 12 L 8 9 M 5 12 L 19 12";
    private const string RepeatStopAtEndIconData = "M 6 4 L 15 11 L 6 18 Z M 19 4 L 19 18";
    private const long SpectrumPeakHoldMilliseconds = 420;
    private const double SpectrumPeakInitialFallRowsPerSecond = 1.6;
    private const double SpectrumPeakGravityRowsPerSecondSquared = 32.0;
    private const double SpectrumPeakMaxFallRowsPerSecond = 18.0;
    private const int SpectrumCellWidthPixels = 5;
    private const int SpectrumCellHeightPixels = 1;
    private const int SpectrumCellGapXPixels = 1;
    private const int SpectrumCellGapYPixels = 2;
    private const int SpectrumMinRows = 8;
    private const int SpectrumMaxRows = 18;
    private const int SpectrumBackgroundColor = unchecked((int)0xFF111111);
    private const int SpectrumInactiveColor = unchecked((int)0xFF1A1A1A);
    private const int SpectrumLowColor = unchecked((int)0xFF0979E8);
    private const int SpectrumHighColor = unchecked((int)0xFF42B8FF);
    private const int SpectrumPeakColor = unchecked((int)0xFF78D0FF);

    private readonly DispatcherTimer _positionTimer;
    private Func<int, Task<AudioPreviewDocument?>>? _loadDocumentAsync;
    private TrackItemCollection _trackItems = new([]);
    private UserSettings _settings = new();
    private AudioPreviewDocument? _document;
    private float[] _waveformPeaks = [];
    private AudioSpectrumData? _spectrumData;
    private WriteableBitmap? _spectrumBitmap;
    private int[] _spectrumPixels = [];
    private double[] _spectrumPeakRows = [];
    private double[] _spectrumPeakFallVelocities = [];
    private long[] _spectrumPeakHoldUntilTicks = [];
    private long[] _spectrumPeakLastUpdateTicks = [];
    private double[] _spectrumDisplayRows = [];
    private double[] _spectrumTargetRows = [];
    private int[] _spectrumActiveRows = [];
    private double[] _spectrumHeldRows = [];
    private int _spectrumBitmapWidth;
    private int _spectrumBitmapHeight;
    private int _spectrumGridLeftPixels;
    private int _spectrumGridTopPixels;
    private int _spectrumColumnCount;
    private int _spectrumRowCount;
    private double _spectrumGain = 1;
    private TimeSpan _duration = TimeSpan.Zero;
    private int _documentIndex;
    private int _documentCount = 1;
    private int _waveformRequestVersion;
    private AudioRepeatMode _repeatMode = AudioRepeatMode.Directory;
    private bool _isNavigationLoading;
    private bool _isDocumentLoading;
    private bool _isDraggingPosition;
    private bool _isUpdatingPosition;
    private bool _isUpdatingTrackSelection;
    private bool _isPlaying;
    private bool _isSpectrumSettling;
    private bool _arePreferenceUpdatesEnabled;
    private TimeSpan _spectrumSettlePosition = TimeSpan.Zero;

    public AudioPreviewWindow(
        string fileName,
        string audioPath,
        string? audioInfo = null,
        string? temporaryAudioPath = null,
        UserSettings? settings = null)
        : this(new AudioPreviewDocument(
            fileName,
            audioPath,
            audioInfo,
            string.IsNullOrWhiteSpace(temporaryAudioPath) ? [] : [temporaryAudioPath]),
            settings: settings)
    {
    }

    public AudioPreviewWindow(
        AudioPreviewDocument document,
        Func<int, Task<AudioPreviewDocument?>>? loadDocumentAsync = null,
        int documentIndex = 0,
        int documentCount = 1,
        UserSettings? settings = null,
        IReadOnlyList<string>? documentNames = null)
    {
        InitializeComponent();

        _settings = settings ?? UserSettings.Load();
        _repeatMode = ParseAudioRepeatMode(_settings.AudioPreviewRepeatMode);
        VolumeSlider.Value = NormalizeVolume(_settings.AudioPreviewVolume);
        _arePreferenceUpdatesEnabled = true;

        _loadDocumentAsync = loadDocumentAsync;
        _documentCount = Math.Max(1, documentCount);
        _documentIndex = Math.Clamp(documentIndex, 0, _documentCount - 1);
        _trackItems = new TrackItemCollection(BuildTrackItems(documentNames, _documentCount));
        TrackListBox.ItemsSource = _trackItems;
        UpdateTrackListVisibility();
        UpdateRepeatModeButton();

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _positionTimer.Tick += PositionTimer_Tick;

        SetDocument(document, deletePrevious: false, startPlayback: false);
        Loaded += AudioPreviewWindow_Loaded;
    }

    public int DocumentCount => _documentCount;

    public void SetNavigationLoading(bool isLoading)
    {
        if (_isNavigationLoading != isLoading)
        {
            _isNavigationLoading = isLoading;
        }

        UpdateTrackListVisibility();
        UpdateNavigationState();
    }

    public void UpdateDocumentNavigation(
        Func<int, Task<AudioPreviewDocument?>>? loadDocumentAsync,
        int documentCount,
        IReadOnlyList<string>? documentNames = null)
    {
        _loadDocumentAsync = loadDocumentAsync;
        _documentCount = Math.Max(1, documentCount);
        _documentIndex = Math.Clamp(_documentIndex, 0, _documentCount - 1);
        _trackItems.Clear();
        _trackItems.AddRange(BuildTrackItems(documentNames, _documentCount));

        TrackListBox.Items.Refresh();
        RefreshDocumentText();
        UpdateTrackListVisibility();
        UpdateNavigationState();
        UpdateSelectedTrackListItem();
    }

    public void AppendDocumentNavigation(
        Func<int, Task<AudioPreviewDocument?>>? loadDocumentAsync,
        int documentCount,
        IReadOnlyList<string>? documentNames = null)
    {
        int targetCount = Math.Max(1, documentCount);
        if (targetCount <= _documentCount)
        {
            RefreshDocumentText();
            UpdateNavigationState();
            return;
        }

        _loadDocumentAsync = loadDocumentAsync;
        int oldCount = _documentCount;
        _documentCount = targetCount;
        _trackItems.AddRange(BuildTrackItems(documentNames, oldCount, _documentCount));

        RefreshDocumentText();
        UpdateTrackListVisibility();
        UpdateNavigationState();
        UpdateSelectedTrackListItem(scrollIntoView: false);
    }

    private void AudioPreviewWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= AudioPreviewWindow_Loaded;
        StartPlayback();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void PreviousAudioButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateDocumentAsync(-1);
    }

    private async void NextAudioButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateDocumentAsync(1);
    }

    private async void TrackListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTrackSelection ||
            TrackListBox.SelectedIndex < 0 ||
            TrackListBox.SelectedIndex == _documentIndex)
        {
            return;
        }

        if (!await NavigateDocumentIndexAsync(TrackListBox.SelectedIndex))
        {
            UpdateSelectedTrackListItem();
        }
    }

    private void RepeatModeButton_Click(object sender, RoutedEventArgs e)
    {
        _repeatMode = _repeatMode switch
        {
            AudioRepeatMode.Directory => AudioRepeatMode.Single,
            AudioRepeatMode.Single => AudioRepeatMode.StopAtEnd,
            _ => AudioRepeatMode.Directory
        };
        UpdateRepeatModeButton();
        SaveAudioPreferences();
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            PausePlayback();
            return;
        }

        StartPlayback();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _isSpectrumSettling = false;
        AudioPlayer.Stop();
        _positionTimer.Stop();
        _isPlaying = false;
        UpdatePlayButtonIcon();
        SetPosition(TimeSpan.Zero);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AudioPlayer is not null)
        {
            AudioPlayer.Volume = NormalizeVolume(VolumeSlider.Value);
        }

        if (_arePreferenceUpdatesEnabled)
        {
            SaveAudioPreferences();
        }
    }

    private void PositionSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSpectrumSettling = false;
        _isDraggingPosition = true;
    }

    private void PositionSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingPosition = false;
        SeekToSliderValue();
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUpdatingPosition && !_isDraggingPosition)
        {
            SeekToSliderValue();
        }
    }

    private void AudioPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        _duration = AudioPlayer.NaturalDuration.HasTimeSpan
            ? AudioPlayer.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        PositionSlider.Maximum = Math.Max(1, _duration.TotalSeconds);
        UpdateTimeText(AudioPlayer.Position);
    }

    private async void AudioPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_repeatMode == AudioRepeatMode.StopAtEnd)
        {
            SetPlaybackEndedState();
            return;
        }

        if (_repeatMode == AudioRepeatMode.Single)
        {
            RestartCurrentDocument();
            return;
        }

        if (await TryAdvanceDirectoryLoopAsync())
        {
            return;
        }

        RestartCurrentDocument();
    }

    private void SetPlaybackEndedState()
    {
        _spectrumSettlePosition = _duration > TimeSpan.Zero ? _duration : AudioPlayer.Position;
        AudioPlayer.Stop();
        _isPlaying = false;
        _isSpectrumSettling = true;
        UpdatePlayButtonIcon();
        SetPosition(TimeSpan.Zero, updateSpectrum: false);
        UpdateWaveformPlaybackVisual(_spectrumSettlePosition, forceSilence: true);
        _positionTimer.Start();
    }

    private void AudioPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _isSpectrumSettling = false;
        _positionTimer.Stop();
        _isPlaying = false;
        UpdatePlayButtonIcon();
        System.Windows.MessageBox.Show(
            e.ErrorException?.Message ?? "Cannot play this audio file.",
            "Audio preview failed",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_isSpectrumSettling)
        {
            UpdateWaveformPlaybackVisual(_spectrumSettlePosition, forceSilence: true);
            if (IsSpectrumSettled())
            {
                _isSpectrumSettling = false;
                _positionTimer.Stop();
            }

            return;
        }

        if (!_isDraggingPosition)
        {
            SetPosition(AudioPlayer.Position);
            return;
        }

        UpdateWaveformPlaybackVisual(TimeSpan.FromSeconds(Math.Clamp(PositionSlider.Value, 0, Math.Max(1, _duration.TotalSeconds))));
    }

    protected override async void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Left && PreviousAudioButton.IsEnabled)
        {
            e.Handled = true;
            await NavigateDocumentAsync(-1);
            return;
        }

        if (e.Key == Key.Right && NextAudioButton.IsEnabled)
        {
            e.Handled = true;
            await NavigateDocumentAsync(1);
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _positionTimer.Stop();
        QueueTemporaryAudioCleanup(_document);
        _document = null;
        base.OnClosed(e);
    }

    private Task<bool> NavigateDocumentAsync(int delta)
    {
        return NavigateDocumentIndexAsync(_documentIndex + delta);
    }

    private async Task<bool> NavigateDocumentIndexAsync(int targetIndex)
    {
        if (!HasDocumentNavigation || _loadDocumentAsync is null || _isDocumentLoading)
        {
            return false;
        }

        if (targetIndex < 0 || targetIndex >= _documentCount)
        {
            return false;
        }

        _isDocumentLoading = true;
        UpdateNavigationState();
        try
        {
            AudioPreviewDocument? document = await _loadDocumentAsync(targetIndex);
            if (document is null)
            {
                return false;
            }

            _documentIndex = targetIndex;
            SetDocument(document, deletePrevious: true, startPlayback: true);
            return true;
        }
        finally
        {
            _isDocumentLoading = false;
            UpdateNavigationState();
        }
    }

    private void SetDocument(AudioPreviewDocument document, bool deletePrevious, bool startPlayback)
    {
        AudioPlayer.Stop();
        _positionTimer.Stop();
        _isPlaying = false;
        _isSpectrumSettling = false;
        UpdatePlayButtonIcon();
        if (deletePrevious)
        {
            QueueTemporaryAudioCleanup(_document);
        }

        _document = document;
        _duration = TimeSpan.Zero;
        SetPosition(TimeSpan.Zero);

        FileNameText.Text = document.AudioName;
        NowPlayingText.Text = document.AudioName;
        FormatBadgeText.Text = document.FormatLabel;
        AudioPlayer.Source = new Uri(document.AudioPath, UriKind.Absolute);
        AudioPlayer.Volume = NormalizeVolume(VolumeSlider.Value);

        RefreshDocumentText();
        UpdateInfoBarVisibility();

        UpdateNavigationState();
        UpdateSelectedTrackListItem();
        QueueWaveformRender(document, ++_waveformRequestVersion);
        if (startPlayback)
        {
            StartPlayback();
        }
    }

    private void StartPlayback()
    {
        _isSpectrumSettling = false;
        AudioPlayer.Play();
        _isPlaying = true;
        UpdatePlayButtonIcon();
        _positionTimer.Start();
    }

    private void PausePlayback()
    {
        _isSpectrumSettling = false;
        AudioPlayer.Pause();
        _positionTimer.Stop();
        _isPlaying = false;
        UpdatePlayButtonIcon();
    }

    private void SeekToSliderValue()
    {
        if (_duration <= TimeSpan.Zero)
        {
            return;
        }

        _isSpectrumSettling = false;
        TimeSpan position = TimeSpan.FromSeconds(Math.Clamp(PositionSlider.Value, 0, _duration.TotalSeconds));
        AudioPlayer.Position = position;
        UpdateTimeText(position);
        UpdateWaveformPlaybackVisual(position);
    }

    private async Task<bool> TryAdvanceDirectoryLoopAsync()
    {
        if (!HasDocumentNavigation || _documentCount <= 1)
        {
            return false;
        }

        int targetIndex = _documentIndex + 1;
        if (targetIndex >= _documentCount)
        {
            targetIndex = 0;
        }

        return await NavigateDocumentIndexAsync(targetIndex);
    }

    private void RestartCurrentDocument()
    {
        _isSpectrumSettling = false;
        AudioPlayer.Stop();
        AudioPlayer.Position = TimeSpan.Zero;
        SetPosition(TimeSpan.Zero);
        AudioPlayer.Play();
        _isPlaying = true;
        UpdatePlayButtonIcon();
        _positionTimer.Start();
    }

    private void SetPosition(TimeSpan position, bool updateSpectrum = true)
    {
        _isUpdatingPosition = true;
        PositionSlider.Value = _duration > TimeSpan.Zero
            ? Math.Clamp(position.TotalSeconds, 0, _duration.TotalSeconds)
            : 0;
        _isUpdatingPosition = false;
        UpdateTimeText(position);
        if (updateSpectrum)
        {
            UpdateWaveformPlaybackVisual(position);
        }
    }

    private void UpdateTimeText(TimeSpan position)
    {
        ElapsedLargeText.Text = FormatShortTime(position);
        DurationText.Text = FormatShortTime(_duration);
        TimeText.Text = $"{FormatLongTime(position)} / {FormatLongTime(_duration)}";
    }

    private bool HasDocumentNavigation => _loadDocumentAsync is not null && _documentCount > 1;

    private void RefreshDocumentText()
    {
        if (_document is null)
        {
            return;
        }

        string? documentInfo = HasDocumentNavigation ? $"{_documentIndex + 1:N0} / {_documentCount:N0}" : null;
        PreviewInfoText.Text = CombineInfo(documentInfo, _document.AudioInfo) ?? string.Empty;
        Title = HasDocumentNavigation
            ? $"{_documentIndex + 1:N0} / {_documentCount:N0} - {_document.AudioName} - Audio Preview"
            : $"{_document.AudioName} - Audio Preview";
    }

    private static IReadOnlyList<AudioPreviewTrackItem> BuildTrackItems(IReadOnlyList<string>? documentNames, int documentCount)
    {
        return BuildTrackItems(documentNames, 0, documentCount);
    }

    private static IReadOnlyList<AudioPreviewTrackItem> BuildTrackItems(
        IReadOnlyList<string>? documentNames,
        int startIndex,
        int endIndex)
    {
        var items = new List<AudioPreviewTrackItem>(Math.Max(0, endIndex - startIndex));
        for (int index = startIndex; index < endIndex; index++)
        {
            items.Add(BuildTrackItem(index, documentNames));
        }

        return items;
    }

    private static AudioPreviewTrackItem BuildTrackItem(int index, IReadOnlyList<string>? documentNames)
    {
        string title = documentNames is not null &&
                       index >= 0 &&
                       index < documentNames.Count &&
                       !string.IsNullOrWhiteSpace(documentNames[index])
            ? documentNames[index]
            : $"Track {index + 1:N0}";
        return new AudioPreviewTrackItem(
            (index + 1).ToString("N0", CultureInfo.CurrentCulture),
            title);
    }

    private void UpdateTrackListVisibility()
    {
        bool showTrackList = HasDocumentNavigation && _trackItems.Count > 1;
        TrackListPanel.Visibility = showTrackList ? Visibility.Visible : Visibility.Collapsed;
        TrackListColumn.Width = showTrackList ? new GridLength(272) : new GridLength(0);
        TrackCountText.Text = showTrackList ? _documentCount.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
        TrackLoadingPanel.Visibility = showTrackList && _isNavigationLoading ? Visibility.Visible : Visibility.Collapsed;
        if (showTrackList)
        {
            MinWidth = Math.Max(MinWidth, 940);
            MinHeight = Math.Max(MinHeight, 330);
            Width = Math.Max(Width, 980);
            Height = Math.Max(Height, 420);
        }
    }

    private void UpdateSelectedTrackListItem(bool scrollIntoView = true)
    {
        if (!HasDocumentNavigation || _trackItems.Count == 0)
        {
            return;
        }

        _isUpdatingTrackSelection = true;
        try
        {
            TrackListBox.SelectedIndex = Math.Clamp(_documentIndex, 0, _trackItems.Count - 1);
            if (scrollIntoView && TrackListBox.SelectedItem is not null)
            {
                TrackListBox.ScrollIntoView(TrackListBox.SelectedItem);
            }
        }
        finally
        {
            _isUpdatingTrackSelection = false;
        }
    }

    private void UpdateRepeatModeButton()
    {
        RepeatOneBadge.Visibility = _repeatMode == AudioRepeatMode.Single ? Visibility.Visible : Visibility.Collapsed;
        RepeatModeIcon.Data = Geometry.Parse(_repeatMode == AudioRepeatMode.StopAtEnd
            ? RepeatStopAtEndIconData
            : RepeatDirectoryIconData);
        RepeatModeButton.ToolTip = _repeatMode switch
        {
            AudioRepeatMode.Single => "\u5355\u66f2\u5faa\u73af",
            AudioRepeatMode.StopAtEnd => "\u64ad\u653e\u5b8c\u6210\u505c\u6b62",
            _ => "\u76ee\u5f55\u5faa\u73af"
        };
    }

    private void SaveAudioPreferences()
    {
        _settings.AudioPreviewVolume = NormalizeVolume(VolumeSlider.Value);
        _settings.AudioPreviewRepeatMode = _repeatMode.ToString();
        _settings.Save();
    }

    private static AudioRepeatMode ParseAudioRepeatMode(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out AudioRepeatMode mode)
            ? mode
            : AudioRepeatMode.Directory;
    }

    private static double NormalizeVolume(double value)
    {
        return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.8;
    }

    private void UpdateNavigationState()
    {
        PreviousAudioButton.Visibility = Visibility.Visible;
        NextAudioButton.Visibility = Visibility.Visible;
        PreviousAudioButton.IsEnabled = HasDocumentNavigation && !_isDocumentLoading && _documentIndex > 0;
        NextAudioButton.IsEnabled = HasDocumentNavigation && !_isDocumentLoading && _documentIndex + 1 < _documentCount;
        UpdateInfoBarVisibility();
    }

    private void UpdateInfoBarVisibility()
    {
        PreviewInfoBar.Visibility = Visibility.Visible;
    }

    private static string? CombineInfo(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return string.IsNullOrWhiteSpace(right) ? null : right;
        }

        return string.IsNullOrWhiteSpace(right) ? left : $"{left} - {right}";
    }

    private void QueueWaveformRender(AudioPreviewDocument document, int requestVersion)
    {
        WaveformImage.Source = null;
        _spectrumBitmap = null;
        _spectrumPixels = [];
        _spectrumBitmapWidth = 0;
        _spectrumBitmapHeight = 0;
        _spectrumGridLeftPixels = 0;
        _spectrumGridTopPixels = 0;
        _spectrumColumnCount = 0;
        _spectrumRowCount = 0;
        _spectrumPeakRows = [];
        _spectrumPeakFallVelocities = [];
        _spectrumPeakHoldUntilTicks = [];
        _spectrumPeakLastUpdateTicks = [];
        _spectrumDisplayRows = [];
        _spectrumTargetRows = [];
        _spectrumActiveRows = [];
        _spectrumHeldRows = [];
        _waveformPeaks = [];
        _spectrumData = null;
        Dispatcher.BeginInvoke(() =>
        {
            if (requestVersion == _waveformRequestVersion)
            {
                RenderWaveform([], null);
            }
        });

        _ = Task.Run(() =>
        {
            try
            {
                if (AudioSpectrumAnalyzer.TryAnalyzeFilePreview(document.AudioPath, 128, 12, out AudioSpectrumData? previewSpectrumData))
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (requestVersion == _waveformRequestVersion)
                        {
                            ApplySpectrumData(previewSpectrumData);
                        }
                    });
                }

                AudioSpectrumAnalyzer.TryAnalyzeFile(document.AudioPath, 128, out AudioSpectrumData? spectrumData);
                Dispatcher.BeginInvoke(() =>
                {
                    if (requestVersion == _waveformRequestVersion)
                    {
                        ApplySpectrumData(spectrumData);
                    }
                });
            }
            catch
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (requestVersion == _waveformRequestVersion)
                    {
                        RenderWaveform([], null);
                    }
                });
            }
        });
    }

    private void ApplySpectrumData(AudioSpectrumData? spectrumData)
    {
        _spectrumData = spectrumData;
        ResetSpectrumMotionState();
        UpdateWaveformPlaybackVisual(AudioPlayer.Position);
    }

    private void RenderWaveform(float[] peaks, AudioSpectrumData? spectrumData)
    {
        _waveformPeaks = peaks;
        _spectrumData = spectrumData;
        _spectrumGain = CalculateSpectrumGain(peaks);
        CreateSpectrumBitmap();
        ResetSpectrumMotionState();
        DrawSpectrumFrame(null, null);
        UpdateWaveformPlaybackVisual(AudioPlayer.Position);
    }

    private void CreateSpectrumBitmap()
    {
        double width = GetWaveformWidth();
        double height = GetWaveformHeight();
        DpiScale dpi = VisualTreeHelper.GetDpi(WaveformImage);
        double dpiScaleX = Math.Max(0.1, dpi.DpiScaleX);
        double dpiScaleY = Math.Max(0.1, dpi.DpiScaleY);
        int pixelWidth = Math.Max(1, (int)Math.Round(width * dpiScaleX));
        int pixelHeight = Math.Max(1, (int)Math.Round(height * dpiScaleY));

        if (_spectrumBitmap is null ||
            _spectrumBitmapWidth != pixelWidth ||
            _spectrumBitmapHeight != pixelHeight)
        {
            _spectrumBitmap = new WriteableBitmap(
                pixelWidth,
                pixelHeight,
                96 * dpiScaleX,
                96 * dpiScaleY,
                PixelFormats.Pbgra32,
                null);
            _spectrumPixels = new int[pixelWidth * pixelHeight];
            _spectrumBitmapWidth = pixelWidth;
            _spectrumBitmapHeight = pixelHeight;
            WaveformImage.Source = _spectrumBitmap;
        }

        int rowStep = SpectrumCellHeightPixels + SpectrumCellGapYPixels;
        int columnStep = SpectrumCellWidthPixels + SpectrumCellGapXPixels;
        int availableRows = Math.Max(1, (pixelHeight - 6 + SpectrumCellGapYPixels) / rowStep);
        int availableColumns = Math.Max(1, (pixelWidth - 8 + SpectrumCellGapXPixels) / columnStep);
        int rows = Math.Clamp(availableRows, SpectrumMinRows, SpectrumMaxRows);
        int columns = Math.Clamp(availableColumns, 24, 220);
        int usedWidth = columns * SpectrumCellWidthPixels + (columns - 1) * SpectrumCellGapXPixels;
        int usedHeight = rows * SpectrumCellHeightPixels + (rows - 1) * SpectrumCellGapYPixels;

        _spectrumGridLeftPixels = Math.Max(0, (pixelWidth - usedWidth) / 2);
        _spectrumGridTopPixels = Math.Max(0, (pixelHeight - usedHeight) / 2);
        _spectrumColumnCount = columns;
        _spectrumRowCount = rows;
    }

    private void ResetSpectrumMotionState()
    {
        _spectrumPeakRows = new double[_spectrumColumnCount];
        _spectrumPeakFallVelocities = new double[_spectrumColumnCount];
        _spectrumPeakHoldUntilTicks = new long[_spectrumColumnCount];
        _spectrumPeakLastUpdateTicks = new long[_spectrumColumnCount];
        _spectrumDisplayRows = new double[_spectrumColumnCount];
        _spectrumTargetRows = new double[_spectrumColumnCount];
        _spectrumActiveRows = new int[_spectrumColumnCount];
        _spectrumHeldRows = new double[_spectrumColumnCount];
        Array.Fill(_spectrumDisplayRows, 1);
    }

    private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 1 && e.NewSize.Height > 1)
        {
            RenderWaveform(_waveformPeaks, _spectrumData);
        }
    }

    private void UpdateWaveformPlaybackVisual(TimeSpan position, bool forceSilence = false)
    {
        if ((_waveformPeaks.Length == 0 && _spectrumData is null) ||
            _spectrumBitmap is null ||
            _spectrumColumnCount == 0 ||
            _spectrumRowCount == 0 ||
            _spectrumPixels.Length != _spectrumBitmapWidth * _spectrumBitmapHeight ||
            _spectrumPeakRows.Length != _spectrumColumnCount ||
            _spectrumPeakFallVelocities.Length != _spectrumColumnCount ||
            _spectrumPeakHoldUntilTicks.Length != _spectrumColumnCount ||
            _spectrumPeakLastUpdateTicks.Length != _spectrumColumnCount ||
            _spectrumDisplayRows.Length != _spectrumColumnCount ||
            _spectrumTargetRows.Length != _spectrumColumnCount ||
            _spectrumActiveRows.Length != _spectrumColumnCount ||
            _spectrumHeldRows.Length != _spectrumColumnCount)
        {
            return;
        }

        long nowTicks = Environment.TickCount64;
        double durationSeconds = Math.Max(0, _duration.TotalSeconds);
        double positionSeconds = Math.Max(0, position.TotalSeconds);
        double progress = durationSeconds > 0
            ? Math.Clamp(positionSeconds / durationSeconds, 0, 1)
            : 0;
        double sourceOffset = progress * Math.Max(1, _waveformPeaks.Length - 1);
        double[] targetRows = _spectrumTargetRows;
        AudioSpectrumData? spectrumData = _spectrumData;
        bool spectrumSilent = forceSilence || (spectrumData is not null && ReadSpectrumFramePeak(spectrumData, positionSeconds) < 0.055);

        for (int i = 0; i < _spectrumColumnCount; i++)
        {
            double normalized = _spectrumColumnCount == 1 ? 0 : i / (double)(_spectrumColumnCount - 1);
            double lowBandShape = Math.Exp(-normalized * 4.8);
            double frequencyEnvelope = 0.08 + 0.92 * Math.Exp(-normalized * 5.9);
            double displayEnergy;
            if (forceSilence)
            {
                displayEnergy = 0;
            }
            else if (spectrumData is not null)
            {
                if (spectrumSilent)
                {
                    displayEnergy = 0;
                }
                else
                {
                    double visualBand = 0.015 + Math.Pow(normalized, 1.04) * 0.94;
                    double activeEnergy = ReadSpectrumBandGroup(spectrumData, positionSeconds, visualBand, 3.2);
                    activeEnergy = activeEnergy < 0.045
                        ? 0
                        : Math.Clamp((activeEnergy - 0.045) / 0.88, 0, 1);
                    double bassPresence = 1.0 + 0.12 * Math.Exp(-normalized * 5.5);
                    displayEnergy = Math.Pow(activeEnergy, 0.50) * bassPresence;
                    displayEnergy = Math.Clamp(displayEnergy, 0, 1.0);
                }
            }
            else
            {
                double sampleA = ReadInterpolatedPeak(_waveformPeaks, sourceOffset + i * 2.7 + Math.Sin(i * 0.73) * 11.0);
                double sampleB = ReadInterpolatedPeak(_waveformPeaks, sourceOffset * 0.37 + i * 7.1);
                double rawEnergy = Math.Clamp((sampleA * 0.76 + sampleB * 0.24) * _spectrumGain * 0.88, 0, 1);
                double audioEnergy = rawEnergy < 0.055 ? 0 : Math.Pow((rawEnergy - 0.055) / 0.945, 0.62);
                displayEnergy = audioEnergy * frequencyEnvelope * (0.78 + 0.96 * lowBandShape);
                displayEnergy = Math.Clamp(displayEnergy, 0, 0.68 + 0.25 * lowBandShape);
            }

            double bodyEnergy = spectrumData is not null
                ? Math.Clamp(displayEnergy, 0, 1.0)
                : Math.Clamp(displayEnergy * (0.92 + 0.22 * lowBandShape), 0, 0.62 + 0.22 * lowBandShape);
            targetRows[i] = 1 + bodyEnergy * (_spectrumRowCount - 1);
        }

        for (int i = 0; i < _spectrumColumnCount; i++)
        {
            double previousTarget = targetRows[Math.Max(0, i - 1)];
            double nextTarget = targetRows[Math.Min(targetRows.Length - 1, i + 1)];
            double smoothedTarget = (previousTarget * 1.05 + targetRows[i] * 2.9 + nextTarget * 1.05) / 5.0;
            double current = _spectrumDisplayRows[i];
            if (smoothedTarget > current)
            {
                current = Math.Min(smoothedTarget, current + Math.Max(2.2, (smoothedTarget - current) * 0.88));
            }
            else
            {
                current = Math.Max(smoothedTarget, current - Math.Max(4.6, (current - smoothedTarget) * 0.96));
            }

            current = Math.Clamp(current, 1, _spectrumRowCount);
            _spectrumDisplayRows[i] = current;

            int activeRows = Math.Clamp((int)Math.Round(current), 1, _spectrumRowCount);
            _spectrumActiveRows[i] = activeRows;
            _spectrumHeldRows[i] = UpdateSpectrumPeak(i, activeRows, _spectrumRowCount, nowTicks);
        }

        DrawSpectrumFrame(_spectrumActiveRows, _spectrumHeldRows);
    }

    private double UpdateSpectrumPeak(int column, int activeRows, int maxRows, long nowTicks)
    {
        activeRows = Math.Clamp(activeRows, 1, maxRows);
        double heldRows = Math.Clamp(_spectrumPeakRows[column], 0, maxRows);
        if (activeRows >= heldRows - 0.05)
        {
            _spectrumPeakRows[column] = activeRows;
            _spectrumPeakFallVelocities[column] = 0;
            _spectrumPeakHoldUntilTicks[column] = nowTicks + SpectrumPeakHoldMilliseconds;
            _spectrumPeakLastUpdateTicks[column] = nowTicks;
            return activeRows;
        }

        if (nowTicks > _spectrumPeakHoldUntilTicks[column])
        {
            long lastTicks = _spectrumPeakLastUpdateTicks[column] > 0 ? _spectrumPeakLastUpdateTicks[column] : nowTicks;
            double elapsedSeconds = Math.Clamp((nowTicks - lastTicks) / 1000.0, 0.0, 0.12);
            double velocity = _spectrumPeakFallVelocities[column] > 0
                ? _spectrumPeakFallVelocities[column]
                : SpectrumPeakInitialFallRowsPerSecond;
            double fallDistance = velocity * elapsedSeconds +
                0.5 * SpectrumPeakGravityRowsPerSecondSquared * elapsedSeconds * elapsedSeconds;
            velocity = Math.Min(
                SpectrumPeakMaxFallRowsPerSecond,
                velocity + SpectrumPeakGravityRowsPerSecondSquared * elapsedSeconds);
            heldRows = Math.Max(activeRows, heldRows - fallDistance);
            _spectrumPeakRows[column] = heldRows;
            _spectrumPeakFallVelocities[column] = heldRows <= activeRows + 0.001 ? 0 : velocity;
        }

        _spectrumPeakLastUpdateTicks[column] = nowTicks;
        return Math.Clamp(_spectrumPeakRows[column], activeRows, maxRows);
    }

    private bool IsSpectrumSettled()
    {
        if (_spectrumDisplayRows.Length == 0 || _spectrumPeakRows.Length == 0)
        {
            return true;
        }

        for (int i = 0; i < _spectrumDisplayRows.Length; i++)
        {
            if (_spectrumDisplayRows[i] > 1.02)
            {
                return false;
            }
        }

        for (int i = 0; i < _spectrumPeakRows.Length; i++)
        {
            if (_spectrumPeakRows[i] > 1.18)
            {
                return false;
            }
        }

        return true;
    }

    private void DrawSpectrumFrame(int[]? activeRows, double[]? heldRows)
    {
        if (_spectrumBitmap is null ||
            _spectrumPixels.Length != _spectrumBitmapWidth * _spectrumBitmapHeight ||
            _spectrumBitmapWidth <= 0 ||
            _spectrumBitmapHeight <= 0 ||
            _spectrumColumnCount <= 0 ||
            _spectrumRowCount <= 0)
        {
            return;
        }

        Array.Fill(_spectrumPixels, SpectrumBackgroundColor);

        int columnStep = SpectrumCellWidthPixels + SpectrumCellGapXPixels;
        int rowStep = SpectrumCellHeightPixels + SpectrumCellGapYPixels;
        for (int column = 0; column < _spectrumColumnCount; column++)
        {
            int active = activeRows is not null && column < activeRows.Length
                ? Math.Clamp(activeRows[column], 0, _spectrumRowCount)
                : 0;
            int activeStartRow = _spectrumRowCount - active;
            int cellLeft = _spectrumGridLeftPixels + column * columnStep;

            for (int row = 0; row < _spectrumRowCount; row++)
            {
                bool isActive = row >= activeStartRow;
                int color = SpectrumInactiveColor;
                if (isActive)
                {
                    color = row <= activeStartRow + 1 ? SpectrumHighColor : SpectrumLowColor;
                }

                int cellTop = _spectrumGridTopPixels + row * rowStep;
                DrawSpectrumBlock(cellLeft, cellTop, SpectrumCellWidthPixels, SpectrumCellHeightPixels, color);
            }

            double peak = heldRows is not null && column < heldRows.Length
                ? heldRows[column]
                : 0;
            if (peak > active + 0.18)
            {
                int maxPeakTop = _spectrumGridTopPixels + (_spectrumRowCount - 1) * rowStep;
                int peakTop = _spectrumGridTopPixels + (int)Math.Round((_spectrumRowCount - peak) * rowStep);
                DrawSpectrumBlock(
                    cellLeft,
                    Math.Clamp(peakTop, _spectrumGridTopPixels, maxPeakTop),
                    SpectrumCellWidthPixels,
                    SpectrumCellHeightPixels,
                    SpectrumPeakColor);
            }
        }

        _spectrumBitmap.WritePixels(
            new Int32Rect(0, 0, _spectrumBitmapWidth, _spectrumBitmapHeight),
            _spectrumPixels,
            _spectrumBitmapWidth * sizeof(int),
            0);
    }

    private void DrawSpectrumBlock(int left, int top, int width, int height, int color)
    {
        int startX = Math.Clamp(left, 0, _spectrumBitmapWidth);
        int endX = Math.Clamp(left + width, 0, _spectrumBitmapWidth);
        int startY = Math.Clamp(top, 0, _spectrumBitmapHeight);
        int endY = Math.Clamp(top + height, 0, _spectrumBitmapHeight);

        for (int y = startY; y < endY; y++)
        {
            int offset = y * _spectrumBitmapWidth + startX;
            for (int x = startX; x < endX; x++)
            {
                _spectrumPixels[offset++] = color;
            }
        }
    }

    private double GetWaveformWidth()
    {
        return WaveformImage.ActualWidth > 1 ? WaveformImage.ActualWidth : 216;
    }

    private double GetWaveformHeight()
    {
        return WaveformImage.ActualHeight > 1 ? WaveformImage.ActualHeight : 58;
    }

    private static float ReadInterpolatedPeak(float[] peaks, double index)
    {
        if (peaks.Length == 0)
        {
            return 0;
        }

        double wrapped = index % peaks.Length;
        if (wrapped < 0)
        {
            wrapped += peaks.Length;
        }

        int left = (int)Math.Floor(wrapped);
        int right = (left + 1) % peaks.Length;
        double amount = wrapped - left;
        return (float)(peaks[left] + (peaks[right] - peaks[left]) * amount);
    }

    private static double ReadSpectrumBand(AudioSpectrumData spectrum, double seconds, double normalizedBand)
    {
        if (spectrum.Frames.Length == 0 || spectrum.BandCount <= 0 || spectrum.FrameDurationSeconds <= 0)
        {
            return 0;
        }

        double framePosition = Math.Clamp(seconds / spectrum.FrameDurationSeconds, 0, spectrum.Frames.Length - 1);
        int frameA = (int)Math.Floor(framePosition);
        int frameB = Math.Min(spectrum.Frames.Length - 1, frameA + 1);
        double frameMix = framePosition - frameA;

        double bandPosition = Math.Clamp(normalizedBand, 0, 1) * (spectrum.BandCount - 1);
        int bandA = (int)Math.Floor(bandPosition);
        int bandB = Math.Min(spectrum.BandCount - 1, bandA + 1);
        double bandMix = bandPosition - bandA;

        double valueA = Lerp(spectrum.Frames[frameA][bandA], spectrum.Frames[frameA][bandB], bandMix);
        double valueB = Lerp(spectrum.Frames[frameB][bandA], spectrum.Frames[frameB][bandB], bandMix);
        return Lerp(valueA, valueB, frameMix);
    }

    private static double ReadSpectrumBandGroup(
        AudioSpectrumData spectrum,
        double seconds,
        double normalizedBand,
        double groupWidthBands)
    {
        if (spectrum.Frames.Length == 0 || spectrum.BandCount <= 0 || spectrum.FrameDurationSeconds <= 0)
        {
            return 0;
        }

        double framePosition = Math.Clamp(seconds / spectrum.FrameDurationSeconds, 0, spectrum.Frames.Length - 1);
        int frameA = (int)Math.Floor(framePosition);
        int frameB = Math.Min(spectrum.Frames.Length - 1, frameA + 1);
        double frameMix = framePosition - frameA;

        double bandCenter = Math.Clamp(normalizedBand, 0, 1) * (spectrum.BandCount - 1);
        double valueA = ReadSpectrumBandGroupFromFrame(spectrum.Frames[frameA], bandCenter, groupWidthBands);
        double valueB = ReadSpectrumBandGroupFromFrame(spectrum.Frames[frameB], bandCenter, groupWidthBands);
        return Lerp(valueA, valueB, frameMix);
    }

    private static double ReadSpectrumBandGroupFromFrame(float[] frame, double bandCenter, double groupWidthBands)
    {
        if (frame.Length == 0)
        {
            return 0;
        }

        double halfWidth = Math.Max(0.5, groupWidthBands / 2);
        int start = Math.Max(0, (int)Math.Floor(bandCenter - halfWidth));
        int end = Math.Min(frame.Length - 1, (int)Math.Ceiling(bandCenter + halfWidth));
        double weightedTotal = 0;
        double weightTotal = 0;
        double maxValue = 0;

        for (int band = start; band <= end; band++)
        {
            double distance = Math.Abs(band - bandCenter);
            double weight = Math.Max(0.18, 1.0 - distance / (halfWidth + 0.5));
            weightedTotal += frame[band] * weight;
            weightTotal += weight;
            maxValue = Math.Max(maxValue, frame[band]);
        }

        double average = weightTotal > 0 ? weightedTotal / weightTotal : 0;
        return Math.Clamp(maxValue * 0.34 + average * 0.66, 0, 1);
    }

    private static double ReadSpectrumFramePeak(AudioSpectrumData spectrum, double seconds)
    {
        if (spectrum.Frames.Length == 0 || spectrum.FrameDurationSeconds <= 0)
        {
            return 0;
        }

        double framePosition = Math.Clamp(seconds / spectrum.FrameDurationSeconds, 0, spectrum.Frames.Length - 1);
        int frameA = (int)Math.Floor(framePosition);
        int frameB = Math.Min(spectrum.Frames.Length - 1, frameA + 1);
        double peakA = 0;
        double peakB = 0;
        foreach (float value in spectrum.Frames[frameA])
        {
            peakA = Math.Max(peakA, value);
        }

        foreach (float value in spectrum.Frames[frameB])
        {
            peakB = Math.Max(peakB, value);
        }

        return Math.Max(peakA, peakB);
    }

    private static double Lerp(double left, double right, double amount)
    {
        return left + (right - left) * amount;
    }

    private static double CalculateSpectrumGain(float[] peaks)
    {
        if (peaks.Length == 0)
        {
            return 1;
        }

        var positivePeaks = new List<float>(peaks.Length);
        foreach (float peak in peaks)
        {
            if (peak > 0.001f)
            {
                positivePeaks.Add(peak);
            }
        }

        if (positivePeaks.Count == 0)
        {
            return 1;
        }

        positivePeaks.Sort();
        int index = Math.Clamp((int)(positivePeaks.Count * 0.82), 0, positivePeaks.Count - 1);
        double referencePeak = positivePeaks[index];
        return referencePeak > 0
            ? Math.Clamp(0.56 / referencePeak, 1.0, 4.4)
            : 1;
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdatePlayButtonIcon()
    {
        if (PlayIcon is null)
        {
            return;
        }

        PlayIcon.Data = Geometry.Parse(_isPlaying ? PauseIconData : PlayIconData);
    }

    private static string FormatShortTime(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }

    private static string FormatLongTime(TimeSpan value)
    {
        return value.ToString(@"hh\:mm\:ss");
    }

    private static void QueueTemporaryAudioCleanup(AudioPreviewDocument? document)
    {
        if (document is null)
        {
            return;
        }

        AudioPreviewTempFileCleanup.Queue(document.TemporaryPaths);
    }
}
