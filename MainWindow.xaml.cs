using System.IO;
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
    private ExplorerItem? _rootItem;
    private ExplorerItem? _currentItem;
    private readonly List<ExplorerItem> _backHistory = new();
    private readonly List<ExplorerItem> _forwardHistory = new();
    private string _selectedDirectory = string.Empty;
    private int _archiveCount;
    private int _extractableFileCount;
    private bool _isBusy;

    private readonly record struct ExtractionProgress(int Completed, int Total, string FileName);

    public MainWindow()
    {
        InitializeComponent();
        LoadEmptyStateImage();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        string? folder = SelectFolder("Select the folder that contains REZ archives.", null);
        if (folder is not null)
        {
            await LoadDirectoryAsync(folder);
        }
        else
        {
            StatusText.Text = "No folder selected";
        }
    }

    private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string? folder = SelectFolder("Select the folder that contains REZ archives.", _selectedDirectory);
        if (folder is not null)
        {
            await LoadDirectoryAsync(folder);
        }
    }

    private async void PackFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string? sourceDirectory = SelectFolder("Select the folder to pack into a REZ archive.", _selectedDirectory);
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
            StatusText.Text = "Packing REZ archive...";

            var progress = new Progress<RezArchiveWriteProgress>(state =>
            {
                StatusText.Text = $"Packing {state.CompletedFiles:N0}/{state.TotalFiles:N0}: {state.FileName}";
            });

            RezArchiveWriteResult result = await Task.Run(() => RezArchiveWriter.WriteFromDirectory(sourceDirectory, outputPath, progress));
            StatusText.Text = $"Packed {result.FileCount:N0} files into {outputPath}";
        }
        catch (Exception ex)
        {
            ShowError("Pack failed", ex);
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

        await ExtractItemsAsync(new[] { _rootItem }, "Preparing REZ archives...");
    }

    private async void ExtractSelectedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        List<ExplorerItem> selectedItems = GetSelectedExplorerItems();
        if (selectedItems.Count == 0)
        {
            return;
        }

        string label = selectedItems.Count == 1 ? selectedItems[0].Name : $"{selectedItems.Count:N0} selected items";
        await ExtractItemsAsync(selectedItems, $"Preparing {label}...");
    }

    private async Task ExtractItemsAsync(IReadOnlyCollection<ExplorerItem> items, string preparingMessage)
    {
        if (items.Count == 0)
        {
            return;
        }

        string? outputDirectory = SelectFolder("Select an output folder.", _selectedDirectory);
        if (outputDirectory is null)
        {
            return;
        }

        SetBusy(true);

        try
        {
            WorkProgress.IsIndeterminate = true;
            StatusText.Text = preparingMessage;

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
                StatusText.Text = "No files to extract";
                return;
            }

            int workerCount = GetExtractionWorkerCount(files.Count);
            StatusText.Text = $"Extracting {files.Count:N0} files with {workerCount} workers...";
            WorkProgress.IsIndeterminate = false;
            WorkProgress.Minimum = 0;
            WorkProgress.Maximum = files.Count;
            WorkProgress.Value = 0;

            var progress = new Progress<ExtractionProgress>(state =>
            {
                WorkProgress.Value = state.Completed;
                StatusText.Text = $"Extracting {state.Completed:N0}/{state.Total:N0}: {state.FileName}";
            });

            await Task.Run(() => ExtractFilesParallel(outputDirectory, items, files, workerCount, progress));

            StatusText.Text = $"Extracted {files.Count:N0} files to {outputDirectory}";
        }
        catch (Exception ex)
        {
            ShowError("Extract failed", ex);
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
            ? "Extract This Item..."
            : $"Extract {selectedItems.Count:N0} Selected Items...";
    }

    private List<ExplorerItem> GetSelectedExplorerItems()
    {
        return ContentsList.SelectedItems.OfType<ExplorerItem>().ToList();
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
        HeaderText.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        BreadcrumbPanel.Children.Clear();
        BreadcrumbPanel.Children.Add(new TextBlock { Text = folder, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        StatusText.Text = "Scanning REZ archives...";
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

            StatusText.Text = $"Found {_archiveCount:N0} REZ archives";
        }
        catch (Exception ex)
        {
            _rootItem = null;
            _currentItem = null;
            _backHistory.Clear();
            _forwardHistory.Clear();
            ContentsList.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            ShowError("Scan failed", ex);
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
            ShowError("Load failed", ex);
            return;
        }

        if (addToHistory && _currentItem is not null && !ReferenceEquals(_currentItem, item))
        {
            _backHistory.Add(_currentItem);
            _forwardHistory.Clear();
        }

        _currentItem = item;
        ContentsList.SelectedItem = null;
        ContentsList.ItemsSource = item.Children;
        EmptyStatePanel.Visibility = item.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderText.Text = item.Name;
        StatusText.Text = item.Children.Count == 0
            ? "\u7A7A\u76EE\u5F55"
            : $"\u663E\u793A {item.Children.Count:N0} \u9879";
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
        StatusText.Text = $"Loading {loadingTarget}...";

        try
        {
            await Task.Run(() => LoadContainerChildren(item));
            StatusText.Text = $"Loaded {item.Children.Count:N0} items from {item.Name}";
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
        ContentsList.IsEnabled = !isBusy;
        UpdateCommandState();
    }

    private void UpdateCommandState()
    {
        BrowseFolderButton.IsEnabled = !_isBusy;
        PackFolderButton.IsEnabled = !_isBusy;
        ExtractAllButton.IsEnabled = !_isBusy && _rootItem is not null && (_archiveCount > 0 || _extractableFileCount > 0);
    }

    private static string? SelectFolder(string description, string? initialDirectory)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(initialDirectory) ? initialDirectory : Environment.CurrentDirectory
        };

        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private static string? SelectRezOutputFile(string sourceDirectory)
    {
        string sourceName = Path.GetFileName(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = "packed";
        }

        using var dialog = new Forms.SaveFileDialog
        {
            Title = "Save REZ archive",
            Filter = "REZ archives (*.rez)|*.rez|All files (*.*)|*.*",
            DefaultExt = "rez",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Directory.Exists(sourceDirectory) ? sourceDirectory : Environment.CurrentDirectory,
            FileName = $"{sourceName}.rez"
        };

        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.FileName : null;
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

    private void ShowError(string title, Exception ex)
    {
        StatusText.Text = $"{title}: {ex.Message}";
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
