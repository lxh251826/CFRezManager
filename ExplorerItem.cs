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
