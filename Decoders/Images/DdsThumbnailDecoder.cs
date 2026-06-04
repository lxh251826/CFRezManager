using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

internal static class DdsThumbnailDecoder
{
    private const int HeaderLength = 128;
    private const int MaxDecodedPixels = 4096 * 4096;
    private const int ThumbnailDecodeMaxSide = 192;
    private const uint PixelFormatAlphaPixels = 0x1;
    private const uint PixelFormatFourCc = 0x4;
    private const uint PixelFormatRgb = 0x40;
    private const uint PixelFormatLuminance = 0x20000;
    private const uint Caps2Cubemap = 0x200;
    private const uint Caps2CubemapPositiveX = 0x400;
    private const uint Caps2CubemapNegativeX = 0x800;
    private const uint Caps2CubemapPositiveY = 0x1000;
    private const uint Caps2CubemapNegativeY = 0x2000;
    private const uint Caps2CubemapPositiveZ = 0x4000;
    private const uint Caps2CubemapNegativeZ = 0x8000;

    private enum DdsPixelFormat
    {
        Unknown,
        Dxt1,
        Dxt3,
        Dxt5,
        Rgb,
        Luminance
    }

    private readonly record struct DdsHeader(
        int Width,
        int Height,
        int MipMapCount,
        uint Caps2,
        DdsPixelFormat Format,
        int BitsPerPixel,
        uint RedMask,
        uint GreenMask,
        uint BlueMask,
        uint AlphaMask,
        int FirstLevelByteCount);

    public static ImageSource? TryDecode(byte[] data)
    {
        return TryDecode(data, 0, ThumbnailDecodeMaxSide, out ImageSource? image, out _)
            ? image
            : null;
    }

    public static ImageSource? TryDecodeOriginal(byte[] data)
    {
        return TryDecode(data, 0, maxSide: null, out ImageSource? image, out _)
            ? image
            : null;
    }

    public static bool IsBlockCompressed(byte[] data)
    {
        return TryReadHeader(data, 0, out DdsHeader header, out _) && IsBlockCompressed(header.Format);
    }

    public static bool TryDecode(byte[] data, int offset, out ImageSource? image, out int byteCount)
    {
        return TryDecode(data, offset, maxSide: null, out image, out byteCount);
    }

    private static bool TryDecode(byte[] data, int offset, int? maxSide, out ImageSource? image, out int byteCount)
    {
        image = null;
        byteCount = 0;

        if (!TryReadHeader(data, offset, out DdsHeader header, out byteCount))
        {
            return false;
        }

        byte[]? pixels = IsBlockCompressed(header.Format)
            ? DecodeDxt(data, offset + HeaderLength, header.Width, header.Height, header.Format)
            : DecodeUncompressed(data, offset + HeaderLength, header);
        if (pixels is null)
        {
            return false;
        }

        image = CreateImage(header.Width, header.Height, pixels, maxSide);
        return true;
    }

    private static bool TryReadHeader(byte[] data, int offset, out DdsHeader header, out int byteCount)
    {
        header = default;
        byteCount = 0;

        if (!HasBytes(data, offset, HeaderLength) ||
            !data.AsSpan(offset, 4).SequenceEqual("DDS "u8) ||
            ReadInt32(data, offset + 4) != 124)
        {
            return false;
        }

        int height = ReadInt32(data, offset + 12);
        int width = ReadInt32(data, offset + 16);
        if (!IsSafeImageSize(width, height) ||
            ReadInt32(data, offset + 76) != 32)
        {
            return false;
        }

        int mipMapCount = Math.Max(1, ReadInt32(data, offset + 28));
        uint pixelFormatFlags = ReadUInt32(data, offset + 80);
        int bitsPerPixel = ReadInt32(data, offset + 88);
        uint redMask = ReadUInt32(data, offset + 92);
        uint greenMask = ReadUInt32(data, offset + 96);
        uint blueMask = ReadUInt32(data, offset + 100);
        uint alphaMask = ReadUInt32(data, offset + 104);
        uint caps2 = ReadUInt32(data, offset + 112);
        DdsPixelFormat format = DdsPixelFormat.Unknown;

        ReadOnlySpan<byte> fourCc = data.AsSpan(offset + 84, 4);
        if ((pixelFormatFlags & PixelFormatFourCc) != 0 && fourCc.SequenceEqual("DXT1"u8))
        {
            format = DdsPixelFormat.Dxt1;
        }
        else if ((pixelFormatFlags & PixelFormatFourCc) != 0 && fourCc.SequenceEqual("DXT3"u8))
        {
            format = DdsPixelFormat.Dxt3;
        }
        else if ((pixelFormatFlags & PixelFormatFourCc) != 0 && fourCc.SequenceEqual("DXT5"u8))
        {
            format = DdsPixelFormat.Dxt5;
        }
        else if ((pixelFormatFlags & PixelFormatRgb) != 0 &&
                 IsSupportedRgbFormat(bitsPerPixel, redMask, greenMask, blueMask))
        {
            format = DdsPixelFormat.Rgb;
        }
        else if ((pixelFormatFlags & PixelFormatLuminance) != 0 &&
                 IsSupportedLuminanceFormat(bitsPerPixel, redMask))
        {
            format = DdsPixelFormat.Luminance;
        }
        else
        {
            return false;
        }

        int firstLevelBytes = GetLevelByteCount(width, height, format, bitsPerPixel);
        if (!HasBytes(data, offset + HeaderLength, firstLevelBytes))
        {
            return false;
        }

        header = new DdsHeader(
            width,
            height,
            mipMapCount,
            caps2,
            format,
            bitsPerPixel,
            redMask,
            greenMask,
            blueMask,
            (pixelFormatFlags & PixelFormatAlphaPixels) != 0 ? alphaMask : 0,
            firstLevelBytes);

        byteCount = GetAvailableTextureByteCount(data, offset, header);
        return true;
    }

