using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

internal static class CrossFireImageBinDecoder
{
    private const int HeaderLength = 4;
    private const byte XorKey = 0x8D;
    private const int ZstdImageHeaderLength = 16;
    private const uint ZstdMagic = 0xFD2FB528;
    private const int MaxZstdImageSide = 16384;
    private const long MaxZstdDecodedBytes = 256L * 1024 * 1024;
    private const int ThumbnailDecodeMaxSide = 192;
    private static readonly byte[] EncodedHeader = "CF10"u8.ToArray();

    private readonly record struct ImagePayload(byte[] Data, bool Encoded);

    public static bool IsCandidate(string extension)
    {
        return string.Equals(extension, "bin", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasEncodedHeader(byte[] data)
    {
        return HasEncodedHeader(data.AsSpan());
    }

    public static bool HasEncodedHeader(ReadOnlySpan<byte> data)
    {
        return data.StartsWith(EncodedHeader);
    }

    public static bool HasSupportedImageHeader(ReadOnlySpan<byte> data)
    {
        return TryReadZstdImageHeader(data, out _, out _, out _, out _);
    }

    public static bool TryDecodeThumbnail(byte[] data, out ImageSource? image, out ImageStorageKind storageKind)
    {
        foreach (ImagePayload payload in EnumeratePayloads(data))
        {
            if (TryDecodePayloadThumbnail(payload.Data, payload.Encoded, out image, out ImageStorageKind innerStorageKind))
            {
                storageKind = ResolveStorageKind(payload, innerStorageKind);
                return true;
            }
        }

        image = null;
        storageKind = ImageStorageKind.None;
        return false;
    }

    public static IReadOnlyList<ImagePreviewFrame> TryDecodePreviewFrames(byte[] data, out ImageStorageKind storageKind)
    {
        foreach (ImagePayload payload in EnumeratePayloads(data))
        {
            IReadOnlyList<ImagePreviewFrame> frames = TryDecodePayloadPreviewFrames(payload.Data, payload.Encoded, out ImageStorageKind innerStorageKind);
            if (frames.Count > 0)
            {
                storageKind = ResolveStorageKind(payload, innerStorageKind);
                return frames;
            }
        }

        storageKind = ImageStorageKind.None;
        return Array.Empty<ImagePreviewFrame>();
    }

    public static ImageSource? TryDecodeOriginal(byte[] data)
    {
        foreach (ImagePayload payload in EnumeratePayloads(data))
        {
            if (TryDecodePayloadPreviewFrames(payload.Data, payload.Encoded, out _).FirstOrDefault()?.Source is ImageSource image)
            {
                return image;
            }
        }

        return null;
    }

    public static bool TryWritePng(byte[] data, string outputPath, out ImageStorageKind storageKind)
    {
        try
        {
            IReadOnlyList<ImagePreviewFrame> frames = TryDecodePreviewFrames(data, out storageKind);
            if (frames.FirstOrDefault()?.Source is not BitmapSource bitmap)
            {
                return false;
            }

            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream output = File.Create(outputPath);
            encoder.Save(output);
            return true;
        }
        catch
        {
            storageKind = ImageStorageKind.None;
            return false;
        }
    }

    public static string? GetStorageDescription(ImageStorageKind storageKind)
    {
        return storageKind switch
        {
            ImageStorageKind.CrossFireImageBin => "BIN - CF10/XOR image",
            ImageStorageKind.CrossFireImageBinLzma => "BIN - LZMA-wrapped image",
            ImageStorageKind.CrossFireImageBinZstd => "BIN - Zstandard BGRA image",
            _ => null
        };
    }

    private static IEnumerable<ImagePayload> EnumeratePayloads(byte[] data)
    {
        if (TryDecodeEncodedPayload(data, out byte[]? payload) && payload is not null)
        {
            yield return new ImagePayload(payload, Encoded: true);
            yield break;
        }

        yield return new ImagePayload(data, Encoded: false);
    }

    private static bool TryDecodeEncodedPayload(byte[] data, out byte[]? payload)
    {
        payload = null;
        if (!HasEncodedHeader(data) || data.Length <= HeaderLength)
        {
            return false;
        }

        payload = new byte[data.Length - HeaderLength];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(data[i + HeaderLength] ^ XorKey);
        }

        return true;
    }

    private static bool TryDecodePayloadThumbnail(
        byte[] data,
        bool allowTgaRepair,
        out ImageSource? image,
        out ImageStorageKind storageKind)
    {
        if (TryDecodeLzmaWrappedImage(data, decodeThumbnail: true, out image, out storageKind) ||
            TryDecodeZstdImage(data, decodeThumbnail: true, out image, out storageKind) ||
            TryDecodeDds(data, decodeThumbnail: true, out image, out storageKind) ||
            TryDecodeDtx(data, thumbnail: true, out image, out storageKind) ||
            TryDecodeRaster(data, decodeThumbnail: true, out image, out storageKind) ||
            (ShouldTryTga(data, allowTgaRepair) && TryDecodeTga(data, thumbnail: true, out image, out storageKind)))
        {
            return true;
        }

        image = null;
        storageKind = ImageStorageKind.None;
        return false;
    }

    private static IReadOnlyList<ImagePreviewFrame> TryDecodePayloadPreviewFrames(
        byte[] data,
        bool allowTgaRepair,
        out ImageStorageKind storageKind)
    {
        if (TryDecodeLzmaWrappedImage(data, decodeThumbnail: false, out ImageSource? lzmaImage, out storageKind))
        {
            return CreatePreviewFrames(lzmaImage);
        }

        if (TryDecodeZstdImage(data, decodeThumbnail: false, out ImageSource? zstdImage, out storageKind))
        {
            return CreatePreviewFrames(zstdImage);
        }

        if (TryDecodeDds(data, decodeThumbnail: false, out ImageSource? ddsImage, out storageKind))
        {
            return CreatePreviewFrames(ddsImage);
        }

        if (TryDecodeDtx(data, thumbnail: false, out ImageSource? dtxImage, out storageKind))
        {
            return CreatePreviewFrames(dtxImage);
        }

        if (TryDecodeRaster(data, decodeThumbnail: false, out ImageSource? rasterImage, out storageKind))
        {
            return CreatePreviewFrames(rasterImage);
        }

        if (ShouldTryTga(data, allowTgaRepair))
        {
            IReadOnlyList<ImagePreviewFrame> tgaFrames = TgaThumbnailDecoder.TryDecodePreviewFrames(data);
            if (tgaFrames.Count > 0)
            {
                storageKind = GetTgaStorageKind(data);
                return tgaFrames;
            }
        }

        storageKind = ImageStorageKind.None;
        return Array.Empty<ImagePreviewFrame>();
    }

    private static bool TryDecodeLzmaWrappedImage(
        byte[] data,
        bool decodeThumbnail,
        out ImageSource? image,
        out ImageStorageKind storageKind)
    {
        image = null;
        storageKind = ImageStorageKind.None;
        if (!LzmaAloneDecoder.IsCompressed(data))
        {
            return false;
        }

        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data, MaxZstdDecodedBytes);
        if (prepared is null)
        {
            return false;
        }

        if (TryDecodeZstdImage(prepared, decodeThumbnail, out image, out _) ||
            TryDecodeEncodedPayload(prepared, out byte[]? encodedPayload) &&
            encodedPayload is not null &&
            TryDecodeZstdImage(encodedPayload, decodeThumbnail, out image, out _))
        {
            storageKind = ImageStorageKind.CrossFireImageBinLzma;
            return true;
        }

        image = null;
        return false;
    }

