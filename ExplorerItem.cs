using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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

public enum ImageStorageKind
{
    None,
    DtxUncompressed,
    DtxLzmaCompressed,
    TgaUncompressed,
    TgaLzmaCompressed,
    TgaInsertedFooterHeader,
    TgaRawPixels
}

public sealed class ExplorerItem : INotifyPropertyChanged
{
    private static readonly ImageSource? FolderIconImage = LoadFolderIcon();
    private static readonly SemaphoreSlim ThumbnailSemaphore = new(3);
    private const int MaxThumbnailBytes = 32 * 1024 * 1024;
    private const int MaxPreviewBytes = 128 * 1024 * 1024;
    private const int MaxTextPreviewBytes = 8 * 1024 * 1024;
    private const int MaxModelPreviewBytes = 128 * 1024 * 1024;
    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png",
        "jpg",
        "jpeg",
        "bmp",
        "gif",
        "tif",
        "tiff",
        "tga",
        "dtx"
    };

    private readonly object _thumbnailSync = new();
    private Task? _thumbnailLoadTask;
    private ImageSource? _thumbnailSource;
    private ImageStorageKind _imageStorageKind;

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

    public bool IsThumbnailCandidate => CanLoadThumbnail();

    public bool IsImagePreviewCandidate => CanLoadImagePreview();

    public bool IsModelPreviewCandidate => CanLoadModelPreview();

    public bool IsTextPreviewCandidate => CanLoadTextPreview();

    public bool HasThumbnail => _thumbnailSource is not null;

    public ImageSource? ThumbnailSource => _thumbnailSource;

    public ImageStorageKind ImageStorageKind => _imageStorageKind;

    public string? ThumbnailBadgeText => _imageStorageKind switch
    {
        ImageStorageKind.DtxLzmaCompressed or ImageStorageKind.TgaLzmaCompressed => "LZMA",
        ImageStorageKind.TgaInsertedFooterHeader or ImageStorageKind.TgaRawPixels => "FIX",
        ImageStorageKind.DtxUncompressed or ImageStorageKind.TgaUncompressed => "RAW",
        _ => null
    };

    public string? CompactThumbnailBadgeText => _imageStorageKind switch
    {
        ImageStorageKind.DtxLzmaCompressed or ImageStorageKind.TgaLzmaCompressed => "LZ",
        ImageStorageKind.TgaInsertedFooterHeader or ImageStorageKind.TgaRawPixels => "FX",
        ImageStorageKind.DtxUncompressed or ImageStorageKind.TgaUncompressed => "R",
        _ => null
    };

    public bool HasThumbnailBadge => ThumbnailBadgeText is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

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
                if (_imageStorageKind is ImageStorageKind.DtxLzmaCompressed or ImageStorageKind.TgaLzmaCompressed)
                {
                    lines.Add($"{GetImageFormatLabel(_imageStorageKind)} storage: LZMA compressed");
                }
                else if (_imageStorageKind is ImageStorageKind.DtxUncompressed or ImageStorageKind.TgaUncompressed)
                {
                    lines.Add($"{GetImageFormatLabel(_imageStorageKind)} storage: uncompressed");
                }
                else if (_imageStorageKind is ImageStorageKind.TgaInsertedFooterHeader)
                {
                    lines.Add("TGA storage: repaired inserted footer/header");
                }
                else if (_imageStorageKind is ImageStorageKind.TgaRawPixels)
                {
                    lines.Add("TGA storage: repaired raw pixel data");
                }

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

    public Task LoadThumbnailAsync()
    {
        if (!IsThumbnailCandidate || _thumbnailSource is not null)
        {
            return Task.CompletedTask;
        }

        lock (_thumbnailSync)
        {
            _thumbnailLoadTask ??= LoadThumbnailCoreAsync();
            return _thumbnailLoadTask;
        }
    }

    public async Task<ImageSource?> LoadPreviewImageAsync()
    {
        IReadOnlyList<ImagePreviewFrame> frames = await LoadPreviewFramesAsync();
        return frames.Count > 0 ? frames[0].Source : null;
    }

    public Task<IReadOnlyList<ImagePreviewFrame>> LoadPreviewFramesAsync()
    {
        return IsImagePreviewCandidate
            ? Task.Run(LoadRezPreviewFrames)
            : Task.FromResult((IReadOnlyList<ImagePreviewFrame>)Array.Empty<ImagePreviewFrame>());
    }

    public Task<TextPreviewDocument?> LoadTextPreviewAsync()
    {
        return IsTextPreviewCandidate
            ? Task.Run(LoadTextPreview)
            : Task.FromResult<TextPreviewDocument?>(null);
    }

    public Task<LithTechModelDocument?> LoadModelPreviewAsync()
    {
        return IsModelPreviewCandidate
            ? Task.Run(LoadModelPreview)
            : Task.FromResult<LithTechModelDocument?>(null);
    }

    private async Task LoadThumbnailCoreAsync()
    {
        await ThumbnailSemaphore.WaitAsync();
        try
        {
            ImageSource? thumbnail = await Task.Run(LoadRezThumbnail);
            if (thumbnail is not null)
            {
                _thumbnailSource = thumbnail;
                OnPropertyChanged(nameof(ThumbnailSource));
                OnPropertyChanged(nameof(HasThumbnail));
            }
            else
            {
                ResetThumbnailLoadTask();
            }
        }
        catch
        {
            ResetThumbnailLoadTask();
        }
        finally
        {
            ThumbnailSemaphore.Release();
        }
    }

    private void ResetThumbnailLoadTask()
    {
        lock (_thumbnailSync)
        {
            _thumbnailLoadTask = null;
        }
    }

    private bool CanLoadThumbnail()
    {
        return Kind == ExplorerItemKind.RezFile &&
               Archive is not null &&
               ArchiveFile is not null &&
               (ThumbnailExtensions.Contains(ArchiveFile.Extension) ||
                LithTechModelDecoder.IsCandidate(ArchiveFile.Extension));
    }

    private bool CanLoadImagePreview()
    {
        return Kind == ExplorerItemKind.RezFile &&
               Archive is not null &&
               ArchiveFile is not null &&
               ThumbnailExtensions.Contains(ArchiveFile.Extension);
    }

    private bool CanLoadTextPreview()
    {
        return Kind == ExplorerItemKind.RezFile &&
               Archive is not null &&
               ArchiveFile is not null &&
               (EncTextDecoder.IsCandidate(Name, ArchiveFile.Extension) ||
                TextPreviewDecoder.IsPlainTextExtension(ArchiveFile.Extension));
    }

    private bool CanLoadModelPreview()
    {
        return Kind == ExplorerItemKind.RezFile &&
               Archive is not null &&
               ArchiveFile is not null &&
               LithTechModelDecoder.IsCandidate(ArchiveFile.Extension);
    }

    private ImageSource? LoadRezThumbnail()
    {
        try
        {
            string? extension = ArchiveFile?.Extension;
            if (extension is null)
            {
                return null;
            }

            int maxBytes = LithTechModelDecoder.IsCandidate(extension) ? MaxModelPreviewBytes : MaxThumbnailBytes;
            byte[]? data = ReadArchiveFileBytes(maxBytes);
            if (data is null)
            {
                return null;
            }

            if (LithTechModelDecoder.IsCandidate(extension))
            {
                return LithTechModelDecoder.TryDecode(data, Name, extension, out LithTechModelDocument? document, out _) &&
                       document is not null
                    ? LithTechModelThumbnailRenderer.TryRender(document)
                    : null;
            }

            if (string.Equals(extension, "dtx", StringComparison.OrdinalIgnoreCase))
            {
                SetImageStorageKind(GetDtxStorageKind(data));
                return DtxThumbnailDecoder.TryDecode(data);
            }

            if (string.Equals(extension, "tga", StringComparison.OrdinalIgnoreCase))
            {
                SetImageStorageKind(GetTgaStorageKind(data));
                return TgaThumbnailDecoder.TryDecode(data);
            }

            return LoadRasterImage(extension, data, decodeThumbnail: true);
        }
        catch
        {
            return null;
        }
    }

    private ImageSource? LoadRezPreviewImage()
    {
        IReadOnlyList<ImagePreviewFrame> frames = LoadRezPreviewFrames();
        return frames.Count > 0 ? frames[0].Source : null;
    }

    private IReadOnlyList<ImagePreviewFrame> LoadRezPreviewFrames()
    {
        try
        {
            string? extension = ArchiveFile?.Extension;
            byte[]? data = ReadArchiveFileBytes(MaxPreviewBytes);
            if (data is null || extension is null)
            {
                return Array.Empty<ImagePreviewFrame>();
            }

            if (string.Equals(extension, "dtx", StringComparison.OrdinalIgnoreCase))
            {
                SetImageStorageKind(GetDtxStorageKind(data));
                return CreatePreviewFrames(DtxThumbnailDecoder.TryDecodeOriginal(data));
            }

            if (string.Equals(extension, "tga", StringComparison.OrdinalIgnoreCase))
            {
                SetImageStorageKind(GetTgaStorageKind(data));
                return TgaThumbnailDecoder.TryDecodePreviewFrames(data);
            }

            return LoadRasterPreviewFrames(extension, data);
        }
        catch
        {
            return Array.Empty<ImagePreviewFrame>();
        }
    }

    private TextPreviewDocument? LoadTextPreview()
    {
        try
        {
            string? extension = ArchiveFile?.Extension;
            byte[]? data = ReadArchiveFileBytes(MaxTextPreviewBytes);
            if (data is null || extension is null)
            {
                return null;
            }

            if (EncTextDecoder.IsCandidate(Name, extension))
            {
                if (!EncTextDecoder.TryDecode(data, out byte[] decodedBytes) ||
                    !TextPreviewDecoder.TryDecode(decodedBytes, preferKorean: true, out string decodedText, out string decodedEncoding))
                {
                    return null;
                }

                return new TextPreviewDocument(
                    decodedText,
                    decodedEncoding,
                    TextPreviewStorageKind.EncBase64,
                    data.Length,
                    decodedBytes.Length);
            }

            if (!TextPreviewDecoder.TryDecode(data, preferKorean: false, out string text, out string encoding))
            {
                return null;
            }

            return new TextPreviewDocument(
                text,
                encoding,
                TextPreviewStorageKind.Plain,
                data.Length,
                data.Length);
        }
        catch
        {
            return null;
        }
    }

    private LithTechModelDocument? LoadModelPreview()
    {
        try
        {
            string? extension = ArchiveFile?.Extension;
            byte[]? data = ReadArchiveFileBytes(MaxModelPreviewBytes);
            return data is not null &&
                   extension is not null &&
                   LithTechModelDecoder.TryDecode(data, Name, extension, out LithTechModelDocument? document, out _)
                ? document
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<ImagePreviewFrame> CreatePreviewFrames(ImageSource? imageSource)
    {
        return imageSource is null
            ? Array.Empty<ImagePreviewFrame>()
            : new[] { new ImagePreviewFrame("Original", imageSource) };
    }

    private ImageSource? LoadRasterImage(string extension, byte[] data, bool decodeThumbnail)
    {
        try
        {
            return LoadBitmapImage(data, decodeThumbnail);
        }
        catch
        {
            return TryLoadPngStoredAsTga(extension, data);
        }
    }

    private IReadOnlyList<ImagePreviewFrame> LoadRasterPreviewFrames(string extension, byte[] data)
    {
        try
        {
            return CreatePreviewFrames(LoadBitmapImage(data, decodeThumbnail: false));
        }
        catch
        {
            if (!IsPngExtension(extension))
            {
                return Array.Empty<ImagePreviewFrame>();
            }

            IReadOnlyList<ImagePreviewFrame> frames = TgaThumbnailDecoder.TryDecodePreviewFrames(data);
            if (frames.Count > 0)
            {
                SetImageStorageKind(GetTgaStorageKind(data));
            }

            return frames;
        }
    }

    private ImageSource? TryLoadPngStoredAsTga(string extension, byte[] data)
    {
        if (!IsPngExtension(extension))
        {
            return null;
        }

        ImageSource? image = TgaThumbnailDecoder.TryDecode(data);
        if (image is null)
        {
            IReadOnlyList<ImagePreviewFrame> frames = TgaThumbnailDecoder.TryDecodePreviewFrames(data);
            image = frames.Count > 0 ? frames[0].Source : null;
        }

        if (image is not null)
        {
            SetImageStorageKind(GetTgaStorageKind(data));
        }

        return image;
    }

    private static bool IsPngExtension(string extension)
    {
        return string.Equals(extension, "png", StringComparison.OrdinalIgnoreCase);
    }

    private byte[]? ReadArchiveFileBytes(int maxBytes)
    {
        if (Archive is null ||
            ArchiveFile is null ||
            ArchiveFile.Size < 0 ||
            ArchiveFile.Size > maxBytes)
        {
            return null;
        }

        byte[] data = new byte[ArchiveFile.Size];
        using FileStream source = File.OpenRead(Archive.FilePath);
        source.Position = ArchiveFile.DataOffset;
        source.ReadExactly(data);
        return data;
    }

    private static ImageSource LoadBitmapImage(byte[] data, bool decodeThumbnail)
    {
        using var stream = new MemoryStream(data);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodeThumbnail)
        {
            image.DecodePixelWidth = 192;
            image.DecodePixelHeight = 192;
        }

        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static ImageStorageKind GetDtxStorageKind(byte[] data)
    {
        return DtxThumbnailDecoder.IsLzmaCompressed(data)
            ? ImageStorageKind.DtxLzmaCompressed
            : ImageStorageKind.DtxUncompressed;
    }

    private static ImageStorageKind GetTgaStorageKind(byte[] data)
    {
        if (TgaThumbnailDecoder.IsLzmaCompressed(data))
        {
            return ImageStorageKind.TgaLzmaCompressed;
        }

        return TgaThumbnailDecoder.HasInsertedFooterHeader(data)
            ? ImageStorageKind.TgaInsertedFooterHeader
            : TgaThumbnailDecoder.HasRawPixelData(data)
                ? ImageStorageKind.TgaRawPixels
                : ImageStorageKind.TgaUncompressed;
    }

    private static string GetImageFormatLabel(ImageStorageKind storageKind)
    {
        return storageKind switch
        {
            ImageStorageKind.DtxLzmaCompressed or ImageStorageKind.DtxUncompressed => "DTX",
            ImageStorageKind.TgaLzmaCompressed or ImageStorageKind.TgaUncompressed or ImageStorageKind.TgaInsertedFooterHeader or ImageStorageKind.TgaRawPixels => "TGA",
            _ => "Image"
        };
    }

    private void SetImageStorageKind(ImageStorageKind storageKind)
    {
        if (_imageStorageKind == storageKind)
        {
            return;
        }

        _imageStorageKind = storageKind;
        OnPropertyChanged(nameof(ImageStorageKind));
        OnPropertyChanged(nameof(ThumbnailBadgeText));
        OnPropertyChanged(nameof(CompactThumbnailBadgeText));
        OnPropertyChanged(nameof(HasThumbnailBadge));
        OnPropertyChanged(nameof(InfoToolTip));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
