using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

internal static class LithTechModelTextureLoader
{
    private const int MaxTextureBytes = 64 * 1024 * 1024;
    private static readonly string[] TextureExtensions = [".dtx", ".dds", ".tga", ".png", ".jpg", ".jpeg", ".bmp"];
    private static readonly ConditionalWeakTable<RezArchive, Dictionary<string, RezFileNode>> ArchiveFileLookupCache = new();

    public static Func<string, ImageSource?>? CreateResolver(ExplorerItem item)
    {
        return item.Kind switch
        {
            ExplorerItemKind.RezFile when item.Archive is not null => CreateArchiveResolver(item.Archive),
            ExplorerItemKind.LocalFile when !string.IsNullOrWhiteSpace(item.SourcePath) => CreateLocalResolver(item.SourcePath),
            _ => null
        };
    }

    public static Func<string, ImageSource?>? CreateLocalResolver(string modelFilePath)
    {
        string? modelDirectory = Path.GetDirectoryName(modelFilePath);
        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            return null;
        }

        var cache = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
        return texturePath =>
        {
            string normalized = NormalizeTexturePath(texturePath);
            if (cache.TryGetValue(normalized, out ImageSource? cached))
            {
                return cached;
            }

            ImageSource? image = TryResolveLocalTexture(modelDirectory, normalized);
            cache[normalized] = image;
            return image;
        };
    }

    private static Func<string, ImageSource?> CreateArchiveResolver(RezArchive archive)
    {
        Dictionary<string, RezFileNode> filesByPath = GetArchiveFileLookup(archive);
        var cache = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
        return texturePath =>
        {
            string normalized = NormalizeTexturePath(texturePath);
            if (cache.TryGetValue(normalized, out ImageSource? cached))
            {
                return cached;
            }

            ImageSource? image = TryResolveArchiveTexture(archive, filesByPath, normalized);
            cache[normalized] = image;
            return image;
        };
    }

    private static ImageSource? TryResolveArchiveTexture(
        RezArchive archive,
        Dictionary<string, RezFileNode> filesByPath,
        string texturePath)
    {
        foreach (string candidate in GetTexturePathCandidates(texturePath))
        {
            if (filesByPath.TryGetValue(candidate, out RezFileNode? file))
            {
                return TryReadArchiveTexture(archive, file);
            }
        }

        foreach (string candidateName in GetTextureFileNameCandidates(texturePath))
        {
            RezFileNode? match = null;
            foreach ((string path, RezFileNode file) in filesByPath)
            {
                if (!PathMatchesFileName(path, candidateName))
                {
                    continue;
                }

                if (match is not null)
                {
                    match = null;
                    break;
                }

                match = file;
            }

            if (match is not null)
            {
                return TryReadArchiveTexture(archive, match);
            }
        }

        return null;
    }

    private static bool PathMatchesFileName(string path, string fileName)
    {
        return string.Equals(path, fileName, StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase);
    }

    private static ImageSource? TryResolveLocalTexture(string modelDirectory, string texturePath)
    {
        foreach (string candidate in GetLocalTexturePathCandidates(modelDirectory, texturePath))
        {
            if (File.Exists(candidate))
            {
                return TryReadLocalTexture(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> GetLocalTexturePathCandidates(string modelDirectory, string texturePath)
    {
        foreach (string candidate in GetTexturePathCandidates(texturePath))
        {
            string platformPath = candidate.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathFullyQualified(platformPath))
            {
                yield return platformPath;
                continue;
            }

            for (string? directory = modelDirectory; !string.IsNullOrWhiteSpace(directory); directory = Directory.GetParent(directory)?.FullName)
            {
                yield return Path.Combine(directory, platformPath);
            }
        }
    }

    private static IEnumerable<string> GetTexturePathCandidates(string texturePath)
    {
        string normalized = NormalizeTexturePath(texturePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        yield return normalized;

        string extension = Path.GetExtension(normalized);
        if (string.IsNullOrWhiteSpace(extension))
        {
            foreach (string textureExtension in TextureExtensions)
            {
                yield return normalized + textureExtension;
            }
        }
        else
        {
            string withoutExtension = normalized[..^extension.Length];
            foreach (string textureExtension in TextureExtensions)
            {
                if (!string.Equals(extension, textureExtension, StringComparison.OrdinalIgnoreCase))
                {
                    yield return withoutExtension + textureExtension;
                }
            }
        }
    }

    private static IEnumerable<string> GetTextureFileNameCandidates(string texturePath)
    {
        foreach (string candidate in GetTexturePathCandidates(texturePath))
        {
            string fileName = Path.GetFileName(candidate);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                yield return fileName;
            }
        }
    }

    private static ImageSource? TryReadArchiveTexture(RezArchive archive, RezFileNode file)
    {
        if (file.Size < 0 || file.Size > MaxTextureBytes)
        {
            return null;
        }

        try
        {
            byte[] data = new byte[file.Size];
            using FileStream source = File.OpenRead(archive.FilePath);
            source.Position = file.DataOffset;
            source.ReadExactly(data);
            return TryDecodeTexture(file.Extension, data);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryReadLocalTexture(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length < 0 || info.Length > MaxTextureBytes || info.Length > int.MaxValue)
            {
                return null;
            }

            byte[] data = File.ReadAllBytes(filePath);
            return TryDecodeTexture(info.Extension.TrimStart('.'), data);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryDecodeTexture(string extension, byte[] data)
    {
        if (string.Equals(extension, "dtx", StringComparison.OrdinalIgnoreCase))
        {
            return DtxThumbnailDecoder.TryDecodeOriginal(data);
        }

        if (string.Equals(extension, "dds", StringComparison.OrdinalIgnoreCase))
        {
            return DdsThumbnailDecoder.TryDecodeOriginal(data);
        }

        if (string.Equals(extension, "tga", StringComparison.OrdinalIgnoreCase))
        {
            return TgaThumbnailDecoder.TryDecodeOriginal(data);
        }

        return TryLoadBitmapImage(data);
    }

    private static ImageSource? TryLoadBitmapImage(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, RezFileNode> GetArchiveFileLookup(RezArchive archive)
    {
        return ArchiveFileLookupCache.GetValue(archive, static archiveValue =>
        {
            var filesByPath = new Dictionary<string, RezFileNode>(StringComparer.OrdinalIgnoreCase);
            AddArchiveFiles(archiveValue.Root, filesByPath);
            return filesByPath;
        });
    }

    private static void AddArchiveFiles(RezDirectoryNode directory, Dictionary<string, RezFileNode> filesByPath)
    {
        foreach (RezNode child in directory.Children)
        {
            if (child is RezFileNode file)
            {
                filesByPath.TryAdd(NormalizeTexturePath(file.FullPath), file);
            }
            else if (child is RezDirectoryNode childDirectory)
            {
                AddArchiveFiles(childDirectory, filesByPath);
            }
        }
    }

    private static string NormalizeTexturePath(string path)
    {
        return path
            .Trim()
            .Trim('"', '\'')
            .Replace('\\', '/')
            .TrimStart('/');
    }
}
