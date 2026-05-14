using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

internal static class PreviewTool
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "bmp",
        "dds",
        "dtx",
        "gif",
        "jpg",
        "jpeg",
        "png",
        "tga",
        "tif",
        "tiff"
    };

    public static bool TryGetPreviewPath(string[] args, out string filePath)
    {
        filePath = string.Empty;
        if (args.Length == 0)
        {
            return false;
        }

        string candidate = args[0];
        if (string.Equals(candidate, "--preview", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, "/preview", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                return false;
            }

            candidate = args[1];
        }

        if (!File.Exists(candidate))
        {
            return false;
        }

        filePath = Path.GetFullPath(candidate);
        return true;
    }

    public static bool IsPreviewInvocation(string[] args)
    {
        return args.Length > 0 &&
               (string.Equals(args[0], "--preview", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[0], "/preview", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(args[0]));
    }

    public static bool IsSupported(string fileName, string extension)
    {
        return LithTechModelDecoder.IsCandidate(extension) ||
               ImageExtensions.Contains(extension) ||
               EncTextDecoder.IsCandidate(fileName, extension) ||
               TextPreviewDecoder.IsPlainTextExtension(extension);
    }

    public static bool TryCreateWindow(string filePath, out Window? window, out string? errorMessage)
    {
        window = null;
        errorMessage = null;

        try
        {
            string fileName = Path.GetFileName(filePath);
            string extension = Path.GetExtension(filePath).TrimStart('.');
            byte[] data = File.ReadAllBytes(filePath);

            if (LithTechModelDecoder.IsCandidate(extension))
            {
                if (LithTechModelDecoder.TryDecode(data, fileName, extension, out LithTechModelDocument? modelDocument, out string? modelError) &&
                    modelDocument is not null)
                {
                    window = new ModelPreviewWindow(fileName, modelDocument, FormatModelInfo(modelDocument));
                    window.ShowInTaskbar = true;
                    return true;
                }

                errorMessage = string.IsNullOrWhiteSpace(modelError)
                    ? $"无法解码模型: {fileName}"
                    : modelError;
                return false;
            }

            if (ImageExtensions.Contains(extension) &&
                TryCreateImageWindow(fileName, extension, data, out window))
            {
                window!.ShowInTaskbar = true;
                return true;
            }

            if (TryCreateTextWindow(fileName, extension, data, out window))
            {
                window!.ShowInTaskbar = true;
                return true;
            }

            errorMessage = $"无法预览此文件: {fileName}";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool TryCreateImageWindow(string fileName, string extension, byte[] data, out Window? window)
    {
        window = null;
        IReadOnlyList<ImagePreviewFrame> frames;
        string? info = null;

        if (string.Equals(extension, "dtx", StringComparison.OrdinalIgnoreCase))
        {
            ImageSource? image = DtxThumbnailDecoder.TryDecodeOriginal(data);
            frames = image is null ? [] : new[] { new ImagePreviewFrame("Original", image) };
            info = DtxThumbnailDecoder.IsLzmaCompressed(data) ? "DTX - LZMA compressed" : "DTX - uncompressed";
        }
        else if (string.Equals(extension, "tga", StringComparison.OrdinalIgnoreCase))
        {
            frames = TgaThumbnailDecoder.TryDecodePreviewFrames(data);
            info = FormatTgaInfo(data);
        }
        else if (string.Equals(extension, "dds", StringComparison.OrdinalIgnoreCase))
        {
            frames = DdsThumbnailDecoder.TryDecode(data, 0, out ImageSource? image, out _)
                ? new[] { new ImagePreviewFrame("Original", image!) }
                : [];
            info = "DDS";
        }
        else
        {
            if (TryLoadBitmapImage(data, out ImageSource? image) && image is not null)
            {
                frames = new[] { new ImagePreviewFrame("Original", image) };
            }
            else if (string.Equals(extension, "png", StringComparison.OrdinalIgnoreCase))
            {
                frames = TgaThumbnailDecoder.TryDecodePreviewFrames(data);
                info = frames.Count > 0 ? $"PNG - {FormatTgaInfo(data)}" : null;
            }
            else
            {
                frames = [];
            }
        }

        if (frames.Count == 0)
        {
            return false;
        }

        window = new ImagePreviewWindow(fileName, frames, info);
        return true;
    }

    private static bool TryCreateTextWindow(string fileName, string extension, byte[] data, out Window? window)
    {
        window = null;
        if (EncTextDecoder.IsCandidate(fileName, extension))
        {
            if (!EncTextDecoder.TryDecode(data, out byte[] decodedBytes) ||
                !TextPreviewDecoder.TryDecode(decodedBytes, preferKorean: true, out string decodedText, out string decodedEncoding))
            {
                return false;
            }

            string info = $"ENC / Base64 / {decodedEncoding}, {data.Length:N0} bytes -> {decodedBytes.Length:N0} bytes";
            window = new TextPreviewWindow(fileName, decodedText, info);
            return true;
        }

        if (!TextPreviewDecoder.IsPlainTextExtension(extension) ||
            !TextPreviewDecoder.TryDecode(data, preferKorean: false, out string text, out string encoding))
        {
            return false;
        }

        window = new TextPreviewWindow(fileName, text, $"{encoding}, {data.Length:N0} bytes");
        return true;
    }

    private static ImageSource LoadBitmapImage(byte[] data)
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

    private static bool TryLoadBitmapImage(byte[] data, out ImageSource? image)
    {
        try
        {
            image = LoadBitmapImage(data);
            return true;
        }
        catch
        {
            image = null;
            return false;
        }
    }

    private static string FormatTgaInfo(byte[] data)
    {
        if (TgaThumbnailDecoder.IsLzmaCompressed(data))
        {
            return "TGA - LZMA compressed";
        }

        return TgaThumbnailDecoder.HasInsertedFooterHeader(data)
            ? "TGA - repaired layout"
            : TgaThumbnailDecoder.HasRawPixelData(data)
                ? "TGA - repaired raw pixels"
                : "TGA - uncompressed";
    }

    private static string FormatModelInfo(LithTechModelDocument document)
    {
        return $"{document.StorageDescription}, {document.Meshes.Count:N0} meshes, {document.VertexCount:N0} vertices, {document.TriangleCount:N0} triangles";
    }
}