    private static bool TryDecodeZstdImage(
        byte[] data,
        bool decodeThumbnail,
        out ImageSource? image,
        out ImageStorageKind storageKind)
    {
        image = null;
        storageKind = ImageStorageKind.None;
        if (!TryReadZstdImageHeader(
                data,
                out int width,
                out int height,
                out int bytesPerPixel,
                out int compressedByteCount))
        {
            return false;
        }

        long expectedByteCount = (long)width * height * bytesPerPixel;
        if (expectedByteCount <= 0 ||
            expectedByteCount > MaxZstdDecodedBytes ||
            expectedByteCount > int.MaxValue)
        {
            return false;
        }

        try
        {
            using var compressed = new MemoryStream(data, ZstdImageHeaderLength, compressedByteCount, writable: false);
            using var zstd = new SharpCompress.Compressors.ZStandard.DecompressionStream(
                compressed,
                bufferSize: 0,
                checkEndOfStream: true,
                leaveOpen: false);
            using var output = new MemoryStream((int)expectedByteCount);
            zstd.CopyTo(output);
            if (output.Length != expectedByteCount)
            {
                return false;
            }

            byte[] pixels = output.ToArray();
            var bitmap = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                width * bytesPerPixel);
            bitmap.Freeze();
            image = decodeThumbnail ? ScaleThumbnail(bitmap) : bitmap;
            storageKind = ImageStorageKind.CrossFireImageBinZstd;
            return true;
        }
        catch
        {
            image = null;
            storageKind = ImageStorageKind.None;
            return false;
        }
    }

    private static bool TryReadZstdImageHeader(
        byte[] data,
        out int width,
        out int height,
        out int bytesPerPixel,
        out int compressedByteCount)
    {
        return TryReadZstdImageHeader(data.AsSpan(), out width, out height, out bytesPerPixel, out compressedByteCount);
    }

    private static bool TryReadZstdImageHeader(
        ReadOnlySpan<byte> data,
        out int width,
        out int height,
        out int bytesPerPixel,
        out int compressedByteCount)
    {
        width = 0;
        height = 0;
        bytesPerPixel = 0;
        compressedByteCount = 0;
        if (data.Length < ZstdImageHeaderLength + sizeof(uint))
        {
            return false;
        }

        uint rawWidth = BinaryPrimitives.ReadUInt32LittleEndian(data[..sizeof(uint)]);
        uint rawHeight = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, sizeof(uint)));
        uint rawBytesPerPixel = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, sizeof(uint)));
        uint rawCompressedByteCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12, sizeof(uint)));
        if (rawWidth == 0 ||
            rawHeight == 0 ||
            rawWidth > MaxZstdImageSide ||
            rawHeight > MaxZstdImageSide ||
            rawBytesPerPixel != 4 ||
            rawCompressedByteCount == 0 ||
            rawCompressedByteCount > int.MaxValue ||
            rawCompressedByteCount > data.Length - ZstdImageHeaderLength)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(ZstdImageHeaderLength, sizeof(uint)));
        if (magic != ZstdMagic)
        {
            return false;
        }

        width = checked((int)rawWidth);
        height = checked((int)rawHeight);
        bytesPerPixel = checked((int)rawBytesPerPixel);
        compressedByteCount = checked((int)rawCompressedByteCount);
        return true;
    }

    private static bool TryDecodeDds(byte[] data, bool decodeThumbnail, out ImageSource? image, out ImageStorageKind storageKind)
    {
        image = DdsThumbnailDecoder.TryDecodeOriginal(data);
        byte[] storageData = data;
        if (image is null &&
            LzmaAloneDecoder.IsCompressed(data) &&
            LzmaAloneDecoder.TryPrepareData(data) is byte[] prepared)
        {
            image = DdsThumbnailDecoder.TryDecodeOriginal(prepared);
            storageData = prepared;
        }

        if (image is null)
        {
            storageKind = ImageStorageKind.None;
            return false;
        }

        if (decodeThumbnail)
        {
            image = ScaleThumbnail(image);
        }

        storageKind = DdsThumbnailDecoder.IsBlockCompressed(storageData)
            ? ImageStorageKind.DdsBlockCompressed
            : ImageStorageKind.DdsUncompressed;
        return true;
    }

    private static bool TryDecodeDtx(byte[] data, bool thumbnail, out ImageSource? image, out ImageStorageKind storageKind)
    {
        image = thumbnail
            ? DtxThumbnailDecoder.TryDecode(data)
            : DtxThumbnailDecoder.TryDecodeOriginal(data);
        storageKind = image is null
            ? ImageStorageKind.None
            : GetDtxStorageKind(data);
        return image is not null;
    }

    private static bool TryDecodeTga(byte[] data, bool thumbnail, out ImageSource? image, out ImageStorageKind storageKind)
    {
        image = thumbnail
            ? TgaThumbnailDecoder.TryDecode(data)
            : TgaThumbnailDecoder.TryDecodeOriginal(data);
        storageKind = image is null
            ? ImageStorageKind.None
            : GetTgaStorageKind(data);
        return image is not null;
    }

    private static bool TryDecodeRaster(byte[] data, bool decodeThumbnail, out ImageSource? image, out ImageStorageKind storageKind)
    {
        try
        {
            if (!TryPrepareRasterImageData(data, out byte[]? imageData, out storageKind) ||
                imageData is null)
            {
                image = null;
                return false;
            }

            image = LoadBitmapImage(imageData, decodeThumbnail);
            return true;
        }
        catch
        {
            image = null;
            storageKind = ImageStorageKind.None;
            return false;
        }
    }

    private static bool ShouldTryTga(byte[] data, bool allowRepair)
    {
        return allowRepair || LzmaAloneDecoder.IsCompressed(data) || LooksLikeTgaHeader(data);
    }

    private static bool LooksLikeTgaHeader(byte[] data)
    {
        if (data.Length < 18)
        {
            return false;
        }

        int colorMapType = data[1];
        int imageType = data[2];
        int width = data[12] | (data[13] << 8);
        int height = data[14] | (data[15] << 8);
        int pixelDepth = data[16];
        return colorMapType is 0 or 1 &&
               imageType is 1 or 2 or 3 or 9 or 10 or 11 &&
               width > 0 &&
               height > 0 &&
               width <= 16384 &&
               height <= 16384 &&
               (long)width * height <= 4096L * 4096L &&
               pixelDepth is 8 or 15 or 16 or 24 or 32;
    }

    private static bool TryPrepareRasterImageData(byte[] data, out byte[]? imageData, out ImageStorageKind storageKind)
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
        try
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
        catch
        {
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

    private static ImageSource ScaleThumbnail(ImageSource image)
    {
        if (image is not BitmapSource bitmap)
        {
            return image;
        }

        double scale = Math.Min(1.0, (double)ThumbnailDecodeMaxSide / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        if (scale >= 1.0)
        {
            return bitmap;
        }

        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static IReadOnlyList<ImagePreviewFrame> CreatePreviewFrames(ImageSource? imageSource)
    {
        return imageSource is null
            ? Array.Empty<ImagePreviewFrame>()
            : new[] { new ImagePreviewFrame("Original", imageSource) };
    }

    private static ImageStorageKind ResolveStorageKind(ImagePayload payload, ImageStorageKind innerStorageKind)
    {
        if (innerStorageKind == ImageStorageKind.CrossFireImageBinZstd)
        {
            return ImageStorageKind.CrossFireImageBinZstd;
        }

        if (!payload.Encoded)
        {
            return innerStorageKind;
        }

        return IsLzmaStorageKind(innerStorageKind) || LzmaAloneDecoder.IsCompressed(payload.Data)
            ? ImageStorageKind.CrossFireImageBinLzma
            : ImageStorageKind.CrossFireImageBin;
    }

    private static bool IsLzmaStorageKind(ImageStorageKind storageKind)
    {
        return storageKind is ImageStorageKind.DtxLzmaCompressed or
               ImageStorageKind.TgaLzmaCompressed or
               ImageStorageKind.RasterLzmaCompressed;
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
}
