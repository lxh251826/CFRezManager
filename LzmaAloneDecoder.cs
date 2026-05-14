using System.Buffers.Binary;
using System.IO;
using SharpCompress.Compressors.LZMA;

namespace CFRezManager;

internal static class LzmaAloneDecoder
{
    private const int HeaderLength = 13;
    private const int PropertiesLength = 5;
    private const long MaxDecodedBytes = 128 * 1024 * 1024;

    public static bool IsCompressed(byte[] data)
    {
        return data.Length >= HeaderLength &&
               data[0] == 0x5D &&
               data[1] == 0 &&
               data[2] == 0 &&
               data[3] == 0;
    }

    public static byte[]? TryPrepareData(byte[] data)
    {
        return IsCompressed(data) ? TryDecompress(data) : data;
    }

    private static byte[]? TryDecompress(byte[] data)
    {
        try
        {
            long decodedBytes = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(PropertiesLength, sizeof(long)));
            if (decodedBytes <= 0 || decodedBytes > MaxDecodedBytes)
            {
                return null;
            }

            byte[] properties = data.AsSpan(0, PropertiesLength).ToArray();
            using var compressed = new MemoryStream(data, HeaderLength, data.Length - HeaderLength, writable: false);
            using LzmaStream lzma = LzmaStream.Create(
                properties,
                compressed,
                data.Length - HeaderLength,
                decodedBytes,
                leaveOpen: false);
            using var decompressed = new MemoryStream((int)decodedBytes);
            lzma.CopyTo(decompressed);
            return decompressed.Length == decodedBytes ? decompressed.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }
}
