using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

internal static class TgaThumbnailDecoder
{
    private const int HeaderLength = 18;
    private const int MaxDecodedPixels = 4096 * 4096;
    private const int ThumbnailMaxSide = 192;
    private static readonly byte[] FooterSignature = "TRUEVISION-XFILE"u8.ToArray();
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] DdsSignature = "DDS "u8.ToArray();

    private const int ImageTypeColorMapped = 1;
    private const int ImageTypeTrueColor = 2;
    private const int ImageTypeGrayscale = 3;
    private const int ImageTypeRleColorMapped = 9;
    private const int ImageTypeRleTrueColor = 10;
    private const int ImageTypeRleGrayscale = 11;

    private readonly record struct TgaHeader(
        int ColorMapType,
        int ImageType,
        int ColorMapFirstEntry,
        int ColorMapLength,
        int ColorMapEntryBits,
        int Width,
        int Height,
        int PixelDepth,
        int Descriptor,
        int ColorMapOffset,
        int ImageDataOffset);

    public static ImageSource? TryDecode(byte[] data)
    {
        return TryDecode(data, ThumbnailMaxSide);
    }

    public static ImageSource? TryDecodeOriginal(byte[] data)
    {
        return TryDecode(data, maxSide: null);
    }

    public static IReadOnlyList<ImagePreviewFrame> TryDecodePreviewFrames(byte[] data)
    {
        byte[]? tgaData = LzmaAloneDecoder.TryPrepareData(data);
        if (tgaData is null)
        {
            return Array.Empty<ImagePreviewFrame>();
        }

        var frames = new List<ImagePreviewFrame>();
        ImageSource? original = TryDecodePrepared(tgaData, maxSide: null);
        if (original is not null)
        {
            frames.Add(new ImagePreviewFrame("Original", original));
        }

        AddEmbeddedPngFrames(tgaData, frames);
        AddEmbeddedDdsFrames(tgaData, frames);
        AddEmbeddedTgaFrames(tgaData, frames);
        return frames;
    }

    public static bool IsLzmaCompressed(byte[] data)
    {
        return LzmaAloneDecoder.IsCompressed(data);
    }

    public static bool HasInsertedFooterHeader(byte[] data)
    {
        byte[]? tgaData = LzmaAloneDecoder.TryPrepareData(data);
        return tgaData is not null &&
               !TryReadHeader(tgaData, out _) &&
               TryRepairInsertedFooterHeader(tgaData) is not null;
    }

    public static bool HasRawPixelData(byte[] data)
    {
        byte[]? tgaData = LzmaAloneDecoder.TryPrepareData(data);
        return tgaData is not null &&
               !TryReadHeader(tgaData, out _) &&
               TryRepairInsertedFooterHeader(tgaData) is null &&
               TryBuildRawPixelTga(tgaData) is not null;
    }

    private static ImageSource? TryDecode(byte[] data, int? maxSide)
    {
        byte[]? tgaData = LzmaAloneDecoder.TryPrepareData(data);
        if (tgaData is null)
        {
            return null;
        }

        return TryDecodePrepared(tgaData, maxSide);
    }

    private static ImageSource? TryDecodePrepared(byte[] tgaData, int? maxSide)
    {
        if (!TryReadHeader(tgaData, out TgaHeader header))
        {
            byte[]? repairedTga = TryRepairInsertedFooterHeader(tgaData) ?? TryBuildRawPixelTga(tgaData);
            if (repairedTga is null || !TryReadHeader(repairedTga, out header))
            {
                return null;
            }

            tgaData = repairedTga;
        }

        if (!TryReadPalette(tgaData, header, out byte[]? palette))
        {
            return null;
        }

        int sourcePixelBytes = GetSourcePixelBytes(header);
        if (sourcePixelBytes <= 0)
        {
            return null;
        }

        var pixels = new byte[checked(header.Width * header.Height * 4)];
        bool decoded = IsRleType(header.ImageType)
            ? DecodeRle(tgaData, header, palette, sourcePixelBytes, pixels)
            : DecodeUncompressed(tgaData, header, palette, sourcePixelBytes, pixels);

        return decoded ? CreateImage(header.Width, header.Height, pixels, maxSide) : null;
    }

    private static void AddEmbeddedPngFrames(byte[] data, List<ImagePreviewFrame> frames)
    {
        int searchOffset = 0;
        int count = 0;
        while (searchOffset < data.Length)
        {
            int relativeOffset = data.AsSpan(searchOffset).IndexOf(PngSignature);
            if (relativeOffset < 0)
            {
                return;
            }

            int pngOffset = searchOffset + relativeOffset;
            int pngEnd = FindPngEnd(data, pngOffset);
            if (pngEnd > pngOffset)
            {
                ImageSource? image = TryLoadBitmapFrame(data.AsSpan(pngOffset, pngEnd - pngOffset).ToArray());
                if (image is not null)
                {
                    frames.Add(new ImagePreviewFrame($"Embedded PNG {++count}", image));
                    searchOffset = pngEnd;
                    continue;
                }
            }

            searchOffset = pngOffset + 1;
        }
    }

    private static void AddEmbeddedDdsFrames(byte[] data, List<ImagePreviewFrame> frames)
    {
        int searchOffset = 0;
        int count = 0;
        while (searchOffset < data.Length)
        {
            int relativeOffset = data.AsSpan(searchOffset).IndexOf(DdsSignature);
            if (relativeOffset < 0)
            {
                return;
            }

            int ddsOffset = searchOffset + relativeOffset;
            if (DdsThumbnailDecoder.TryDecode(data, ddsOffset, out ImageSource? image, out int byteCount) && image is not null)
            {
                frames.Add(new ImagePreviewFrame($"Embedded DDS {++count}", image));
                searchOffset = ddsOffset + Math.Max(byteCount, DdsSignature.Length);
                continue;
            }

            searchOffset = ddsOffset + 1;
        }
    }

    private static void AddEmbeddedTgaFrames(byte[] data, List<ImagePreviewFrame> frames)
    {
        int count = 0;
        for (int offset = 1; offset <= data.Length - HeaderLength; offset++)
        {
            if (!LooksLikeEmbeddedTgaHeader(data, offset))
            {
                continue;
            }

            byte[] tail = data.AsSpan(offset).ToArray();
            if (!TryReadHeader(tail, out TgaHeader header) || IsRleType(header.ImageType))
            {
                continue;
            }

            int sourcePixelBytes = GetSourcePixelBytes(header);
            long pixelBytes = (long)header.Width * header.Height * sourcePixelBytes;
            long frameBytes = header.ImageDataOffset + pixelBytes;
            if (sourcePixelBytes <= 0 ||
                frameBytes > int.MaxValue ||
                !HasBytes(data, offset, (int)frameBytes))
            {
                continue;
            }

            byte[] tgaBytes = data.AsSpan(offset, (int)frameBytes).ToArray();
            ImageSource? image = TryDecodePrepared(tgaBytes, maxSide: null);
            if (image is null)
            {
                continue;
            }

            frames.Add(new ImagePreviewFrame($"Embedded TGA {++count}", image));
            offset += (int)frameBytes - 1;
        }
    }

    private static bool LooksLikeEmbeddedTgaHeader(byte[] data, int offset)
    {
        if (!HasBytes(data, offset, HeaderLength))
        {
            return false;
        }

        int colorMapType = data[offset + 1];
        int imageType = data[offset + 2];
        int width = ReadUInt16(data, offset + 12);
        int height = ReadUInt16(data, offset + 14);
        int pixelDepth = data[offset + 16];
        return colorMapType == 0 &&
               imageType == ImageTypeTrueColor &&
               pixelDepth is 24 or 32 &&
               width >= 16 &&
               height >= 16 &&
               IsSafeImageSize(width, height);
    }

    private static int FindPngEnd(byte[] data, int pngOffset)
    {
        int cursor = pngOffset + PngSignature.Length;
        while (HasBytes(data, cursor, 8))
        {
            uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor, 4));
            int chunkTypeOffset = cursor + 4;
            long nextChunkOffset = (long)chunkTypeOffset + 4 + chunkLength + 4;
            if (nextChunkOffset > data.Length)
            {
                return -1;
            }

            bool isEnd = data.AsSpan(chunkTypeOffset, 4).SequenceEqual("IEND"u8);
            cursor = (int)nextChunkOffset;
            if (isEnd)
            {
                return cursor;
            }
        }

        return -1;
    }

    private static ImageSource? TryLoadBitmapFrame(byte[] data)
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

    private static byte[]? TryRepairInsertedFooterHeader(byte[] data)
    {
        byte[]? bestRepair = null;
        int searchOffset = 0;
        while (searchOffset < data.Length)
        {
            int signatureOffset = data.AsSpan(searchOffset).IndexOf(FooterSignature);
            if (signatureOffset < 0)
            {
                return bestRepair;
            }

            signatureOffset += searchOffset;
            int footerOffset = signatureOffset - 8;
            int headerOffset = footerOffset + 26;
            if (footerOffset >= 0 &&
                headerOffset + HeaderLength <= data.Length &&
                HasTgaFooterTerminator(data, signatureOffset) &&
                TryBuildRepairedTga(data, footerOffset, headerOffset, out byte[]? repaired))
            {
                bestRepair = repaired;
            }

            searchOffset = signatureOffset + 1;
        }

        return bestRepair;
    }

    private static byte[]? TryBuildRawPixelTga(byte[] data)
    {
        return TryBuildLikelyTripletRawTga(data) ??
               TryBuildRawPixelTga(data, data.Length, 4) ??
               TryBuildRawPixelTga(data, data.Length, 3) ??
               TryBuildRawPixelTga(data, data.Length, 1) ??
               (data.Length > 44
                   ? TryBuildRawPixelTga(data, data.Length - 44, 4) ??
                     TryBuildRawPixelTga(data, data.Length - 44, 3) ??
                     TryBuildRawPixelTga(data, data.Length - 44, 1)
                   : null);
    }

    private static byte[]? TryBuildRawPixelTga(byte[] data, int pixelBytes, int sourcePixelBytes)
    {
        if (!TryInferSquareDimensions(pixelBytes, sourcePixelBytes, out int side) ||
            !IsSafeImageSize(side, side))
        {
            return null;
        }

        var repaired = new byte[HeaderLength + pixelBytes];
        repaired[2] = (byte)(sourcePixelBytes == 1 ? ImageTypeGrayscale : ImageTypeTrueColor);
        WriteUInt16(repaired, 12, side);
        WriteUInt16(repaired, 14, side);
        repaired[16] = (byte)(sourcePixelBytes * 8);
        repaired[17] = 0x20;
        if (sourcePixelBytes == 4)
        {
            repaired[17] = 0x28;
        }

        data.AsSpan(0, pixelBytes).CopyTo(repaired.AsSpan(HeaderLength));
        return repaired;
    }

    private static byte[]? TryBuildLikelyTripletRawTga(byte[] data)
    {
        const int sourcePixelBytes = 3;
        if (data.Length < 2048 * 16 * sourcePixelBytes ||
            data.Length % sourcePixelBytes != 0 ||
            !HasConstantTripletChannels(data))
        {
            return null;
        }

        int pixelCount = data.Length / sourcePixelBytes;
        foreach (int width in new[] { 2048, 1024, 512, 256, 128 })
        {
            int height = pixelCount / width;
            int leftoverPixels = pixelCount - (width * height);
            if (height < 16 ||
                leftoverPixels >= width * 3 / 4 ||
                !IsSafeImageSize(width, height))
            {
                continue;
            }

            int pixelBytes = width * height * sourcePixelBytes;
            var repaired = new byte[HeaderLength + pixelBytes];
            repaired[2] = ImageTypeTrueColor;
            WriteUInt16(repaired, 12, width);
            WriteUInt16(repaired, 14, height);
            repaired[16] = 24;
            repaired[17] = 0x20;
            data.AsSpan(0, pixelBytes).CopyTo(repaired.AsSpan(HeaderLength));
            return repaired;
        }

        return null;
    }

    private static bool HasConstantTripletChannels(byte[] data)
    {
        const int sampleTriplets = 4096;
        int tripletCount = Math.Min(data.Length / 3, sampleTriplets);
        Span<int> channel0 = stackalloc int[256];
        Span<int> channel1 = stackalloc int[256];
        Span<int> channel2 = stackalloc int[256];
        for (int i = 0, offset = 0; i < tripletCount; i++, offset += 3)
        {
            channel0[data[offset]]++;
            channel1[data[offset + 1]]++;
            channel2[data[offset + 2]]++;
        }

        int dominant0 = GetDominantCount(channel0, out int value0);
        int dominant1 = GetDominantCount(channel1, out int value1);
        int dominant2 = GetDominantCount(channel2, out int value2);
        int threshold = tripletCount * 98 / 100;

        return (dominant0 >= threshold && dominant1 >= threshold && value0 == value1 && dominant2 < threshold) ||
               (dominant0 >= threshold && dominant2 >= threshold && value0 == value2 && dominant1 < threshold) ||
               (dominant1 >= threshold && dominant2 >= threshold && value1 == value2 && dominant0 < threshold);
    }

    private static int GetDominantCount(ReadOnlySpan<int> counts, out int value)
    {
        int best = 0;
        value = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > best)
            {
                best = counts[i];
                value = i;
            }
        }

        return best;
    }

    private static bool TryBuildRepairedTga(byte[] data, int footerOffset, int headerOffset, out byte[]? repaired)
    {
        repaired = null;
        byte[] headerData = data.AsSpan(headerOffset).ToArray();
        if (!TryReadHeader(headerData, out TgaHeader header) || IsRleType(header.ImageType))
        {
            return false;
        }

        int sourcePixelBytes = GetSourcePixelBytes(header);
        if (sourcePixelBytes <= 0)
        {
            return false;
        }

        int headerPrefixBytes = header.ImageDataOffset;
        int width = header.Width;
        int height = header.Height;
        long pixelBytes = (long)width * height * sourcePixelBytes;
        long availablePixelBytes = footerOffset + (long)data.Length - (headerOffset + headerPrefixBytes);
        if (pixelBytes != availablePixelBytes &&
            TryInferSquareDimensions(availablePixelBytes, sourcePixelBytes, out int inferredSide))
        {
            width = inferredSide;
            height = inferredSide;
            pixelBytes = availablePixelBytes;
        }

        if (pixelBytes != availablePixelBytes ||
            !IsSafeImageSize(width, height) ||
            headerOffset + headerPrefixBytes > data.Length ||
            headerPrefixBytes + pixelBytes > int.MaxValue)
        {
            return false;
        }

        repaired = new byte[headerPrefixBytes + (int)pixelBytes];
        data.AsSpan(headerOffset, headerPrefixBytes).CopyTo(repaired);
        WriteUInt16(repaired, 12, width);
        WriteUInt16(repaired, 14, height);
        data.AsSpan(0, footerOffset).CopyTo(repaired.AsSpan(headerPrefixBytes));
        data.AsSpan(headerOffset + headerPrefixBytes).CopyTo(repaired.AsSpan(headerPrefixBytes + footerOffset));
        return true;
    }

    private static bool TryInferSquareDimensions(long pixelBytes, int sourcePixelBytes, out int side)
    {
        side = 0;
        if (sourcePixelBytes <= 0 || pixelBytes <= 0 || pixelBytes % sourcePixelBytes != 0)
        {
            return false;
        }

        long pixelCount = pixelBytes / sourcePixelBytes;
        side = (int)Math.Sqrt(pixelCount);
        return side > 0 && (long)side * side == pixelCount;
    }

    private static bool HasTgaFooterTerminator(byte[] data, int signatureOffset)
    {
        int terminatorOffset = signatureOffset + FooterSignature.Length;
        return terminatorOffset + 2 <= data.Length &&
               data[terminatorOffset] == (byte)'.' &&
               data[terminatorOffset + 1] == 0;
    }

    private static bool TryReadHeader(byte[] data, out TgaHeader header)
    {
        header = default;
        if (data.Length < HeaderLength)
        {
            return false;
        }

        int idLength = data[0];
        int colorMapType = data[1];
        int imageType = data[2];
        int colorMapFirstEntry = ReadUInt16(data, 3);
        int colorMapLength = ReadUInt16(data, 5);
        int colorMapEntryBits = data[7];
        int width = ReadUInt16(data, 12);
        int height = ReadUInt16(data, 14);
        int pixelDepth = data[16];
        int descriptor = data[17];

        if (colorMapType is not 0 and not 1 ||
            !IsSupportedImageType(imageType) ||
            !IsSafeImageSize(width, height))
        {
            return false;
        }

        int colorMapOffset = HeaderLength + idLength;
        if (colorMapOffset < HeaderLength || colorMapOffset > data.Length)
        {
            return false;
        }

        int colorMapBytes = 0;
        if (colorMapType == 1)
        {
            int colorMapEntryBytes = GetColorMapEntryBytes(colorMapEntryBits);
            if (colorMapEntryBytes <= 0 || colorMapLength <= 0)
            {
                return false;
            }

            colorMapBytes = checked(colorMapLength * colorMapEntryBytes);
            if (!HasBytes(data, colorMapOffset, colorMapBytes))
            {
                return false;
            }
        }

        if (IsColorMappedType(imageType) && colorMapType != 1)
        {
            return false;
        }

        int imageDataOffset = colorMapOffset + colorMapBytes;
        if (imageDataOffset < colorMapOffset || imageDataOffset > data.Length)
        {
            return false;
        }

        header = new TgaHeader(
            colorMapType,
            imageType,
            colorMapFirstEntry,
            colorMapLength,
            colorMapEntryBits,
            width,
            height,
            pixelDepth,
            descriptor,
            colorMapOffset,
            imageDataOffset);
        return true;
    }

    private static bool TryReadPalette(byte[] data, TgaHeader header, out byte[]? palette)
    {
        palette = null;
        if (header.ColorMapType == 0)
        {
            return true;
        }

        int entryBytes = GetColorMapEntryBytes(header.ColorMapEntryBits);
        palette = new byte[checked(header.ColorMapLength * 4)];
        int source = header.ColorMapOffset;
        for (int i = 0; i < header.ColorMapLength; i++)
        {
            if (!DecodeColor(data.AsSpan(source, entryBytes), header.ColorMapEntryBits, header.AlphaBits(), palette.AsSpan(i * 4, 4)))
            {
                return false;
            }

            source += entryBytes;
        }

        return true;
    }

    private static bool DecodeUncompressed(
        byte[] data,
        TgaHeader header,
        byte[]? palette,
        int sourcePixelBytes,
        byte[] pixels)
    {
        int pixelCount = header.Width * header.Height;
        int source = header.ImageDataOffset;
        int requiredBytes = checked(pixelCount * sourcePixelBytes);
        if (!HasBytes(data, source, requiredBytes))
        {
            return false;
        }

        for (int ordinal = 0; ordinal < pixelCount; ordinal++)
        {
            int target = GetTargetOffset(header, ordinal);
            if (!DecodePixel(data.AsSpan(source, sourcePixelBytes), header, palette, pixels.AsSpan(target, 4)))
            {
                return false;
            }

            source += sourcePixelBytes;
        }

        return true;
    }

    private static bool DecodeRle(
        byte[] data,
        TgaHeader header,
        byte[]? palette,
        int sourcePixelBytes,
        byte[] pixels)
    {
        int pixelCount = header.Width * header.Height;
        int source = header.ImageDataOffset;
        int ordinal = 0;

        while (ordinal < pixelCount)
        {
            if (!HasBytes(data, source, 1))
            {
                return false;
            }

            int packet = data[source++];
            int packetPixels = (packet & 0x7F) + 1;
            if (packetPixels > pixelCount - ordinal)
            {
                return false;
            }

            bool isRun = (packet & 0x80) != 0;
            if (isRun)
            {
                if (!HasBytes(data, source, sourcePixelBytes))
                {
                    return false;
                }

                ReadOnlySpan<byte> sourcePixel = data.AsSpan(source, sourcePixelBytes);
                source += sourcePixelBytes;
                for (int i = 0; i < packetPixels; i++, ordinal++)
                {
                    int target = GetTargetOffset(header, ordinal);
                    if (!DecodePixel(sourcePixel, header, palette, pixels.AsSpan(target, 4)))
                    {
                        return false;
                    }
                }
            }
            else
            {
                int packetBytes = checked(packetPixels * sourcePixelBytes);
                if (!HasBytes(data, source, packetBytes))
                {
                    return false;
                }

                for (int i = 0; i < packetPixels; i++, ordinal++)
                {
                    int target = GetTargetOffset(header, ordinal);
                    if (!DecodePixel(data.AsSpan(source, sourcePixelBytes), header, palette, pixels.AsSpan(target, 4)))
                    {
                        return false;
                    }

                    source += sourcePixelBytes;
                }
            }
        }

        return true;
    }

    private static bool DecodePixel(ReadOnlySpan<byte> source, TgaHeader header, byte[]? palette, Span<byte> target)
    {
        if (IsColorMappedType(header.ImageType))
        {
            if (palette is null)
            {
                return false;
            }

            int colorIndex = header.PixelDepth switch
            {
                8 => source[0],
                15 or 16 => BinaryPrimitives.ReadUInt16LittleEndian(source),
                _ => -1
            };

            int paletteIndex = colorIndex - header.ColorMapFirstEntry;
            if (paletteIndex < 0 || paletteIndex >= header.ColorMapLength)
            {
                return false;
            }

            palette.AsSpan(paletteIndex * 4, 4).CopyTo(target);
            return true;
        }

        if (IsGrayscaleType(header.ImageType))
        {
            return DecodeGrayscale(source, header, target);
        }

        return DecodeColor(source, header.PixelDepth, header.AlphaBits(), target);
    }

    private static bool DecodeColor(ReadOnlySpan<byte> source, int bitsPerPixel, int alphaBits, Span<byte> target)
    {
        switch (bitsPerPixel)
        {
            case 15:
            case 16:
            {
                ushort value = BinaryPrimitives.ReadUInt16LittleEndian(source);
                target[0] = Expand5(value & 0x1F);
                target[1] = Expand5((value >> 5) & 0x1F);
                target[2] = Expand5((value >> 10) & 0x1F);
                target[3] = alphaBits > 0 && bitsPerPixel == 16 && (value & 0x8000) == 0 ? (byte)0 : (byte)255;
                return true;
            }
            case 24:
                target[0] = source[0];
                target[1] = source[1];
                target[2] = source[2];
                target[3] = 0xFF;
                return true;
            case 32:
                target[0] = source[0];
                target[1] = source[1];
                target[2] = source[2];
                target[3] = alphaBits == 0 ? (byte)0xFF : source[3];
                return true;
            default:
                return false;
        }
    }

    private static bool DecodeGrayscale(ReadOnlySpan<byte> source, TgaHeader header, Span<byte> target)
    {
        switch (header.PixelDepth)
        {
            case 8:
                target[0] = source[0];
                target[1] = source[0];
                target[2] = source[0];
                target[3] = 0xFF;
                return true;
            case 16:
                target[0] = source[0];
                target[1] = source[0];
                target[2] = source[0];
                target[3] = header.AlphaBits() == 0 ? (byte)0xFF : source[1];
                return true;
            default:
                return false;
        }
    }

    private static ImageSource CreateImage(int width, int height, byte[] pixels, int? maxSide)
    {
        BitmapSource source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        source.Freeze();

        if (maxSide is null)
        {
            return source;
        }

        double scale = Math.Min(1.0, (double)maxSide.Value / Math.Max(width, height));
        if (scale >= 1.0)
        {
            return source;
        }

        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static int GetTargetOffset(TgaHeader header, int ordinal)
    {
        int sourceX = ordinal % header.Width;
        int sourceY = ordinal / header.Width;
        int targetX = header.IsRightOrigin() ? header.Width - 1 - sourceX : sourceX;
        int targetY = header.IsTopOrigin() ? sourceY : header.Height - 1 - sourceY;
        return ((targetY * header.Width) + targetX) * 4;
    }

    private static int GetSourcePixelBytes(TgaHeader header)
    {
        return header.ImageType switch
        {
            ImageTypeColorMapped or ImageTypeRleColorMapped => header.PixelDepth switch
            {
                8 => 1,
                15 or 16 => 2,
                _ => 0
            },
            ImageTypeGrayscale or ImageTypeRleGrayscale => header.PixelDepth switch
            {
                8 => 1,
                16 => 2,
                _ => 0
            },
            ImageTypeTrueColor or ImageTypeRleTrueColor => header.PixelDepth switch
            {
                15 or 16 => 2,
                24 => 3,
                32 => 4,
                _ => 0
            },
            _ => 0
        };
    }

    private static int GetColorMapEntryBytes(int entryBits)
    {
        return entryBits switch
        {
            15 or 16 => 2,
            24 => 3,
            32 => 4,
            _ => 0
        };
    }

    private static bool IsSupportedImageType(int imageType)
    {
        return imageType is ImageTypeColorMapped
            or ImageTypeTrueColor
            or ImageTypeGrayscale
            or ImageTypeRleColorMapped
            or ImageTypeRleTrueColor
            or ImageTypeRleGrayscale;
    }

    private static bool IsColorMappedType(int imageType)
    {
        return imageType is ImageTypeColorMapped or ImageTypeRleColorMapped;
    }

    private static bool IsGrayscaleType(int imageType)
    {
        return imageType is ImageTypeGrayscale or ImageTypeRleGrayscale;
    }

    private static bool IsRleType(int imageType)
    {
        return imageType is ImageTypeRleColorMapped or ImageTypeRleTrueColor or ImageTypeRleGrayscale;
    }

    private static bool IsSafeImageSize(int width, int height)
    {
        return width > 0 &&
               height > 0 &&
               width <= 16384 &&
               height <= 16384 &&
               (long)width * height <= MaxDecodedPixels;
    }

    private static bool HasBytes(byte[] data, int offset, int byteCount)
    {
        return offset >= 0 &&
               byteCount >= 0 &&
               offset <= data.Length &&
               byteCount <= data.Length - offset;
    }

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    }

    private static void WriteUInt16(byte[] data, int offset, int value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), checked((ushort)value));
    }

    private static byte Expand5(int value)
    {
        return (byte)((value << 3) | (value >> 2));
    }

    private static int AlphaBits(this TgaHeader header)
    {
        return header.Descriptor & 0x0F;
    }

    private static bool IsRightOrigin(this TgaHeader header)
    {
        return (header.Descriptor & 0x10) != 0;
    }

    private static bool IsTopOrigin(this TgaHeader header)
    {
        return (header.Descriptor & 0x20) != 0;
    }
}
