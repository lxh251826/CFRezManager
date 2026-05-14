using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

internal static class DdsThumbnailDecoder
{
    private const int HeaderLength = 128;
    private const int MaxDecodedPixels = 4096 * 4096;

    private enum DdsPixelFormat
    {
        Unknown,
        Dxt1,
        Dxt3,
        Dxt5
    }

    public static bool TryDecode(byte[] data, int offset, out ImageSource? image, out int byteCount)
    {
        image = null;
        byteCount = 0;

        if (!TryReadHeader(data, offset, out int width, out int height, out DdsPixelFormat format, out byteCount))
        {
            return false;
        }

        byte[]? pixels = DecodeDxt(data, offset + HeaderLength, width, height, format);
        if (pixels is null)
        {
            return false;
        }

        image = CreateImage(width, height, pixels);
        return true;
    }

    private static bool TryReadHeader(
        byte[] data,
        int offset,
        out int width,
        out int height,
        out DdsPixelFormat format,
        out int byteCount)
    {
        width = 0;
        height = 0;
        format = DdsPixelFormat.Unknown;
        byteCount = 0;

        if (!HasBytes(data, offset, HeaderLength) ||
            !data.AsSpan(offset, 4).SequenceEqual("DDS "u8) ||
            ReadInt32(data, offset + 4) != 124)
        {
            return false;
        }

        height = ReadInt32(data, offset + 12);
        width = ReadInt32(data, offset + 16);
        if (!IsSafeImageSize(width, height) ||
            ReadInt32(data, offset + 76) != 32)
        {
            return false;
        }

        ReadOnlySpan<byte> fourCc = data.AsSpan(offset + 84, 4);
        if (fourCc.SequenceEqual("DXT1"u8))
        {
            format = DdsPixelFormat.Dxt1;
        }
        else if (fourCc.SequenceEqual("DXT3"u8))
        {
            format = DdsPixelFormat.Dxt3;
        }
        else if (fourCc.SequenceEqual("DXT5"u8))
        {
            format = DdsPixelFormat.Dxt5;
        }
        else
        {
            return false;
        }

        int blockBytes = format == DdsPixelFormat.Dxt1 ? 8 : 16;
        int compressedBytes = GetDxtLevelByteCount(width, height, blockBytes);
        byteCount = HeaderLength + compressedBytes;
        return HasBytes(data, offset, byteCount);
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

    private static ImageSource CreateImage(int width, int height, byte[] pixels)
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
        return source;
    }

    private static int GetDxtLevelByteCount(int width, int height, int blockBytes)
    {
        return checked(((width + 3) / 4) * ((height + 3) / 4) * blockBytes);
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
}