    private static byte[]? DecodeDxt(byte[] data, int sourceOffset, int width, int height, DdsPixelFormat format)
    {
        int blockBytes = format == DdsPixelFormat.Dxt1 ? 8 : 16;
        int compressedBytes = GetDxtLevelByteCount(width, height, blockBytes);
        if (!HasBytes(data, sourceOffset, compressedBytes))
        {
            return null;
        }

        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        var pixels = new byte[checked(width * height * 4)];
        int source = sourceOffset;
        for (int blockY = 0; blockY < blocksY; blockY++)
        {
            for (int blockX = 0; blockX < blocksX; blockX++)
            {
                switch (format)
                {
                    case DdsPixelFormat.Dxt1:
                        DecodeDxt1Block(data, source, pixels, width, height, blockX, blockY);
                        break;
                    case DdsPixelFormat.Dxt3:
                        DecodeDxt3Block(data, source, pixels, width, height, blockX, blockY);
                        break;
                    case DdsPixelFormat.Dxt5:
                        DecodeDxt5Block(data, source, pixels, width, height, blockX, blockY);
                        break;
                }

                source += blockBytes;
            }
        }

        return pixels;
    }

    private static byte[]? DecodeUncompressed(byte[] data, int sourceOffset, DdsHeader header)
    {
        int bytesPerPixel = header.BitsPerPixel / 8;
        if (bytesPerPixel <= 0 || header.FirstLevelByteCount <= 0 ||
            !HasBytes(data, sourceOffset, header.FirstLevelByteCount))
        {
            return null;
        }

        int rowPitch = GetUncompressedRowPitch(header.Width, header.BitsPerPixel);
        var pixels = new byte[checked(header.Width * header.Height * 4)];
        for (int y = 0; y < header.Height; y++)
        {
            int sourceRow = sourceOffset + (y * rowPitch);
            int targetRow = y * header.Width * 4;
            for (int x = 0; x < header.Width; x++)
            {
                uint value = ReadPixelValue(data, sourceRow + (x * bytesPerPixel), bytesPerPixel);
                byte r;
                byte g;
                byte b;
                if (header.Format == DdsPixelFormat.Luminance)
                {
                    r = g = b = ExtractChannel(value, header.RedMask, 0);
                }
                else
                {
                    r = ExtractChannel(value, header.RedMask, 0);
                    g = ExtractChannel(value, header.GreenMask, 0);
                    b = ExtractChannel(value, header.BlueMask, 0);
                }

                byte a = header.AlphaMask == 0
                    ? (byte)255
                    : ExtractChannel(value, header.AlphaMask, 255);
                int target = targetRow + (x * 4);
                pixels[target] = b;
                pixels[target + 1] = g;
                pixels[target + 2] = r;
                pixels[target + 3] = a;
            }
        }

        return pixels;
    }

    private static void DecodeDxt1Block(
        byte[] data,
        int source,
        byte[] pixels,
        int width,
        int height,
        int blockX,
        int blockY)
    {
        DecodeColorBlock(data, source, pixels, width, height, blockX, blockY, ReadOnlySpan<byte>.Empty, useDxt1Alpha: true);
    }

    private static void DecodeDxt3Block(
        byte[] data,
        int source,
        byte[] pixels,
        int width,
        int height,
        int blockX,
        int blockY)
    {
        Span<byte> alpha = stackalloc byte[16];
        ulong alphaBits = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(source, 8));
        for (int i = 0; i < 16; i++)
        {
            alpha[i] = (byte)(((alphaBits >> (i * 4)) & 0xF) * 17);
        }

