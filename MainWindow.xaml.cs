using System.IO;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace CFRezManager;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty TileItemWidthProperty =
        DependencyProperty.Register(nameof(TileItemWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(136.0));

    public static readonly DependencyProperty TileItemHeightProperty =
        DependencyProperty.Register(nameof(TileItemHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(128.0));

    public static readonly DependencyProperty TileCellWidthProperty =
        DependencyProperty.Register(nameof(TileCellWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(148.0));

    public static readonly DependencyProperty TileCellHeightProperty =
        DependencyProperty.Register(nameof(TileCellHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(140.0));

    public static readonly DependencyProperty TileIconSizeProperty =
        DependencyProperty.Register(nameof(TileIconSize), typeof(double), typeof(MainWindow), new PropertyMetadata(61.0));

    public static readonly DependencyProperty TileIconRowHeightProperty =
        DependencyProperty.Register(nameof(TileIconRowHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(71.0));

    public static readonly DependencyProperty TileFileIconWidthProperty =
        DependencyProperty.Register(nameof(TileFileIconWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(50.0));

    public static readonly DependencyProperty TileFilePageWidthProperty =
        DependencyProperty.Register(nameof(TileFilePageWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(42.0));

    public static readonly DependencyProperty TileFilePageHeightProperty =
        DependencyProperty.Register(nameof(TileFilePageHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(54.0));

    public static readonly DependencyProperty TileNameMaxHeightProperty =
        DependencyProperty.Register(nameof(TileNameMaxHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(44.0));

    private const double ListViewThreshold = 35.0;

    private enum AppLanguage
    {
        Chinese,
        English
    }

    private enum FolderDialogKind
    {
        RezSource,
        PackSource,
        Output
    }

    private ExplorerItem? _rootItem;
    private ExplorerItem? _currentItem;
    private readonly List<ExplorerItem> _backHistory = new();
    private readonly List<ExplorerItem> _forwardHistory = new();
    private readonly UserSettings _settings;
    private readonly HashSet<ExplorerItem> _dragInitialSelection = new();
    private List<SearchEntry> _searchIndex = new();
    private AppLanguage _language = AppLanguage.Chinese;
    private string _statusKey = "Ready";
    private object[] _statusArgs = [];
    private string _selectedDirectory = string.Empty;
    private System.Windows.Point? _dragStartPoint;
    private int _archiveCount;
    private int _extractableFileCount;
    private bool _isBusy;
    private bool _isDragSelecting;
    private bool _isListViewMode;
    private bool _isSearchIndexReady;
    private bool _isSearchMode;
    private bool _suppressSearchTextChanged;
    private int _searchRequestVersion;

    private readonly record struct ExtractionProgress(int Completed, int Total, string FileName);
    private readonly record struct SearchEntry(ExplorerItem Item, string SearchText);

    public double TileItemWidth
    {
        get => (double)GetValue(TileItemWidthProperty);
        set => SetValue(TileItemWidthProperty, value);
    }

    public double TileItemHeight
    {
        get => (double)GetValue(TileItemHeightProperty);
        set => SetValue(TileItemHeightProperty, value);
    }

    public double TileCellWidth
    {
        get => (double)GetValue(TileCellWidthProperty);
        set => SetValue(TileCellWidthProperty, value);
    }

    public double TileCellHeight
    {
        get => (double)GetValue(TileCellHeightProperty);
        set => SetValue(TileCellHeightProperty, value);
    }

    public double TileIconSize
    {
        get => (double)GetValue(TileIconSizeProperty);
        set => SetValue(TileIconSizeProperty, value);
    }

    public double TileIconRowHeight
    {
        get => (double)GetValue(TileIconRowHeightProperty);
        set => SetValue(TileIconRowHeightProperty, value);
    }

    public double TileFileIconWidth
    {
        get => (double)GetValue(TileFileIconWidthProperty);
        set => SetValue(TileFileIconWidthProperty, value);
    }

    public double TileFilePageWidth
    {
        get => (double)GetValue(TileFilePageWidthProperty);
        set => SetValue(TileFilePageWidthProperty, value);
    }

    public double TileFilePageHeight
    {
        get => (double)GetValue(TileFilePageHeightProperty);
        set => SetValue(TileFilePageHeightProperty, value);
    }

    public double TileNameMaxHeight
    {
        get => (double)GetValue(TileNameMaxHeightProperty);
        set => SetValue(TileNameMaxHeightProperty, value);
    }

    private static readonly IReadOnlyDictionary<string, (string Chinese, string English)> Texts =
        new Dictionary<string, (string Chinese, string English)>
        {
            ["BrowseFolder"] = ("选择文件夹...", "Select Folder..."),
            ["ExtractAll"] = ("全部导出...", "Extract All..."),
            ["PackFolder"] = ("打包文件夹...", "Pack Folder..."),
            ["HeaderDefault"] = ("选择一个文件夹扫描 REZ 资源包", "Choose a folder to scan REZ archives"),
            ["Contents"] = ("内容", "Contents"),
            ["SearchLabel"] = ("搜索:", "Search:"),
            ["SearchTooltip"] = ("输入关键字快速搜索已扫描的文件和目录", "Type keywords to quickly search indexed files and folders"),
            ["ClearSearch"] = ("清除搜索", "Clear search"),
            ["ViewSizeLabel"] = ("大小", "Size"),
            ["ViewSizeTooltip"] = ("缩小切换到列表，放大切换到平铺图标", "Shrink for list view, enlarge for tile view"),
            ["BuildingSearchIndex"] = ("正在建立搜索索引...", "Building search index..."),
            ["SearchIndexReady"] = ("搜索索引已就绪，共 {0:N0} 项", "Search index ready, {0:N0} items"),
            ["SearchResults"] = ("搜索 “{0}”：找到 {1:N0} 项", "Search \"{0}\": {1:N0} items found"),
            ["SearchNoResults"] = ("搜索 “{0}”：没有结果", "Search \"{0}\": no results"),
            ["SearchFailed"] = ("搜索失败", "Search failed"),
            ["EmptyFolder"] = ("空目录", "Empty folder"),
            ["Ready"] = ("就绪", "Ready"),
            ["NoFolderSelected"] = ("未选择文件夹", "No folder selected"),
            ["SelectRezFolderDescription"] = ("选择包含 REZ 资源包的文件夹。", "Select the folder that contains REZ archives."),
            ["SelectPackFolderDescription"] = ("选择要打包成 REZ 的文件夹。", "Select the folder to pack into a REZ archive."),
            ["SelectOutputFolderDescription"] = ("选择输出文件夹。", "Select an output folder."),
            ["SaveRezArchiveTitle"] = ("保存 REZ 资源包", "Save REZ archive"),
            ["RezFileFilter"] = ("REZ 资源包 (*.rez)|*.rez|所有文件 (*.*)|*.*", "REZ archives (*.rez)|*.rez|All files (*.*)|*.*"),
            ["PreparingRezArchives"] = ("正在准备 REZ 资源包...", "Preparing REZ archives..."),
            ["SelectedItemsLabel"] = ("{0:N0} 个选中项", "{0:N0} selected items"),
            ["PreparingItem"] = ("正在准备 {0}...", "Preparing {0}..."),
            ["PackingArchive"] = ("正在打包 REZ 资源包...", "Packing REZ archive..."),
            ["PackingProgress"] = ("正在打包 {0:N0}/{1:N0}: {2}", "Packing {0:N0}/{1:N0}: {2}"),
            ["PackedResult"] = ("已将 {0:N0} 个文件打包到 {1}", "Packed {0:N0} files into {1}"),
            ["PackFailed"] = ("打包失败", "Pack failed"),
            ["NoFilesToExtract"] = ("没有可导出的文件", "No files to extract"),
            ["ExtractingStart"] = ("正在使用 {1} 个线程导出 {0:N0} 个文件...", "Extracting {0:N0} files with {1} workers..."),
            ["ExtractingProgress"] = ("正在导出 {0:N0}/{1:N0}: {2}", "Extracting {0:N0}/{1:N0}: {2}"),
            ["ExtractedResult"] = ("已将 {0:N0} 个文件导出到 {1}", "Extracted {0:N0} files to {1}"),
            ["ExtractFailed"] = ("导出失败", "Extract failed"),
            ["ScanningRezArchives"] = ("正在扫描 REZ 资源包...", "Scanning REZ archives..."),
            ["FoundRezArchives"] = ("找到 {0:N0} 个 REZ 资源包", "Found {0:N0} REZ archives"),
            ["ScanFailed"] = ("扫描失败", "Scan failed"),
            ["LoadFailed"] = ("加载失败", "Load failed"),
            ["LoadingItem"] = ("正在加载 {0}...", "Loading {0}..."),
            ["LoadedItems"] = ("已从 {1} 加载 {0:N0} 项", "Loaded {0:N0} items from {1}"),
            ["ShowingItems"] = ("显示 {0:N0} 项", "Showing {0:N0} items"),
            ["ExtractThisItem"] = ("导出此项...", "Extract This Item..."),
            ["ExtractSelectedItems"] = ("导出 {0:N0} 个选中项...", "Extract {0:N0} Selected Items..."),
            ["ExtractSelectedDefault"] = ("导出选中项...", "Extract Selected..."),
            ["ErrorStatus"] = ("{0}: {1}", "{0}: {1}")
        };

    public MainWindow()
    {
        _settings = UserSettings.Load();
        _language = ParseLanguage(_settings.Language);
        InitializeComponent();
        LanguageComboBox.SelectedIndex = _language == AppLanguage.English ? 1 : 0;
        ViewSizeSlider.Value = ClampViewSize(_settings.ViewSize);
        ApplyViewSize(ViewSizeSlider.Value);
        ApplyLanguage();
        LoadEmptyStateImage();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        string? folder = SelectFolder(T("SelectRezFolderDescription"), FolderDialogKind.RezSource);
        if (folder is not null)
        {
            await LoadDirectoryAsync(folder);
        }
        else
        {
            SetStatus("NoFolderSelected");
        }
    }

    private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string? folder = SelectFolder(T("SelectRezFolderDescription"), FolderDialogKind.RezSource, _selectedDirectory);
        if (folder is not null)
        {
            await LoadDirectoryAsync(folder);
        }
    }

    private async void PackFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string? sourceDirectory = SelectFolder(T("SelectPackFolderDescription"), FolderDialogKind.PackSource, _selectedDirectory);
        if (sourceDirectory is null)
        {
            return;
        }

        string? outputPath = SelectRezOutputFile(sourceDirectory);
        if (outputPath is null)
        {
            return;
        }

        SetBusy(true);

        try
        {
            WorkProgress.IsIndeterminate = true;
            SetStatus("PackingArchive");

            var progress = new Progress<RezArchiveWriteProgress>(state =>
            {
                SetStatus("PackingProgress", state.CompletedFiles, state.TotalFiles, state.FileName);
            });

            RezArchiveWriteResult result = await Task.Run(() => RezArchiveWriter.WriteFromDirectory(sourceDirectory, outputPath, progress));
            SetStatus("PackedResult", result.FileCount, outputPath);
        }
        catch (Exception ex)
        {
            ShowError("PackFailed", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ExtractAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_rootItem is null || _archiveCount == 0)
        {
            return;
        }

        await ExtractItemsAsync(new[] { _rootItem }, T("PreparingRezArchives"));
    }

    private async void ExtractSelectedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        List<ExplorerItem> selectedItems = GetSelectedExplorerItems();
        if (selectedItems.Count == 0)
        {
            return;
        }

        string label = selectedItems.Count == 1 ? selectedItems[0].Name : FormatText("SelectedItemsLabel", selectedItems.Count);
        await ExtractItemsAsync(selectedItems, FormatText("PreparingItem", label));
    }

    private async Task ExtractItemsAsync(IReadOnlyCollection<ExplorerItem> items, string preparingMessage)
    {
        if (items.Count == 0)
        {
            return;
        }

        string? outputDirectory = SelectFolder(T("SelectOutputFolderDescription"), FolderDialogKind.Output, _selectedDirectory);
        if (outputDirectory is null)
        {
            return;
        }

        SetBusy(true);

        try
        {
            WorkProgress.IsIndeterminate = true;
            SetStatusText(preparingMessage);

            await Task.Run(() => LoadItemsForExtraction(items));

            var files = new List<ExplorerItem>();
            foreach (ExplorerItem item in items)
            {
                CollectExtractableFiles(item, files);
            }

            files = files.Distinct().ToList();
            _extractableFileCount = files.Count;
            if (files.Count == 0)
            {
                SetStatus("NoFilesToExtract");
                return;
            }

            int workerCount = GetExtractionWorkerCount(files.Count);
            SetStatus("ExtractingStart", files.Count, workerCount);
            WorkProgress.IsIndeterminate = false;
            WorkProgress.Minimum = 0;
            WorkProgress.Maximum = files.Count;
            WorkProgress.Value = 0;

            var progress = new Progress<ExtractionProgress>(state =>
            {
                WorkProgress.Value = state.Completed;
                SetStatus("ExtractingProgress", state.Completed, state.Total, state.FileName);
            });

            await Task.Run(() => ExtractFilesParallel(outputDirectory, items, files, workerCount, progress));

            SetStatus("ExtractedResult", files.Count, outputDirectory);
        }
        catch (Exception ex)
        {
            ShowError("ExtractFailed", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ContentsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is ExplorerItem item && item.IsContainer)
        {
            await ShowFolderAsync(item);
        }
    }

    private async void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
        {
            e.Handled = true;
            await NavigateBackAsync();
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            e.Handled = true;
            await NavigateForwardAsync();
        }
    }

    private void ContentsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isBusy ||
            e.ChangedButton != MouseButton.Left ||
            FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _dragStartPoint = e.GetPosition(SelectionOverlay);
        _dragInitialSelection.Clear();
        foreach (ExplorerItem item in ContentsList.SelectedItems.OfType<ExplorerItem>())
        {
            _dragInitialSelection.Add(item);
        }
    }

    private void ContentsList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isBusy || _dragStartPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        System.Windows.Point currentPoint = e.GetPosition(SelectionOverlay);
        if (!_isDragSelecting)
        {
            double horizontalMove = Math.Abs(currentPoint.X - _dragStartPoint.Value.X);
            double verticalMove = Math.Abs(currentPoint.Y - _dragStartPoint.Value.Y);
            if (horizontalMove < SystemParameters.MinimumHorizontalDragDistance &&
                verticalMove < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _isDragSelecting = true;
            SelectionBox.Visibility = Visibility.Visible;
            ContentsList.CaptureMouse();
        }

        UpdateSelectionBox(currentPoint);
        UpdateDragSelection(GetSelectionRect(currentPoint));
        e.Handled = true;
    }

    private void ContentsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragSelecting)
        {
            e.Handled = true;
        }

        EndDragSelection();
    }

    private void ContentsList_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        EndDragSelection();
    }

    private void ContentsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } listBoxItem)
        {
            return;
        }

        if (!listBoxItem.IsSelected)
        {
            ContentsList.SelectedItems.Clear();
            listBoxItem.IsSelected = true;
        }

        listBoxItem.Focus();
    }

    private void ContentsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        List<ExplorerItem> selectedItems = GetSelectedExplorerItems();
        ExtractSelectedMenuItem.IsEnabled = !_isBusy && selectedItems.Count > 0;
        ExtractSelectedMenuItem.Header = selectedItems.Count == 1
            ? T("ExtractThisItem")
            : FormatText("ExtractSelectedItems", selectedItems.Count);
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageComboBox.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        _language = string.Equals(tag, "en", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.English
            : AppLanguage.Chinese;
        _settings.Language = ToLanguageCode(_language);
        _settings.Save();
        ApplyLanguage();
    }

    private async void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearchTextChanged)
        {
            return;
        }

        string query = SearchTextBox.Text.Trim();
        ClearSearchButton.IsEnabled = query.Length > 0;
        int requestVersion = ++_searchRequestVersion;

        if (query.Length == 0)
        {
            ExitSearchMode();
            return;
        }

        if (_rootItem is null)
        {
            return;
        }

        if (!_isSearchIndexReady)
        {
            await EnsureSearchIndexAsync();
        }

        if (_isSearchIndexReady && requestVersion == _searchRequestVersion && SearchTextBox.Text.Trim().Length > 0)
        {
            ApplySearch(SearchTextBox.Text.Trim());
        }
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        ClearSearchText();
    }

    private void ViewSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized)
        {
            return;
        }

        ApplyViewSize(e.NewValue);
        _settings.ViewSize = e.NewValue;
        _settings.Save();
    }

    private List<ExplorerItem> GetSelectedExplorerItems()
    {
        return ContentsList.SelectedItems.OfType<ExplorerItem>().ToList();
    }

    private void UpdateSelectionBox(System.Windows.Point currentPoint)
    {
        Rect rect = GetSelectionRect(currentPoint);
        Canvas.SetLeft(SelectionBox, rect.Left);
        Canvas.SetTop(SelectionBox, rect.Top);
        SelectionBox.Width = rect.Width;
        SelectionBox.Height = rect.Height;
    }

    private Rect GetSelectionRect(System.Windows.Point currentPoint)
    {
        System.Windows.Point startPoint = _dragStartPoint ?? currentPoint;
        return new Rect(startPoint, currentPoint);
    }

    private void UpdateDragSelection(Rect selectionRect)
    {
        var hitItems = new HashSet<ExplorerItem>();
        foreach (object item in ContentsList.Items)
        {
            if (ContentsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container ||
                container.DataContext is not ExplorerItem explorerItem ||
                !container.IsVisible)
            {
                continue;
            }

            Rect itemRect = container
                .TransformToVisual(SelectionOverlay)
                .TransformBounds(new Rect(new System.Windows.Size(container.ActualWidth, container.ActualHeight)));
            if (selectionRect.IntersectsWith(itemRect))
            {
                hitItems.Add(explorerItem);
            }
        }

        IEnumerable<ExplorerItem> selectedItems;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            var toggledSelection = new HashSet<ExplorerItem>(_dragInitialSelection);
            foreach (ExplorerItem item in hitItems)
            {
                if (!toggledSelection.Add(item))
                {
                    toggledSelection.Remove(item);
                }
            }

            selectedItems = toggledSelection;
        }
        else if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            var appendedSelection = new HashSet<ExplorerItem>(_dragInitialSelection);
            appendedSelection.UnionWith(hitItems);
            selectedItems = appendedSelection;
        }
        else
        {
            selectedItems = hitItems;
        }

        ApplySelectedItems(selectedItems);
    }

    private void ApplySelectedItems(IEnumerable<ExplorerItem> selectedItems)
    {
        var selectedSet = selectedItems.ToHashSet();
        ContentsList.SelectedItems.Clear();
        foreach (ExplorerItem item in selectedSet)
        {
            ContentsList.SelectedItems.Add(item);
        }
    }

    private void EndDragSelection()
    {
        _dragStartPoint = null;
        _dragInitialSelection.Clear();
        _isDragSelecting = false;
        SelectionBox.Visibility = Visibility.Collapsed;
        if (ContentsList.IsMouseCaptured)
        {
            ContentsList.ReleaseMouseCapture();
        }
    }

    private void ApplyViewSize(double value)
    {
        bool useListView = value < ListViewThreshold;
        if (useListView)
        {
            if (!_isListViewMode)
            {
                _isListViewMode = true;
                ContentsList.ItemTemplate = (DataTemplate)FindResource("ExplorerListTemplate");
                ContentsList.ItemContainerStyle = (Style)FindResource("ExplorerListItemStyle");
                ContentsList.ItemsPanel = (ItemsPanelTemplate)FindResource("ExplorerListPanelTemplate");
            }

            ContentsList.InvalidateMeasure();
            return;
        }

        double factor = Math.Clamp((value - ListViewThreshold) / (100.0 - ListViewThreshold), 0, 1);
        TileItemWidth = 112 + (44 * factor);
        TileItemHeight = 104 + (44 * factor);
        TileCellWidth = TileItemWidth + 12;
        TileCellHeight = TileItemHeight + 12;
        TileIconSize = 48 + (24 * factor);
        TileIconRowHeight = TileIconSize + 10;
        TileFileIconWidth = TileIconSize * 0.82;
        TileFilePageWidth = TileIconSize * 0.68;
        TileFilePageHeight = TileIconSize * 0.88;
        TileNameMaxHeight = 34 + (18 * factor);

        if (_isListViewMode)
        {
            _isListViewMode = false;
            ContentsList.ItemTemplate = (DataTemplate)FindResource("ExplorerIconTemplate");
            ContentsList.ItemContainerStyle = (Style)FindResource("ExplorerTileItemStyle");
            ContentsList.ItemsPanel = (ItemsPanelTemplate)FindResource("ExplorerTilePanelTemplate");
        }

        ContentsList.InvalidateMeasure();
    }

    private static double ClampViewSize(double value)
    {
        if (double.IsNaN(value))
        {
            return 72;
        }

        return Math.Clamp(value, 0, 100);
    }

    private async Task EnsureSearchIndexAsync()
    {
        if (_isSearchIndexReady || _rootItem is null)
        {
            return;
        }

        ExplorerItem root = _rootItem;
        SetBusy(true);
        SetStatus("BuildingSearchIndex");

        try
        {
            List<SearchEntry> index = await Task.Run(() => BuildSearchIndex(root));
            if (ReferenceEquals(root, _rootItem))
            {
                _searchIndex = index;
                _isSearchIndexReady = true;
                SetStatus("SearchIndexReady", index.Count);
            }
        }
        catch (Exception ex)
        {
            ShowError("SearchFailed", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static List<SearchEntry> BuildSearchIndex(ExplorerItem root)
    {
        LoadAllArchives(root);

        var entries = new List<SearchEntry>();
        foreach (ExplorerItem child in root.Children)
        {
            CollectSearchEntries(child, entries);
        }

        return entries;
    }

    private static void CollectSearchEntries(ExplorerItem item, List<SearchEntry> entries)
    {
        entries.Add(new SearchEntry(item, CreateSearchText(item)));
        foreach (ExplorerItem child in item.Children)
        {
            CollectSearchEntries(child, entries);
        }
    }

    private static string CreateSearchText(ExplorerItem item)
    {
        return string.Join('\n', item.Name, item.OutputRelativePath, item.SourcePath);
    }

    private void ApplySearch(string query)
    {
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<ExplorerItem> results = _searchIndex
            .Where(entry => terms.All(term => entry.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => entry.Item)
            .Distinct()
            .ToList();

        _isSearchMode = true;
        ContentsList.SelectedItems.Clear();
        ContentsList.ItemsSource = results;
        EmptyStatePanel.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (results.Count == 0)
        {
            SetStatus("SearchNoResults", query);
        }
        else
        {
            SetStatus("SearchResults", query, results.Count);
            ContentsList.ScrollIntoView(results[0]);
        }
    }

    private void ExitSearchMode()
    {
        if (!_isSearchMode)
        {
            return;
        }

        _isSearchMode = false;
        if (_currentItem is null)
        {
            ContentsList.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        ContentsList.SelectedItems.Clear();
        ContentsList.ItemsSource = _currentItem.Children;
        EmptyStatePanel.Visibility = _currentItem.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetFolderStatus(_currentItem);
        if (_currentItem.Children.Count > 0)
        {
            ContentsList.ScrollIntoView(_currentItem.Children[0]);
        }
    }

    private void ClearSearchText()
    {
        if (SearchTextBox.Text.Length == 0)
        {
            ExitSearchMode();
            return;
        }

        SearchTextBox.Clear();
    }

    private void ClearSearchTextSilently()
    {
        _suppressSearchTextChanged = true;
        SearchTextBox.Clear();
        ClearSearchButton.IsEnabled = false;
        _suppressSearchTextChanged = false;
        _searchRequestVersion++;
        _isSearchMode = false;
    }

    private void ResetSearchState(bool clearText)
    {
        _searchIndex.Clear();
        _isSearchIndexReady = false;
        _isSearchMode = false;
        _searchRequestVersion++;
        if (clearText)
        {
            ClearSearchTextSilently();
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void ApplyLanguage()
    {
        BrowseFolderButton.Content = T("BrowseFolder");
        ExtractAllButton.Content = T("ExtractAll");
        PackFolderButton.Content = T("PackFolder");
        ContentsHeaderText.Text = T("Contents");
        EmptyStateText.Text = T("EmptyFolder");
        ExtractSelectedMenuItem.Header = T("ExtractSelectedDefault");
        SearchLabelText.Text = T("SearchLabel");
        SearchTextBox.ToolTip = T("SearchTooltip");
        ClearSearchButton.ToolTip = T("ClearSearch");
        ViewSizeLabelText.Text = T("ViewSizeLabel");
        ViewSizeSlider.ToolTip = T("ViewSizeTooltip");

        if (_currentItem is null)
        {
            HeaderText.Text = T("HeaderDefault");
        }

        RefreshStatusText();
    }

    private static AppLanguage ParseLanguage(string? value)
    {
        return string.Equals(value, "en", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.English
            : AppLanguage.Chinese;
    }

    private static string ToLanguageCode(AppLanguage language)
    {
        return language == AppLanguage.English ? "en" : "zh";
    }

    private string T(string key)
    {
        if (!Texts.TryGetValue(key, out (string Chinese, string English) text))
        {
            return key;
        }

        return _language == AppLanguage.English ? text.English : text.Chinese;
    }

    private string FormatText(string key, params object[] args)
    {
        return args.Length == 0
            ? T(key)
            : string.Format(CultureInfo.CurrentCulture, T(key), args);
    }

    private void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        RefreshStatusText();
    }

    private void SetStatusText(string text)
    {
        _statusKey = string.Empty;
        _statusArgs = [];
        StatusText.Text = text;
    }

    private void RefreshStatusText()
    {
        if (string.IsNullOrEmpty(_statusKey))
        {
            return;
        }

        StatusText.Text = FormatText(_statusKey, _statusArgs);
    }

    private void SetFolderStatus(ExplorerItem item)
    {
        SetStatus(item.Children.Count == 0 ? "EmptyFolder" : "ShowingItems", item.Children.Count);
    }

    private async void BreadcrumbButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: ExplorerItem item })
        {
            await ShowFolderAsync(item);
        }
    }

    private async Task NavigateBackAsync()
    {
        if (_isBusy || _currentItem is null || _backHistory.Count == 0)
        {
            return;
        }

        ExplorerItem target = PopHistoryItem(_backHistory);
        _forwardHistory.Add(_currentItem);
        await ShowFolderAsync(target, addToHistory: false);
    }

    private async Task NavigateForwardAsync()
    {
        if (_isBusy || _currentItem is null || _forwardHistory.Count == 0)
        {
            return;
        }

        ExplorerItem target = PopHistoryItem(_forwardHistory);
        _backHistory.Add(_currentItem);
        await ShowFolderAsync(target, addToHistory: false);
    }

    private static ExplorerItem PopHistoryItem(List<ExplorerItem> history)
    {
        int lastIndex = history.Count - 1;
        ExplorerItem item = history[lastIndex];
        history.RemoveAt(lastIndex);
        return item;
    }

    private async Task LoadDirectoryAsync(string folder)
    {
        SetBusy(true);
        ResetSearchState(clearText: true);
        HeaderText.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        BreadcrumbPanel.Children.Clear();
        BreadcrumbPanel.Children.Add(new TextBlock { Text = folder, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        SetStatus("ScanningRezArchives");
        WorkProgress.IsIndeterminate = true;

        try
        {
            ExplorerItem root = await Task.Run(() => BuildDirectoryTree(folder));
            _rootItem = root;
            _selectedDirectory = folder;
            _archiveCount = CountItems(root, ExplorerItemKind.RezArchive);
            _extractableFileCount = 0;
            _backHistory.Clear();
            _forwardHistory.Clear();

            await ShowFolderAsync(root, addToHistory: false);

            SetStatus("FoundRezArchives", _archiveCount);
        }
        catch (Exception ex)
        {
            _rootItem = null;
            _currentItem = null;
            _backHistory.Clear();
            _forwardHistory.Clear();
            ContentsList.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            ShowError("ScanFailed", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static ExplorerItem BuildDirectoryTree(string folder)
    {
        string rootName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(rootName))
        {
            rootName = folder;
        }

        var root = new ExplorerItem
        {
            Name = rootName,
            Kind = ExplorerItemKind.Directory,
            SourcePath = folder,
            OutputRelativePath = string.Empty
        };

        PopulateDirectory(root, folder, folder);
        root.SortChildren();
        return root;
    }

    private static void PopulateDirectory(ExplorerItem parent, string folder, string rootFolder)
    {
        foreach (string directory in SafeEnumerateDirectories(folder))
        {
            string relativePath = Path.GetRelativePath(rootFolder, directory);
            var directoryItem = new ExplorerItem
            {
                Name = Path.GetFileName(directory),
                Kind = ExplorerItemKind.Directory,
                SourcePath = directory,
                OutputRelativePath = SanitizeRelativePath(relativePath)
            };

            PopulateDirectory(directoryItem, directory, rootFolder);
            if (directoryItem.Children.Count > 0)
            {
                parent.AddChild(directoryItem);
            }
        }

        foreach (string rezPath in SafeEnumerateRezFiles(folder))
        {
            parent.AddChild(CreateArchivePlaceholderItem(rezPath, rootFolder));
        }
    }

    private static ExplorerItem CreateArchivePlaceholderItem(string rezPath, string rootFolder)
    {
        string relativePath = Path.ChangeExtension(Path.GetRelativePath(rootFolder, rezPath), null) ?? Path.GetFileNameWithoutExtension(rezPath);
        string outputRelativePath = SanitizeRelativePath(relativePath);

        return new ExplorerItem
        {
            Name = Path.GetFileNameWithoutExtension(rezPath),
            Kind = ExplorerItemKind.RezArchive,
            SourcePath = rezPath,
            OutputRelativePath = outputRelativePath,
            IsLoaded = false
        };
    }

    private static ExplorerItem CreateArchiveChildItem(RezNode node, RezArchive archive, string parentOutputPath)
    {
        string outputRelativePath = CombineRelativePath(parentOutputPath, SanitizePathSegment(node.Name));

        if (node is RezDirectoryNode directory)
        {
            return new ExplorerItem
            {
                Name = directory.Name,
                Kind = ExplorerItemKind.RezDirectory,
                SourcePath = archive.FilePath,
                OutputRelativePath = outputRelativePath,
                Archive = archive,
                ArchiveDirectory = directory,
                IsLoaded = false
            };
        }

        var file = (RezFileNode)node;
        return new ExplorerItem
        {
            Name = file.Name,
            Kind = ExplorerItemKind.RezFile,
            SourcePath = archive.FilePath,
            OutputRelativePath = outputRelativePath,
            Archive = archive,
            ArchiveFile = file
        };
    }

    private async Task ShowFolderAsync(ExplorerItem item, bool addToHistory = true)
    {
        try
        {
            await EnsureContainerLoadedAsync(item);
        }
        catch (Exception ex)
        {
            ShowError("LoadFailed", ex);
            return;
        }

        if (addToHistory && _currentItem is not null && !ReferenceEquals(_currentItem, item))
        {
            _backHistory.Add(_currentItem);
            _forwardHistory.Clear();
        }

        _currentItem = item;
        ClearSearchTextSilently();
        ContentsList.SelectedItem = null;
        ContentsList.ItemsSource = item.Children;
        EmptyStatePanel.Visibility = item.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderText.Text = item.Name;
        SetFolderStatus(item);
        RenderBreadcrumb(item);
        UpdateCommandState();
        if (item.Children.Count > 0)
        {
            ContentsList.ScrollIntoView(item.Children[0]);
        }
    }

    private async Task EnsureContainerLoadedAsync(ExplorerItem item)
    {
        if (item.Kind is not (ExplorerItemKind.RezArchive or ExplorerItemKind.RezDirectory) || item.IsLoaded)
        {
            return;
        }

        SetBusy(true);
        WorkProgress.IsIndeterminate = true;
        string loadingTarget = item.Kind == ExplorerItemKind.RezArchive ? $"{item.Name}.rez" : item.Name;
        SetStatus("LoadingItem", loadingTarget);

        try
        {
            await Task.Run(() => LoadContainerChildren(item));
            SetStatus("LoadedItems", item.Children.Count, item.Name);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        WorkProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        WorkProgress.IsIndeterminate = isBusy;
        BrowseFolderButton.IsEnabled = !isBusy;
        PackFolderButton.IsEnabled = !isBusy;
        SearchTextBox.IsEnabled = !isBusy;
        ClearSearchButton.IsEnabled = !isBusy && SearchTextBox.Text.Length > 0;
        ContentsList.IsEnabled = !isBusy;
        UpdateCommandState();
    }

    private void UpdateCommandState()
    {
        BrowseFolderButton.IsEnabled = !_isBusy;
        PackFolderButton.IsEnabled = !_isBusy;
        ExtractAllButton.IsEnabled = !_isBusy && _rootItem is not null && (_archiveCount > 0 || _extractableFileCount > 0);
    }

    private string? SelectFolder(string description, FolderDialogKind kind, string? fallbackDirectory = null)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            SelectedPath = ResolveInitialDirectory(GetRememberedDirectory(kind), fallbackDirectory)
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return null;
        }

        RememberDirectory(kind, dialog.SelectedPath);
        return dialog.SelectedPath;
    }

    private string? SelectRezOutputFile(string sourceDirectory)
    {
        string sourceName = Path.GetFileName(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = "packed";
        }

        using var dialog = new Forms.SaveFileDialog
        {
            Title = T("SaveRezArchiveTitle"),
            Filter = T("RezFileFilter"),
            DefaultExt = "rez",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = ResolveInitialDirectory(_settings.LastSaveDirectory, sourceDirectory),
            FileName = $"{sourceName}.rez"
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return null;
        }

        string? outputDirectory = Path.GetDirectoryName(dialog.FileName);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            _settings.LastSaveDirectory = outputDirectory;
            _settings.LastDirectory = outputDirectory;
            _settings.Save();
        }

        return dialog.FileName;
    }

    private string GetRememberedDirectory(FolderDialogKind kind)
    {
        return kind switch
        {
            FolderDialogKind.RezSource => _settings.LastRezDirectory,
            FolderDialogKind.PackSource => _settings.LastPackDirectory,
            FolderDialogKind.Output => _settings.LastOutputDirectory,
            _ => string.Empty
        };
    }

    private void RememberDirectory(FolderDialogKind kind, string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        switch (kind)
        {
            case FolderDialogKind.RezSource:
                _settings.LastRezDirectory = directory;
                break;
            case FolderDialogKind.PackSource:
                _settings.LastPackDirectory = directory;
                break;
            case FolderDialogKind.Output:
                _settings.LastOutputDirectory = directory;
                break;
        }

        _settings.LastDirectory = directory;
        _settings.Save();
    }

    private string ResolveInitialDirectory(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        if (Directory.Exists(_settings.LastDirectory))
        {
            return _settings.LastDirectory;
        }

        return Environment.CurrentDirectory;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateRezFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(file => string.Equals(Path.GetExtension(file), ".rez", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void CollectExtractableFiles(ExplorerItem item, List<ExplorerItem> files)
    {
        if (item.Kind == ExplorerItemKind.RezFile && item.Archive is not null && item.ArchiveFile is not null)
        {
            files.Add(item);
        }

        foreach (ExplorerItem child in item.Children)
        {
            CollectExtractableFiles(child, files);
        }
    }

    private static void LoadItemsForExtraction(IEnumerable<ExplorerItem> items)
    {
        foreach (ExplorerItem item in items)
        {
            LoadItemForExtraction(item);
        }
    }

    private static void LoadItemForExtraction(ExplorerItem item)
    {
        switch (item.Kind)
        {
            case ExplorerItemKind.Directory:
                LoadAllArchives(item);
                break;
            case ExplorerItemKind.RezArchive:
            case ExplorerItemKind.RezDirectory:
                LoadContainerChildren(item);
                LoadAllRezDirectories(item);
                break;
        }
    }

    private static void LoadAllArchives(ExplorerItem rootItem)
    {
        var archives = new List<ExplorerItem>();
        CollectArchiveItems(rootItem, archives);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
        };

        Parallel.ForEach(archives, options, archive =>
        {
            LoadContainerChildren(archive);
            LoadAllRezDirectories(archive);
        });
    }

    private static void CollectArchiveItems(ExplorerItem item, List<ExplorerItem> archives)
    {
        if (item.Kind == ExplorerItemKind.RezArchive)
        {
            archives.Add(item);
            return;
        }

        foreach (ExplorerItem child in item.Children)
        {
            CollectArchiveItems(child, archives);
        }
    }

    private static void LoadAllRezDirectories(ExplorerItem item)
    {
        foreach (ExplorerItem child in item.Children.ToArray())
        {
            if (child.Kind == ExplorerItemKind.RezDirectory)
            {
                LoadContainerChildren(child);
                LoadAllRezDirectories(child);
            }
        }
    }

    private static void LoadContainerChildren(ExplorerItem item)
    {
        if (item.IsLoaded)
        {
            return;
        }

        if (item.Kind == ExplorerItemKind.RezArchive)
        {
            var reader = new RezArchiveReader();
            RezArchive archive = reader.Read(item.SourcePath);
            item.Archive = archive;
            item.ArchiveDirectory = archive.Root;
        }

        if (item.Archive is null || item.ArchiveDirectory is null)
        {
            item.IsLoaded = true;
            return;
        }

        item.Children.Clear();
        foreach (RezNode child in item.ArchiveDirectory.Children)
        {
            item.AddChild(CreateArchiveChildItem(child, item.Archive, item.OutputRelativePath));
        }

        item.SortChildren();
        item.IsLoaded = true;
    }

    private static void ExtractFilesParallel(
        string outputDirectory,
        IEnumerable<ExplorerItem> rootItems,
        IReadOnlyList<ExplorerItem> files,
        int workerCount,
        IProgress<ExtractionProgress> progress)
    {
        foreach (string directory in CollectOutputDirectories(rootItems))
        {
            Directory.CreateDirectory(Path.Combine(outputDirectory, directory));
        }

        int completed = 0;
        long nextReportAt = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = workerCount };

        Parallel.ForEach(files, options, item =>
        {
            string destinationPath = Path.Combine(outputDirectory, item.OutputRelativePath);
            RezArchiveReader.ExtractFile(item.Archive!, item.ArchiveFile!, destinationPath);

            int done = Interlocked.Increment(ref completed);
            if (done == files.Count || ShouldReportProgress(ref nextReportAt))
            {
                progress.Report(new ExtractionProgress(done, files.Count, item.Name));
            }
        });
    }

    private static bool ShouldReportProgress(ref long nextReportAt)
    {
        long now = Environment.TickCount64;
        long due = Volatile.Read(ref nextReportAt);
        return now >= due && Interlocked.CompareExchange(ref nextReportAt, now + 80, due) == due;
    }

    private static int GetExtractionWorkerCount(int fileCount)
    {
        if (fileCount <= 1)
        {
            return 1;
        }

        return Math.Clamp(Environment.ProcessorCount, 2, 8);
    }

    private static IEnumerable<string> CollectOutputDirectories(ExplorerItem item)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectOutputDirectories(item, directories);
        return directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> CollectOutputDirectories(IEnumerable<ExplorerItem> items)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExplorerItem item in items)
        {
            CollectOutputDirectories(item, directories);
        }

        return directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CollectOutputDirectories(ExplorerItem item, HashSet<string> directories)
    {
        if (item.IsContainer && !string.IsNullOrEmpty(item.OutputRelativePath))
        {
            directories.Add(item.OutputRelativePath);
        }

        foreach (ExplorerItem child in item.Children)
        {
            CollectOutputDirectories(child, directories);
        }
    }

    private static int CountItems(ExplorerItem item, ExplorerItemKind kind)
    {
        int count = item.Kind == kind ? 1 : 0;
        foreach (ExplorerItem child in item.Children)
        {
            count += CountItems(child, kind);
        }

        return count;
    }

    private void RenderBreadcrumb(ExplorerItem item)
    {
        BreadcrumbPanel.Children.Clear();

        var path = new Stack<ExplorerItem>();
        ExplorerItem? cursor = item;
        while (cursor is not null)
        {
            path.Push(cursor);
            cursor = cursor.Parent;
        }

        bool first = true;
        foreach (ExplorerItem part in path)
        {
            if (!first)
            {
                BreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = ">",
                    Foreground = System.Windows.Media.Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 2, 0)
                });
            }

            var button = new System.Windows.Controls.Button
            {
                Content = part.Name,
                Tag = part,
                Style = (Style)FindResource("BreadcrumbButtonStyle")
            };
            button.Click += BreadcrumbButton_Click;
            BreadcrumbPanel.Children.Add(button);
            first = false;
        }
    }

    private static string SanitizeRelativePath(string relativePath)
    {
        string[] parts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine(parts.Select(SanitizePathSegment).ToArray());
    }

    private static string SanitizePathSegment(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }

    private static string CombineRelativePath(string parent, string child)
    {
        return string.IsNullOrEmpty(parent) ? child : Path.Combine(parent, child);
    }

    private void ShowError(string titleKey, Exception ex)
    {
        string title = T(titleKey);
        SetStatus("ErrorStatus", title, ex.Message);
        System.Windows.MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void LoadEmptyStateImage()
    {
        string imagePath = Path.Combine(AppContext.BaseDirectory, "assets", "empty.png");
        if (File.Exists(imagePath))
        {
            EmptyStateImage.Source = new BitmapImage(new Uri(imagePath, UriKind.Absolute));
        }
    }
}
