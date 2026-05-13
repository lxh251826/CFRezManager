using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

public enum ExplorerItemKind
{
    Directory,
    RezArchive,
    RezDirectory,
    RezFile
}

public sealed class ExplorerItem
{
    private static readonly ImageSource? FolderIconImage = LoadFolderIcon();

    public required string Name { get; init; }
    public required ExplorerItemKind Kind { get; init; }
    public string SourcePath { get; init; } = string.Empty;
    public string OutputRelativePath { get; init; } = string.Empty;
    public RezArchive? Archive { get; set; }
    public RezDirectoryNode? ArchiveDirectory { get; set; }
    public RezFileNode? ArchiveFile { get; set; }
    public ExplorerItem? Parent { get; set; }
    public bool IsLoaded { get; set; } = true;
    public List<ExplorerItem> Children { get; } = new();

    public bool IsContainer => Kind is ExplorerItemKind.Directory or ExplorerItemKind.RezArchive or ExplorerItemKind.RezDirectory;

    public ImageSource? IconSource => IsContainer ? FolderIconImage : null;

    public long? SizeBytes
    {
        get
        {
            return Kind switch
            {
                ExplorerItemKind.RezFile => ArchiveFile?.Size,
                ExplorerItemKind.RezArchive => TryGetFileSize(SourcePath),
                ExplorerItemKind.Directory => SumChildSizes(),
                ExplorerItemKind.RezDirectory => SumChildSizes() ?? SumRezNodeSizes(ArchiveDirectory),
                _ => null
            };
        }
    }

    public string SizeText => FormatByteSize(SizeBytes);

    public string KindText => Kind switch
    {
        ExplorerItemKind.Directory => "Folder",
        ExplorerItemKind.RezArchive => "REZ archive",
        ExplorerItemKind.RezDirectory => "REZ folder",
        ExplorerItemKind.RezFile => "REZ file",
        _ => "Item"
    };

    public string LocationText
    {
        get
        {
            if (!string.IsNullOrEmpty(OutputRelativePath))
            {
                return OutputRelativePath;
            }

            return SourcePath;
        }
    }

    public string InfoToolTip
    {
        get
        {
            var lines = new List<string>
            {
                $"Name: {Name}",
                $"Type: {KindText}",
                $"Size: {SizeText}"
            };

            if (!string.IsNullOrWhiteSpace(OutputRelativePath))
            {
                lines.Add($"Path: {OutputRelativePath}");
            }

            if (!string.IsNullOrWhiteSpace(SourcePath))
            {
                lines.Add($"Source: {SourcePath}");
            }

            if (ArchiveFile is not null)
            {
                lines.Add($"Offset: {ArchiveFile.DataOffset:N0}");
                lines.Add($"MD5: {ArchiveFile.Md5}");
            }

            if (Archive is not null)
            {
                lines.Add($"Archive: {Path.GetFileName(Archive.FilePath)}");
            }

            int childCount = Children.Count > 0 ? Children.Count : ArchiveDirectory?.Children.Count ?? 0;
            if (IsContainer)
            {
                lines.Add($"Items: {childCount:N0}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public void AddChild(ExplorerItem child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public void SortChildren()
    {
        Children.Sort((left, right) =>
        {
            int leftGroup = left.IsContainer ? 0 : 1;
            int rightGroup = right.IsContainer ? 0 : 1;
            int groupCompare = leftGroup.CompareTo(rightGroup);
            return groupCompare != 0
                ? groupCompare
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (ExplorerItem child in Children)
        {
            child.SortChildren();
        }
    }

    private long? SumChildSizes()
    {
        if (Children.Count == 0)
        {
            return null;
        }

        long total = 0;
        bool hasSize = false;
        foreach (ExplorerItem child in Children)
        {
            long? size = child.SizeBytes;
            if (size is null)
            {
                continue;
            }

            total += size.Value;
            hasSize = true;
        }

        return hasSize ? total : null;
    }

    private static long? SumRezNodeSizes(RezDirectoryNode? directory)
    {
        if (directory is null)
        {
            return null;
        }

        long total = 0;
        bool hasSize = false;
        foreach (RezNode node in directory.Children)
        {
            if (node is RezFileNode file)
            {
                total += file.Size;
                hasSize = true;
            }
            else if (node is RezDirectoryNode childDirectory)
            {
                long? childSize = SumRezNodeSizes(childDirectory);
                if (childSize is not null)
                {
                    total += childSize.Value;
                    hasSize = true;
                }
            }
        }

        return hasSize ? total : null;
    }

    private static long? TryGetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatByteSize(long? bytes)
    {
        if (bytes is null)
        {
            return "-";
        }

        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes.Value;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:N0} {units[unitIndex]}" : $"{value:N1} {units[unitIndex]}";
    }

    private static ImageSource? LoadFolderIcon()
    {
        string imagePath = Path.Combine(AppContext.BaseDirectory, "assets", "folder.png");
        if (!File.Exists(imagePath))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(imagePath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