        DecodeColorBlock(data, source + 8, pixels, width, height, blockX, blockY, alpha, useDxt1Alpha: false);
    }

    private static void DecodeDxt5Block(
        byte[] data,
        int source,
        byte[] pixels,
        int width,
        int height,
        int blockX,
        int blockY)
    {
        Span<byte> palette = stackalloc byte[8];
        palette[0] = data[source];
        palette[1] = data[source + 1];
        if (palette[0] > palette[1])
        {
            palette[2] = (byte)((6 * palette[0] + palette[1]) / 7);
            palette[3] = (byte)((5 * palette[0] + 2 * palette[1]) / 7);
            palette[4] = (byte)((4 * palette[0] + 3 * palette[1]) / 7);
            palette[5] = (byte)((3 * palette[0] + 4 * palette[1]) / 7);
            palette[6] = (byte)((2 * palette[0] + 5 * palette[1]) / 7);
            palette[7] = (byte)((palette[0] + 6 * palette[1]) / 7);
        }
        else
        {
            palette[2] = (byte)((4 * palette[0] + palette[1]) / 5);
            palette[3] = (byte)((3 * palette[0] + 2 * palette[1]) / 5);
            palette[4] = (byte)((2 * palette[0] + 3 * palette[1]) / 5);
            palette[5] = (byte)((palette[0] + 4 * palette[1]) / 5);
            palette[6] = 0;
            palette[7] = 255;
        }

        ulong alphaBits = 0;
        for (int i = 0; i < 6; i++)
        {
            alphaBits |= (ulong)data[source + 2 + i] << (8 * i);
        }

        Span<byte> alpha = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            alpha[i] = palette[(int)((alphaBits >> (i * 3)) & 0x7)];
        }

        DecodeColorBlock(data, source + 8, pixels, width, height, blockX, blockY, alpha, useDxt1Alpha: false);
    }

    private static void DecodeColorBlock(
        byte[] data,
        int source,
        byte[] pixels,
        int width,
        int height,
        int blockX,
        int blockY,
        ReadOnlySpan<byte> alpha,
        bool useDxt1Alpha)
    {
        ushort color0 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(source, 2));
        ushort color1 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(source + 2, 2));
        Span<byte> colors = stackalloc byte[16];
        WriteRgb565(colors, 0, color0, 255);
        WriteRgb565(colors, 4, color1, 255);
        if (color0 > color1 || !useDxt1Alpha)
        {
            InterpolateColor(colors, 8, colors, 0, 2, colors, 4, 1, 3, 255);
            InterpolateColor(colors, 12, colors, 0, 1, colors, 4, 2, 3, 255);
        }
        else
        {
            InterpolateColor(colors, 8, colors, 0, 1, colors, 4, 1, 2, 255);
            colors[12] = 0;
            colors[13] = 0;
            colors[14] = 0;
            colors[15] = 0;
        }

        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(source + 4, 4));
        for (int y = 0; y < 4; y++)
        {
            int targetY = (blockY * 4) + y;
            if (targetY >= height)
            {
                continue;
            }

            for (int x = 0; x < 4; x++)
            {
                int targetX = (blockX * 4) + x;
                if (targetX >= width)
                {
                    continue;
                }

                int pixelIndex = (y * 4) + x;
                int colorIndex = (int)((indices >> (pixelIndex * 2)) & 0x3) * 4;
                int target = ((targetY * width) + targetX) * 4;
                pixels[target] = colors[colorIndex];
                pixels[target + 1] = colors[colorIndex + 1];
                pixels[target + 2] = colors[colorIndex + 2];
                pixels[target + 3] = alpha.IsEmpty ? colors[colorIndex + 3] : alpha[pixelIndex];
            }
        }
    }

    private static void WriteRgb565(Span<byte> target, int offset, ushort color, byte alpha)
    {
        byte r = (byte)((((color >> 11) & 0x1F) * 255) / 31);
        byte g = (byte)((((color >> 5) & 0x3F) * 255) / 63);
        byte b = (byte)(((color & 0x1F) * 255) / 31);
        target[offset] = b;
        target[offset + 1] = g;
        target[offset + 2] = r;
        target[offset + 3] = alpha;
    }

    private static void InterpolateColor(
        Span<byte> target,
        int targetOffset,
        ReadOnlySpan<byte> left,
        int leftOffset,
        int leftWeight,
        ReadOnlySpan<byte> right,
        int rightOffset,
        int rightWeight,
        int divisor,
        byte alpha)
    {
        target[targetOffset] = (byte)((left[leftOffset] * leftWeight + right[rightOffset] * rightWeight) / divisor);
        target[targetOffset + 1] = (byte)((left[leftOffset + 1] * leftWeight + right[rightOffset + 1] * rightWeight) / divisor);
        target[targetOffset + 2] = (byte)((left[leftOffset + 2] * leftWeight + right[rightOffset + 2] * rightWeight) / divisor);
        target[targetOffset + 3] = alpha;
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

        if (maxSide is not > 0 || Math.Max(width, height) <= maxSide.Value)
        {
            source.Freeze();
            return source;
        }

        double scale = maxSide.Value / (double)Math.Max(width, height);
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static bool IsSupportedRgbFormat(int bitsPerPixel, uint redMask, uint greenMask, uint blueMask)
    {
        return bitsPerPixel is 16 or 24 or 32 &&
               redMask != 0 &&
               greenMask != 0 &&
               blueMask != 0;
    }

    private static bool IsSupportedLuminanceFormat(int bitsPerPixel, uint luminanceMask)
    {
        return bitsPerPixel is 8 or 16 && luminanceMask != 0;
    }

    private static bool IsBlockCompressed(DdsPixelFormat format)
    {
        return format is DdsPixelFormat.Dxt1 or DdsPixelFormat.Dxt3 or DdsPixelFormat.Dxt5;
    }

    private static int GetDxtLevelByteCount(int width, int height, int blockBytes)
    {
        return checked(((Math.Max(1, width) + 3) / 4) * ((Math.Max(1, height) + 3) / 4) * blockBytes);
    }

    private static int GetLevelByteCount(int width, int height, DdsPixelFormat format, int bitsPerPixel)
    {
        if (IsBlockCompressed(format))
        {
            int blockBytes = format == DdsPixelFormat.Dxt1 ? 8 : 16;
            return GetDxtLevelByteCount(width, height, blockBytes);
        }

        return checked(GetUncompressedRowPitch(width, bitsPerPixel) * height);
    }

    private static int GetUncompressedRowPitch(int width, int bitsPerPixel)
    {
        return checked(((width * bitsPerPixel) + 31) / 32 * 4);
    }

    private static int GetAvailableTextureByteCount(byte[] data, int offset, DdsHeader header)
    {
        int firstLevelByteCount = HeaderLength + header.FirstLevelByteCount;
        long totalTextureBytes = GetTotalTextureBytes(header);
        long totalByteCount = HeaderLength + totalTextureBytes;
        return totalByteCount <= int.MaxValue && HasBytes(data, offset, (int)totalByteCount)
            ? (int)totalByteCount
            : firstLevelByteCount;
    }

    private static long GetTotalTextureBytes(DdsHeader header)
    {
        long total = 0;
        int width = header.Width;
        int height = header.Height;
        int mipLevels = Math.Min(header.MipMapCount, CountPossibleMipLevels(header.Width, header.Height));
        for (int level = 0; level < mipLevels; level++)
        {
            total += GetLevelByteCount(width, height, header.Format, header.BitsPerPixel);
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        return total * GetFaceCount(header.Caps2);
    }

    private static int CountPossibleMipLevels(int width, int height)
    {
        int count = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            count++;
        }

        return count;
    }

    private static int GetFaceCount(uint caps2)
    {
        if ((caps2 & Caps2Cubemap) == 0)
        {
            return 1;
        }

        int faces = 0;
        faces += (caps2 & Caps2CubemapPositiveX) != 0 ? 1 : 0;
        faces += (caps2 & Caps2CubemapNegativeX) != 0 ? 1 : 0;
        faces += (caps2 & Caps2CubemapPositiveY) != 0 ? 1 : 0;
        faces += (caps2 & Caps2CubemapNegativeY) != 0 ? 1 : 0;
        faces += (caps2 & Caps2CubemapPositiveZ) != 0 ? 1 : 0;
        faces += (caps2 & Caps2CubemapNegativeZ) != 0 ? 1 : 0;
        return faces == 0 ? 6 : faces;
    }

    private static uint ReadPixelValue(byte[] data, int offset, int bytesPerPixel)
    {
        return bytesPerPixel switch
        {
            1 => data[offset],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)),
            3 => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)),
            _ => 0
        };
    }

    private static byte ExtractChannel(uint value, uint mask, byte defaultValue)
    {
        if (mask == 0)
        {
            return defaultValue;
        }

        int shift = 0;
        while (shift < 32 && ((mask >> shift) & 1) == 0)
        {
            shift++;
        }

        uint max = mask >> shift;
        if (max == 0)
        {
            return defaultValue;
        }

        uint channel = (value & mask) >> shift;
        return (byte)((channel * 255 + (max / 2)) / max);
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

    private static int ReadInt32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }
}
