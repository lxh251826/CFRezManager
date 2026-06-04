using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

internal sealed record CachedThumbnail(ImageSource Source, ImageStorageKind StorageKind);

internal sealed record ThumbnailCacheClearResult(string CacheDirectory, int DeletedFileCount, long DeletedByteCount);

internal static class ThumbnailDiskCache
{
    private const string CacheVersion = "v1";
    private const string ImageExtension = ".png";
    private const string MetadataExtension = ".kind";

    private static readonly string CacheDirectory = Path.Combine(
        GetLocalApplicationDataPath(),
        "CFRezManager",
        "ThumbnailCache",
        CacheVersion);

    public static bool TryLoad(ExplorerItem item, out CachedThumbnail? thumbnail)
    {
        thumbnail = null;

        try
        {
            if (!TryBuildCacheKey(item, out string? key))
            {
                return false;
            }

            string imagePath = Path.Combine(CacheDirectory, key + ImageExtension);
            string metadataPath = Path.Combine(CacheDirectory, key + MetadataExtension);
            if (!File.Exists(imagePath) || !File.Exists(metadataPath))
            {
                return false;
            }

            string metadata = File.ReadAllText(metadataPath, Encoding.UTF8).Trim();
            if (!Enum.TryParse(metadata, ignoreCase: false, out ImageStorageKind storageKind))
            {
                return false;
            }

            using FileStream stream = File.OpenRead(imagePath);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                return false;
            }

            BitmapFrame frame = decoder.Frames[0];
            if (frame.CanFreeze)
            {
                frame.Freeze();
            }

            thumbnail = new CachedThumbnail(frame, storageKind);
            return true;
        }
        catch
        {
            thumbnail = null;
            return false;
        }
    }

    public static bool TrySave(ExplorerItem item, ImageSource source, ImageStorageKind storageKind)
    {
        try
        {
            if (!TryBuildCacheKey(item, out string? key) ||
                !TryGetBitmapSource(source, out BitmapSource? bitmap))
            {
                return false;
            }

            Directory.CreateDirectory(CacheDirectory);

            string imagePath = Path.Combine(CacheDirectory, key + ImageExtension);
            string metadataPath = Path.Combine(CacheDirectory, key + MetadataExtension);
            string imageTempPath = imagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string metadataTempPath = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using (FileStream stream = File.Create(imageTempPath))
                {
                    encoder.Save(stream);
                }

                File.WriteAllText(metadataTempPath, storageKind.ToString(), Encoding.UTF8);
                File.Move(imageTempPath, imagePath, overwrite: true);
                File.Move(metadataTempPath, metadataPath, overwrite: true);
                return true;
            }
            finally
            {
                TryDelete(imageTempPath);
                TryDelete(metadataTempPath);
            }
        }
        catch
        {
            return false;
        }
    }

    public static ThumbnailCacheClearResult Clear()
    {
        string cacheDirectory = Path.GetFullPath(CacheDirectory);
        string cacheRoot = Path.GetFullPath(Path.Combine(GetLocalApplicationDataPath(), "CFRezManager", "ThumbnailCache"));
        string expectedPrefix = cacheRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        if (!cacheDirectory.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to clear an unexpected thumbnail cache directory.");
        }

        if (!Directory.Exists(cacheDirectory))
        {
            return new ThumbnailCacheClearResult(cacheDirectory, 0, 0);
        }

        int deletedFileCount = 0;
        long deletedByteCount = 0;
        foreach (string filePath in Directory.EnumerateFiles(cacheDirectory, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(filePath);
            long length = info.Exists ? info.Length : 0;
            File.Delete(filePath);
            deletedFileCount++;
            deletedByteCount += length;
        }

        foreach (string directoryPath in Directory
                     .EnumerateDirectories(cacheDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath);
            }
        }

        if (Directory.Exists(cacheDirectory))
        {
            Directory.Delete(cacheDirectory);
        }

        return new ThumbnailCacheClearResult(cacheDirectory, deletedFileCount, deletedByteCount);
    }

    private static bool TryBuildCacheKey(ExplorerItem item, out string? key)
    {
        key = null;
        string extension = item.FileExtension.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        string descriptor;
        if (item.Kind == ExplorerItemKind.LocalFile)
        {
            if (string.IsNullOrWhiteSpace(item.SourcePath))
            {
                return false;
            }

            var info = new FileInfo(item.SourcePath);
            if (!info.Exists)
            {
                return false;
            }

            descriptor = string.Join(
                "|",
                CacheVersion,
                "local",
                NormalizePath(info.FullName),
                info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                extension);
        }
        else if (item.Kind == ExplorerItemKind.RezFile &&
                 item.Archive is not null &&
                 item.ArchiveFile is not null)
        {
            var archiveInfo = new FileInfo(item.Archive.FilePath);
            if (!archiveInfo.Exists)
            {
                return false;
            }

            descriptor = string.Join(
                "|",
                CacheVersion,
                "rez",
                NormalizePath(archiveInfo.FullName),
                archiveInfo.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                archiveInfo.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.ArchiveFile.FullPath,
                item.ArchiveFile.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.ArchiveFile.DataOffset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.ArchiveFile.Time.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.ArchiveFile.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.ArchiveFile.Md5,
                extension);
        }
        else
        {
            return false;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(descriptor));
        key = Convert.ToHexString(hash).ToLowerInvariant();
        return true;
    }

    private static bool TryGetBitmapSource(ImageSource source, out BitmapSource? bitmap)
    {
        if (source is BitmapSource bitmapSource)
        {
            bitmap = bitmapSource;
            return true;
        }

        bitmap = null;
        return false;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }
        catch
        {
            return path.Trim().ToUpperInvariant();
        }
    }

    private static string GetLocalApplicationDataPath()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(path)
            ? Path.GetTempPath()
            : path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
