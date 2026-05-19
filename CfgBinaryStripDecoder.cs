using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

internal sealed record CfgBinaryStripInfo(
    int ByteCount,
    int PixelCount,
    int VariableChannel,
    int UniqueByteCount,
    double WhiteByteRatio,
    double FixedChannelRatio);

internal static class CfgBinaryStripDecoder
{
    private const int MaxUniqueBytes = 96;
    private const double MinWhiteByteRatio = 0.55;
    private const double MinFixedChannelRatio = 0.90;

    public static bool TryDetect(ReadOnlySpan<byte> data, out CfgBinaryStripInfo info)
    {
        info = new CfgBinaryStripInfo(0, 0, 0, 0, 0, 0);
        if (data.Length < 48)
        {
            return false;
        }

        bool[] seen = new bool[256];
        int uniqueCount = 0;
        int whiteCount = 0;
        int[] channelLengths = new int[3];
        int[] channelWhiteCounts = new int[3];
        int[] channelVariableCounts = new int[3];

        for (int index = 0; index < data.Length; index++)
        {
            byte value = data[index];
            if (!seen[value])
            {
                seen[value] = true;
                uniqueCount++;
            }

            int channel = index % 3;
            channelLengths[channel]++;
            if (value == 0xFF)
            {
                whiteCount++;
                channelWhiteCounts[channel]++;
            }
            else
            {
                channelVariableCounts[channel]++;
            }
        }

        if (uniqueCount > MaxUniqueBytes)
        {
            return false;
        }

        double whiteRatio = whiteCount / (double)data.Length;
        if (whiteRatio < MinWhiteByteRatio)
        {
            return false;
        }

        int variableChannel = 0;
        for (int channel = 1; channel < channelVariableCounts.Length; channel++)
        {
            if (channelVariableCounts[channel] > channelVariableCounts[variableChannel])
            {
                variableChannel = channel;
            }
        }

        double fixedChannelRatioSum = 0;
        int fixedChannelCount = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            if (channel == variableChannel)
            {
                continue;
            }

            fixedChannelRatioSum += channelLengths[channel] == 0
                ? 1
                : channelWhiteCounts[channel] / (double)channelLengths[channel];
            fixedChannelCount++;
        }

        double fixedChannelRatio = fixedChannelRatioSum / fixedChannelCount;
        if (fixedChannelRatio < MinFixedChannelRatio)
        {
            return false;
        }

        int pixelCount = (data.Length + 2) / 3;
        info = new CfgBinaryStripInfo(
            data.Length,
            pixelCount,
            variableChannel,
            uniqueCount,
            whiteRatio,
            fixedChannelRatio);
        return true;
    }

    public static bool TryRenderThumbnail(byte[] data, out ImageSource? image, out CfgBinaryStripInfo info)
    {
        image = null;
        if (!TryDetect(data, out info))
        {
            return false;
        }

        image = RenderStrip(data, Math.Clamp(info.PixelCount, 64, 192), 96);
        return image is not null;
    }

    public static bool TryWritePng(byte[] data, string outputPath, out CfgBinaryStripInfo info)
    {
        if (!TryDetect(data, out info))
        {
            return false;
        }

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        BitmapSource bitmap = RenderStrip(data, Math.Clamp(info.PixelCount, 128, 512), 48);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
        return true;
    }

    public static string Describe(CfgBinaryStripInfo info)
    {
        string channelName = info.VariableChannel switch
        {
            0 => "R",
            1 => "G",
            2 => "B",
            _ => "?"
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"raw RGB strip, pixels={info.PixelCount}, variableChannel={channelName}, uniqueBytes={info.UniqueByteCount}, FF={info.WhiteByteRatio:P1}");
    }

    private static BitmapSource RenderStrip(ReadOnlySpan<byte> data, int width, int height)
    {
        int pixelCount = Math.Max(1, (data.Length + 2) / 3);
        byte[] pixels = new byte[width * height * 4];
        for (int x = 0; x < width; x++)
        {
            int sourcePixel = Math.Min(pixelCount - 1, x * pixelCount / width);
            int sourceOffset = sourcePixel * 3;
            byte r = ReadOrWhite(data, sourceOffset);
            byte g = ReadOrWhite(data, sourceOffset + 1);
            byte b = ReadOrWhite(data, sourceOffset + 2);

            for (int y = 0; y < height; y++)
            {
                int outputOffset = ((y * width) + x) * 4;
                pixels[outputOffset] = b;
                pixels[outputOffset + 1] = g;
                pixels[outputOffset + 2] = r;
                pixels[outputOffset + 3] = 0xFF;
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte ReadOrWhite(ReadOnlySpan<byte> data, int index)
    {
        return index >= 0 && index < data.Length ? data[index] : (byte)0xFF;
    }
}
