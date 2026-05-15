using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

public enum ExplorerItemKind
{
    Directory,
    LocalFile,
    RezArchive,
    RezDirectory,
    RezFile
}

public enum ImageStorageKind
{
    None,
    DtxUncompressed,
    DtxLzmaCompressed,
    DdsBlockCompressed,
    DdsUncompressed,
    TgaUncompressed,
    TgaLzmaCompressed,
    TgaInsertedFooterHeader,
    TgaRawPixels,
    LtcText,
    LtcModel,
    LithTechWorldDat,
    LithTechWorldDatLzma,
    CrossFireDat,
    CrossFireDatLzma,
    LithTechSprite,
    LithTechSpriteLzma,
    AudioUncompressed,
    AudioLzmaCompressed,
    ResourceText,
    ResourceTextLzma,
    RasterUncompressed,
    RasterLzmaCompressed,
    LithTechModel,
    LithTechModelLzma,
    FmodBank,
    FmodBankLzma
}

public sealed class ExplorerItem : INotifyPropertyChanged
{
    private static readonly ImageSource? FolderIconImage = LoadFolderIcon();
    private static readonly SemaphoreSlim ThumbnailSemaphore = new(3);
    private const int MaxThumbnailBytes = 32 * 1024 * 1024;
    private const int MaxPreviewBytes = 128 * 1024 * 1024;
    private const int MaxTextPreviewBytes = 8 * 1024 * 1024;
    private const int MaxModelPreviewBytes = 128 * 1024 * 1024;
    private const int MaxWorldDatPreviewBytes = 256 * 1024 * 1024;
    private const int MaxWorldDatThumbnailTriangles = 40_000;
    private const int ThumbnailDecodeMaxSide = 192;
    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png",
        "jpg",
        "jpeg",
        "bmp",
        "gif",
        "tif",
        "tiff",
        "dds",
        "tga",
        "dtx"
    };

    private static readonly HashSet<string> TextThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "cft",
        "fcf",
        "txt"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3",
        "ogg",
        "wav",
        "wave"
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

    public bool IsFile => Kind is ExplorerItemKind.LocalFile or ExplorerItemKind.RezFile;

    public string FileExtension => Kind switch
    {
        ExplorerItemKind.RezFile => ArchiveFile?.Extension ?? string.Empty,
        ExplorerItemKind.LocalFile => Path.GetExtension(SourcePath).TrimStart('.'),
        _ => string.Empty
    };

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
        ImageStorageKind.DtxLzmaCompressed or
        ImageStorageKind.TgaLzmaCompressed or
        ImageStorageKind.RasterLzmaCompressed or
        ImageStorageKind.LithTechModelLzma or
        ImageStorageKind.FmodBankLzma => "LZMA",
        ImageStorageKind.DdsBlockCompressed => "DXT",
        ImageStorageKind.TgaInsertedFooterHeader or ImageStorageKind.TgaRawPixels => "FIX",
        ImageStorageKind.DtxUncompressed or
        ImageStorageKind.DdsUncompressed or
        ImageStorageKind.TgaUncompressed or
        ImageStorageKind.RasterUncompressed or
        ImageStorageKind.LithTechModel => "RAW",
        ImageStorageKind.LtcText or ImageStorageKind.LtcModel => "LTC",
        ImageStorageKind.LithTechWorldDat => "DAT",
        ImageStorageKind.LithTechWorldDatLzma => "LZMA",
        ImageStorageKind.CrossFireDat => "DAT",
        ImageStorageKind.CrossFireDatLzma => "LZMA",
        ImageStorageKind.LithTechSprite => "SPR",
        ImageStorageKind.LithTechSpriteLzma => "LZMA",
        ImageStorageKind.AudioUncompressed => "RAW",
        ImageStorageKind.AudioLzmaCompressed => "LZMA",
        ImageStorageKind.ResourceText => "TXT",
        ImageStorageKind.ResourceTextLzma => "LZMA",
        ImageStorageKind.FmodBank => "BANK",
        _ => null
    };

    public string? CompactThumbnailBadgeText => _imageStorageKind switch
    {
        ImageStorageKind.DtxLzmaCompressed or
        ImageStorageKind.TgaLzmaCompressed or
        ImageStorageKind.RasterLzmaCompressed or
        ImageStorageKind.LithTechModelLzma or
        ImageStorageKind.FmodBankLzma => "LZ",
        ImageStorageKind.DdsBlockCompressed => "DX",
        ImageStorageKind.TgaInsertedFooterHeader or ImageStorageKind.TgaRawPixels => "FX",
        ImageStorageKind.DtxUncompressed or
        ImageStorageKind.DdsUncompressed or
        ImageStorageKind.TgaUncompressed or
        ImageStorageKind.RasterUncompressed or
        ImageStorageKind.LithTechModel => "R",
        ImageStorageKind.LtcText or ImageStorageKind.LtcModel => "LT",
        ImageStorageKind.LithTechWorldDat => "DT",
        ImageStorageKind.LithTechWorldDatLzma => "LZ",
        ImageStorageKind.CrossFireDat => "DT",
        ImageStorageKind.CrossFireDatLzma => "LZ",
        ImageStorageKind.LithTechSprite => "SP",
        ImageStorageKind.LithTechSpriteLzma => "LZ",
        ImageStorageKind.AudioUncompressed => "R",
        ImageStorageKind.AudioLzmaCompressed => "LZ",
        ImageStorageKind.ResourceText => "TX",
        ImageStorageKind.ResourceTextLzma => "LZ",
        ImageStorageKind.FmodBank => "BK",
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
                ExplorerItemKind.LocalFile => TryGetFileSize(SourcePath),
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
        ExplorerItemKind.LocalFile => "File",
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
                if (_imageStorageKind is ImageStorageKind.DtxLzmaCompressed or
                    ImageStorageKind.TgaLzmaCompressed or
                    ImageStorageKind.RasterLzmaCompressed or
                    ImageStorageKind.LithTechModelLzma or
                    ImageStorageKind.FmodBankLzma)
                {
                    lines.Add($"{GetImageFormatLabel(_imageStorageKind)} storage: LZMA compressed");
                }
                else if (_imageStorageKind is ImageStorageKind.DtxUncompressed or
                         ImageStorageKind.TgaUncompressed or
                         ImageStorageKind.RasterUncompressed or
                         ImageStorageKind.LithTechModel or
                         ImageStorageKind.FmodBank)
                {
                    lines.Add($"{GetImageFormatLabel(_imageStorageKind)} storage: uncompressed");
                }
                else if (_imageStorageKind is ImageStorageKind.DdsBlockCompressed)
                {
                    lines.Add("DDS storage: DXT block-compressed");
                }
                else if (_imageStorageKind is ImageStorageKind.DdsUncompressed)
                {
                    lines.Add("DDS storage: uncompressed pixels");
                }
                else if (_imageStorageKind is ImageStorageKind.TgaInsertedFooterHeader)
                {
                    lines.Add("TGA storage: repaired inserted footer/header");
                }
                else if (_imageStorageKind is ImageStorageKind.TgaRawPixels)
                {
                    lines.Add("TGA storage: repaired raw pixel data");
                }
                else if (_imageStorageKind is ImageStorageKind.LtcText)
                {
                    lines.Add("LTC preview: decoded text");
                }
                else if (_imageStorageKind is ImageStorageKind.LtcModel)
                {
                    lines.Add("LTC preview: decoded model");
                }
                else if (_imageStorageKind is ImageStorageKind.LithTechWorldDat)
                {
                    lines.Add("DAT preview: LithTech world");
                }
                else if (_imageStorageKind is ImageStorageKind.LithTechWorldDatLzma)
                {
                    lines.Add("DAT preview: LZMA-compressed LithTech world");
                }
                else if (_imageStorageKind is ImageStorageKind.CrossFireDat)
                {
                    lines.Add("DAT preview: decoded CrossFire object data");
                }
                else if (_imageStorageKind is ImageStorageKind.CrossFireDatLzma)
                {
                    lines.Add("DAT preview: LZMA-compressed CrossFire object data");
                }
                else if (_imageStorageKind is ImageStorageKind.LithTechSprite)
                {
                    lines.Add("SPR preview: LithTech sprite animation");
                }
                else if (_imageStorageKind is ImageStorageKind.LithTechSpriteLzma)
                {
                    lines.Add("SPR preview: LZMA-compressed LithTech sprite animation");
                }
                else if (_imageStorageKind is ImageStorageKind.AudioUncompressed)
                {
                    lines.Add("Audio preview: uncompressed audio");
                }
                else if (_imageStorageKind is ImageStorageKind.AudioLzmaCompressed)
                {
                    lines.Add("Audio preview: LZMA-compressed audio");
                }
                else if (_imageStorageKind is ImageStorageKind.ResourceText)
                {
                    lines.Add("Resource preview: decoded text");
                }
                else if (_imageStorageKind is ImageStorageKind.ResourceTextLzma)
                {
                    lines.Add("Resource preview: LZMA-compressed decoded text");
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
            ? Task.Run(LoadPreviewFrames)
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
            ImageSource? thumbnail = await Task.Run(LoadThumbnail);
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
        string extension = FileExtension;
        return IsFile &&
               (ThumbnailExtensions.Contains(extension) ||
                TextThumbnailExtensions.Contains(extension) ||
                AudioExtensions.Contains(extension) ||
                LithTechModelDecoder.IsCandidate(extension) ||
                LithTechWorldDatDecoder.IsCandidate(extension) ||
                CrossFireDatDecoder.IsCandidate(extension) ||
                LithTechSpriteDecoder.IsCandidate(extension) ||
                FmodBankDecoder.IsCandidate(extension));
    }

    private bool CanLoadImagePreview()
    {
        return IsFile && ThumbnailExtensions.Contains(FileExtension);
    }

    private bool CanLoadTextPreview()
    {
        string extension = FileExtension;
        return IsFile &&
               (EncTextDecoder.IsCandidate(Name, extension) ||
                CrossFireLtcDecoder.IsCandidate(extension) ||
                CrossFireDatDecoder.IsCandidate(extension) ||
                LithTechSpriteDecoder.IsCandidate(extension) ||
                FmodBankDecoder.IsCandidate(extension) ||
                ResourceTextDecoder.IsCandidate(Name, extension) ||
                TextPreviewDecoder.IsPlainTextExtension(extension));
    }

    private bool CanLoadModelPreview()
    {
        string extension = FileExtension;
        return IsFile &&
               (LithTechModelDecoder.IsCandidate(extension) ||
                LithTechWorldDatDecoder.IsCandidate(extension));
    }

    private ImageSource? LoadThumbnail()
    {
        try
        {
            string extension = FileExtension;
            if (string.IsNullOrWhiteSpace(extension))
            {
                return null;
            }

            int maxBytes = LithTechWorldDatDecoder.IsCandidate(extension)
                ? MaxWorldDatPreviewBytes
                : LithTechModelDecoder.IsCandidate(extension)
                        ? MaxModelPreviewBytes
                        : CrossFireDatDecoder.IsCandidate(extension)
                            ? MaxModelPreviewBytes
                            : FmodBankDecoder.IsCandidate(extension)
                                ? FmodBankDecoder.MaxThumbnailSourceBytes
                                : MaxThumbnailBytes;
            byte[]? data = ReadFileBytes(maxBytes);
            if (data is null)
            {
                return null;
            }

            if (LithTechWorldDatDecoder.IsCandidate(extension) &&
                LithTechWorldDatDecoder.TryDecode(data, Name, out LithTechModelDocument? worldDocument, out _) &&
                worldDocument is not null)
            {
                SetImageStorageKind(GetWorldDatStorageKind(data));
                return LithTechModelThumbnailRenderer.TryRender(SimplifyForThumbnail(worldDocument, MaxWorldDatThumbnailTriangles));
            }

            if (CrossFireDatDecoder.IsCandidate(extension))
            {
                return LoadCrossFireDatThumbnail(data);
            }

            if (LithTechSpriteDecoder.IsCandidate(extension))
            {
                return LoadLithTechSpriteThumbnail(data);
            }

            if (FmodBankDecoder.IsCandidate(extension))
            {
                return LoadFmodBankThumbnail(data);
            }

            if (AudioExtensions.Contains(extension))
            {
                return LoadAudioThumbnail(data);
            }

            if (ResourceTextDecoder.IsCandidate(Name, extension) && TextThumbnailExtensions.Contains(extension))
            {
                return LoadResourceTextThumbnail(data, extension);
            }

            if (CrossFireLtcDecoder.IsCandidate(extension))
            {
                return LoadLtcThumbnail(data);
            }

            if (LithTechModelDecoder.IsCandidate(extension))
            {
                if (!LithTechModelDecoder.TryDecode(data, Name, extension, out LithTechModelDocument? document, out _) ||
                    document is null)
                {
                    return null;
                }

                SetImageStorageKind(GetLithTechModelStorageKind(data));
                return LithTechModelThumbnailRenderer.TryRender(document);
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

            if (string.Equals(extension, "dds", StringComparison.OrdinalIgnoreCase))
            {
                SetImageStorageKind(GetDdsStorageKind(data));
                return DdsThumbnailDecoder.TryDecode(data);
            }

            return LoadRasterImage(extension, data, decodeThumbnail: true);
        }
        catch
        {
            return null;
        }
    }

    private ImageSource? LoadLtcThumbnail(byte[] data)
    {
        if (!CrossFireLtcDecoder.TryDecodeText(data, Name, out CrossFireLtcTextDocument? document, out _) ||
            document is null)
        {
            return null;
        }

        if (document.Text.Contains("(lt-model", StringComparison.OrdinalIgnoreCase) &&
            LithTechModelDecoder.TryParseLtaText(
                document.Text,
                Name,
                document.StorageDescription,
                data.Length,
                document.DecodedByteCount,
                out LithTechModelDocument? modelDocument,
                out _) &&
            modelDocument is not null)
        {
            ImageSource? modelThumbnail = LithTechModelThumbnailRenderer.TryRender(modelDocument);
            if (modelThumbnail is not null)
            {
                SetImageStorageKind(ImageStorageKind.LtcModel);
                return modelThumbnail;
            }
        }

        SetImageStorageKind(ImageStorageKind.LtcText);
        return TextThumbnailRenderer.TryRender(Name, document.Text, "LTC");
    }

    private ImageSource? LoadCrossFireDatThumbnail(byte[] data)
    {
        if (!CrossFireDatDecoder.TryDecode(data, Name, out CrossFireDatDocument? document, out _) ||
            document is null)
        {
            return null;
        }

        SetImageStorageKind(GetCrossFireDatStorageKind(data));
        return TextThumbnailRenderer.TryRender(Name, document.Text, "DAT");
    }

    private ImageSource? LoadLithTechSpriteThumbnail(byte[] data)
    {
        if (!LithTechSpriteDecoder.TryDecode(data, Name, out LithTechSpriteDocument? document, out _) ||
            document is null)
        {
            return null;
        }

        SetImageStorageKind(GetLithTechSpriteStorageKind(data));
        ImageSource? frameThumbnail = Kind switch
        {
            ExplorerItemKind.RezFile when Archive is not null => LithTechSpritePreviewLoader.TryLoadThumbnailFromArchive(Archive, document),
            ExplorerItemKind.LocalFile when !string.IsNullOrWhiteSpace(SourcePath) => LithTechSpritePreviewLoader.TryLoadThumbnailFromLocalFile(SourcePath, document),
            _ => null
        };

        return frameThumbnail ?? TextThumbnailRenderer.TryRender(Name, document.Text, "SPR");
    }

    private ImageSource? LoadAudioThumbnail(byte[] data)
    {
        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data);
        if (prepared is null)
        {
            return null;
        }

        SetImageStorageKind(LzmaAloneDecoder.IsCompressed(data)
            ? ImageStorageKind.AudioLzmaCompressed
            : ImageStorageKind.AudioUncompressed);
        return AudioThumbnailRenderer.TryRender(Name, prepared);
    }

    private ImageSource? LoadResourceTextThumbnail(byte[] data, string extension)
    {
        if (!ResourceTextDecoder.TryDecode(data, Name, extension, out ResourceTextDocument? document, out _) ||
            document is null)
        {
            return null;
        }

        SetImageStorageKind(LzmaAloneDecoder.IsCompressed(data)
            ? ImageStorageKind.ResourceTextLzma
            : ImageStorageKind.ResourceText);
        string badge = string.Equals(extension, "txt", StringComparison.OrdinalIgnoreCase)
            ? "TXT"
            : extension.ToUpperInvariant();
        return TextThumbnailRenderer.TryRender(Name, document.Text, badge);
    }

    private ImageSource? LoadFmodBankThumbnail(byte[] data)
    {
        SetImageStorageKind(FmodBankDecoder.IsCompressedBank(data)
            ? ImageStorageKind.FmodBankLzma
            : ImageStorageKind.FmodBank);

        if (FmodBankAudioPreviewDocumentFactory.TryCreateThumbnailAudioData(
                Name,
                data,
                out byte[]? audioData,
                out string? audioTitle,
                out _) &&
            audioData is not null)
        {
            ImageSource? audioThumbnail = AudioThumbnailRenderer.TryRender(audioTitle ?? Name, audioData);
            if (audioThumbnail is not null)
            {
                return audioThumbnail;
            }
        }

        if (!FmodBankDecoder.TryDecode(data, Name, out FmodBankDocument? document, out _) ||
            document is null)
        {
            return null;
        }

        string badge = document.FsbBlockCount > 0 ? "FSB" : "BANK";
        return TextThumbnailRenderer.TryRender(Name, document.Text, badge);
    }

    private static LithTechModelDocument SimplifyForThumbnail(LithTechModelDocument document, int maxTriangles)
    {
        if (document.TriangleCount <= maxTriangles)
        {
            return document;
        }

        var meshes = new List<LithTechMesh>();
        int remainingTriangles = maxTriangles;
        int totalTriangles = document.TriangleCount;

        foreach (LithTechMesh mesh in document.Meshes)
        {
            if (remainingTriangles <= 0)
            {
                break;
            }

            int meshTriangles = mesh.TriangleIndices.Count / 3;
            if (meshTriangles == 0)
            {
                continue;
            }

            int targetTriangles = Math.Max(1, (int)Math.Round((double)meshTriangles / totalTriangles * maxTriangles));
            targetTriangles = Math.Min(targetTriangles, remainingTriangles);
            LithTechMesh? simplified = SimplifyMeshForThumbnail(mesh, targetTriangles);
            if (simplified is null)
            {
                continue;
            }

            meshes.Add(simplified);
            remainingTriangles -= simplified.TriangleIndices.Count / 3;
        }

        return meshes.Count == 0
            ? document
            : new LithTechModelDocument(
                document.Name,
                meshes,
                $"{document.StorageDescription} thumbnail sample",
                document.SourceByteCount,
                document.DecodedByteCount);
    }

    private static LithTechMesh? SimplifyMeshForThumbnail(LithTechMesh mesh, int targetTriangles)
    {
        int meshTriangles = mesh.TriangleIndices.Count / 3;
        if (targetTriangles <= 0 || meshTriangles == 0)
        {
            return null;
        }

        if (meshTriangles <= targetTriangles)
        {
            return mesh;
        }

        int stride = Math.Max(1, meshTriangles / targetTriangles);
        var vertexMap = new Dictionary<int, int>();
        var vertices = new List<LithTechVector3>();
        var indices = new List<int>(targetTriangles * 3);

        for (int triangle = 0; triangle < meshTriangles && indices.Count / 3 < targetTriangles; triangle += stride)
        {
            int triangleOffset = triangle * 3;
            int a = mesh.TriangleIndices[triangleOffset];
            int b = mesh.TriangleIndices[triangleOffset + 1];
            int c = mesh.TriangleIndices[triangleOffset + 2];
            if (!TryAddMappedTriangle(mesh, a, b, c, vertexMap, vertices, indices))
            {
                continue;
            }
        }

        return indices.Count >= 3
            ? new LithTechMesh(mesh.Name, vertices, indices)
            : null;
    }

    private static bool TryAddMappedTriangle(
        LithTechMesh mesh,
        int a,
        int b,
        int c,
        Dictionary<int, int> vertexMap,
        List<LithTechVector3> vertices,
        List<int> indices)
    {
        if (a < 0 || b < 0 || c < 0 ||
            a >= mesh.Vertices.Count ||
            b >= mesh.Vertices.Count ||
            c >= mesh.Vertices.Count)
        {
            return false;
        }

        indices.Add(GetMappedIndex(mesh, a, vertexMap, vertices));
        indices.Add(GetMappedIndex(mesh, b, vertexMap, vertices));
        indices.Add(GetMappedIndex(mesh, c, vertexMap, vertices));
        return true;
    }

    private static int GetMappedIndex(
        LithTechMesh mesh,
        int sourceIndex,
        Dictionary<int, int> vertexMap,
        List<LithTechVector3> vertices)
    {
        if (vertexMap.TryGetValue(sourceIndex, out int mappedIndex))
        {
            return mappedIndex;
        }

        mappedIndex = vertices.Count;
        vertexMap.Add(sourceIndex, mappedIndex);
        vertices.Add(mesh.Vertices[sourceIndex]);
        return mappedIndex;
    }

    private ImageSource? LoadPreviewImage()
    {
        IReadOnlyList<ImagePreviewFrame> frames = LoadPreviewFrames();
        return frames.Count > 0 ? frames[0].Source : null;
    }

    private IReadOnlyList<ImagePreviewFrame> LoadPreviewFrames()
    {
        try
        {
            string extension = FileExtension;
            byte[]? data = ReadFileBytes(MaxPreviewBytes);
            if (data is null || string.IsNullOrWhiteSpace(extension))
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

            if (string.Equals(extension, "dds", StringComparison.OrdinalIgnoreCase))
            {
                SetImageStorageKind(GetDdsStorageKind(data));
                return CreatePreviewFrames(DdsThumbnailDecoder.TryDecodeOriginal(data));
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
            string extension = FileExtension;
            int maxBytes = FmodBankDecoder.IsCandidate(extension)
                ? FmodBankDecoder.MaxSourceBytes
                : MaxTextPreviewBytes;
            byte[]? data = ReadFileBytes(maxBytes);
            if (data is null || string.IsNullOrWhiteSpace(extension))
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

            if (CrossFireLtcDecoder.IsCandidate(extension))
            {
                if (!CrossFireLtcDecoder.TryDecodeText(data, Name, out CrossFireLtcTextDocument? ltcDocument, out _) ||
                    ltcDocument is null)
                {
                    return null;
                }

                return new TextPreviewDocument(
                    ltcDocument.Text,
                    $"{ltcDocument.StorageDescription} / {ltcDocument.EncodingName}",
                    TextPreviewStorageKind.LtcConverted,
                    ltcDocument.SourceByteCount,
                    ltcDocument.DecodedByteCount);
            }

            if (CrossFireDatDecoder.IsCandidate(extension))
            {
                if (!CrossFireDatDecoder.TryDecode(data, Name, out CrossFireDatDocument? datDocument, out _) ||
                    datDocument is null)
                {
                    return null;
                }

                return new TextPreviewDocument(
                    datDocument.Text,
                    $"{datDocument.StorageDescription} / version {datDocument.Version}, {datDocument.ObjectCount:N0} {datDocument.ObjectKind}",
                    TextPreviewStorageKind.CrossFireDat,
                    datDocument.SourceByteCount,
                    datDocument.DecodedByteCount);
            }

            if (LithTechSpriteDecoder.IsCandidate(extension))
            {
                if (!LithTechSpriteDecoder.TryDecode(data, Name, out LithTechSpriteDocument? sprDocument, out _) ||
                    sprDocument is null)
                {
                    return null;
                }

                return new TextPreviewDocument(
                    sprDocument.Text,
                    $"{sprDocument.StorageDescription}, {sprDocument.FrameCount:N0} frames @ {sprDocument.FrameRate:N0} fps",
                    TextPreviewStorageKind.LithTechSprite,
                    sprDocument.SourceByteCount,
                    sprDocument.DecodedByteCount);
            }

            if (FmodBankDecoder.IsCandidate(extension))
            {
                if (!FmodBankDecoder.TryDecode(data, Name, out FmodBankDocument? bankDocument, out _) ||
                    bankDocument is null)
                {
                    return null;
                }

                return new TextPreviewDocument(
                    bankDocument.Text,
                    bankDocument.StorageDescription,
                    TextPreviewStorageKind.FmodBank,
                    bankDocument.SourceByteCount,
                    bankDocument.DecodedByteCount);
            }

            if (ResourceTextDecoder.IsCandidate(Name, extension))
            {
                if (!ResourceTextDecoder.TryDecode(data, Name, extension, out ResourceTextDocument? resourceDocument, out _) ||
                    resourceDocument is null)
                {
                    return null;
                }

                return new TextPreviewDocument(
                    resourceDocument.Text,
                    resourceDocument.Description,
                    TextPreviewStorageKind.ResourceDecoded,
                    resourceDocument.SourceByteCount,
                    resourceDocument.DecodedByteCount);
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
            string extension = FileExtension;
            if (string.IsNullOrWhiteSpace(extension))
            {
                return null;
            }

            int maxBytes = LithTechWorldDatDecoder.IsCandidate(extension)
                ? MaxWorldDatPreviewBytes
                : MaxModelPreviewBytes;
            byte[]? data = ReadFileBytes(maxBytes);
            if (data is null)
            {
                return null;
            }

            if (LithTechWorldDatDecoder.IsCandidate(extension) &&
                LithTechWorldDatDecoder.TryDecode(data, Name, out LithTechModelDocument? worldDocument, out _) &&
                worldDocument is not null)
            {
                return worldDocument;
            }

            return LithTechModelDecoder.TryDecode(data, Name, extension, out LithTechModelDocument? document, out _)
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
            if (!TryPrepareRasterImageData(data, out byte[]? imageData, out ImageStorageKind storageKind) ||
                imageData is null)
            {
                return TryLoadPngStoredAsTga(extension, data);
            }

            ImageSource image = LoadBitmapImage(imageData, decodeThumbnail);
            SetImageStorageKind(storageKind);
            return image;
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
            if (!TryPrepareRasterImageData(data, out byte[]? imageData, out ImageStorageKind storageKind) ||
                imageData is null)
            {
                return Array.Empty<ImagePreviewFrame>();
            }

            IReadOnlyList<ImagePreviewFrame> frames = CreatePreviewFrames(LoadBitmapImage(imageData, decodeThumbnail: false));
            if (frames.Count > 0)
            {
                SetImageStorageKind(storageKind);
            }

            return frames;
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

    private static bool TryPrepareRasterImageData(
        byte[] data,
        out byte[]? imageData,
        out ImageStorageKind storageKind)
    {
        if (!LzmaAloneDecoder.IsCompressed(data))
        {
            imageData = data;
            storageKind = ImageStorageKind.RasterUncompressed;
            return true;
        }

        imageData = LzmaAloneDecoder.TryPrepareData(data);
        storageKind = ImageStorageKind.RasterLzmaCompressed;
        return imageData is not null;
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

    private byte[]? ReadFileBytes(int maxBytes)
    {
        if (Kind == ExplorerItemKind.LocalFile)
        {
            var info = new FileInfo(SourcePath);
            if (!info.Exists || info.Length < 0 || info.Length > maxBytes || info.Length > int.MaxValue)
            {
                return null;
            }

            return File.ReadAllBytes(SourcePath);
        }

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
            SetThumbnailDecodeSize(image, data);
        }

        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void SetThumbnailDecodeSize(BitmapImage image, byte[] data)
    {
        (int width, int height) = ReadBitmapDimensions(data);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (width >= height)
        {
            image.DecodePixelWidth = ThumbnailDecodeMaxSide;
        }
        else
        {
            image.DecodePixelHeight = ThumbnailDecodeMaxSide;
        }
    }

    private static (int Width, int Height) ReadBitmapDimensions(byte[] data)
    {
        using var stream = new MemoryStream(data);
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.Default);
        BitmapFrame frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static ImageStorageKind GetDtxStorageKind(byte[] data)
    {
        return DtxThumbnailDecoder.IsLzmaCompressed(data)
            ? ImageStorageKind.DtxLzmaCompressed
            : ImageStorageKind.DtxUncompressed;
    }

    private static ImageStorageKind GetDdsStorageKind(byte[] data)
    {
        return DdsThumbnailDecoder.IsBlockCompressed(data)
            ? ImageStorageKind.DdsBlockCompressed
            : ImageStorageKind.DdsUncompressed;
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

    private static ImageStorageKind GetWorldDatStorageKind(byte[] data)
    {
        return LzmaAloneDecoder.IsCompressed(data)
            ? ImageStorageKind.LithTechWorldDatLzma
            : ImageStorageKind.LithTechWorldDat;
    }

    private static ImageStorageKind GetCrossFireDatStorageKind(byte[] data)
    {
        return LzmaAloneDecoder.IsCompressed(data)
            ? ImageStorageKind.CrossFireDatLzma
            : ImageStorageKind.CrossFireDat;
    }

    private static ImageStorageKind GetLithTechSpriteStorageKind(byte[] data)
    {
        return LzmaAloneDecoder.IsCompressed(data)
            ? ImageStorageKind.LithTechSpriteLzma
            : ImageStorageKind.LithTechSprite;
    }

    private static ImageStorageKind GetLithTechModelStorageKind(byte[] data)
    {
        return LzmaAloneDecoder.IsCompressed(data)
            ? ImageStorageKind.LithTechModelLzma
            : ImageStorageKind.LithTechModel;
    }

    private static string GetImageFormatLabel(ImageStorageKind storageKind)
    {
        return storageKind switch
        {
            ImageStorageKind.DtxLzmaCompressed or ImageStorageKind.DtxUncompressed => "DTX",
            ImageStorageKind.DdsBlockCompressed or ImageStorageKind.DdsUncompressed => "DDS",
            ImageStorageKind.TgaLzmaCompressed or ImageStorageKind.TgaUncompressed or ImageStorageKind.TgaInsertedFooterHeader or ImageStorageKind.TgaRawPixels => "TGA",
            ImageStorageKind.RasterLzmaCompressed or ImageStorageKind.RasterUncompressed => "Image",
            ImageStorageKind.LithTechModelLzma or ImageStorageKind.LithTechModel => "Model",
            ImageStorageKind.LithTechWorldDat or ImageStorageKind.LithTechWorldDatLzma or ImageStorageKind.CrossFireDat or ImageStorageKind.CrossFireDatLzma => "DAT",
            ImageStorageKind.LithTechSprite or ImageStorageKind.LithTechSpriteLzma => "SPR",
            ImageStorageKind.AudioUncompressed or ImageStorageKind.AudioLzmaCompressed => "Audio",
            ImageStorageKind.ResourceText or ImageStorageKind.ResourceTextLzma => "Resource",
            ImageStorageKind.FmodBank or ImageStorageKind.FmodBankLzma => "BANK",
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
