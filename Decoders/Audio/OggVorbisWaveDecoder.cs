using System.Buffers.Binary;
using System.IO;
using System.Text;
using NVorbis;

namespace CFRezManager;

internal static class OggVorbisWaveDecoder
{
    private const int BitsPerSample = 16;
    private const int HeaderLength = 44;
    private const long MaxPcmDataBytes = 512L * 1024 * 1024;

    public static bool TryDecodeToWaveBytes(byte[] oggData, out byte[] wavData, out string? errorMessage)
    {
        return TryDecodeToWaveBytes(oggData, maxDurationSeconds: null, out wavData, out errorMessage);
    }

    public static bool TryDecodeToWaveBytes(byte[] oggData, double? maxDurationSeconds, out byte[] wavData, out string? errorMessage)
    {
        wavData = [];
        using var output = new MemoryStream();
        if (!TryDecodeToWaveStream(oggData, output, maxDurationSeconds, out errorMessage))
        {
            return false;
        }

        wavData = output.ToArray();
        return true;
    }

    public static bool TryDecodeToWave(byte[] oggData, string outputPath, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            return TryDecodeToWaveStream(oggData, output, maxDurationSeconds: null, out errorMessage);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or ArgumentException)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool TryDecodeToWaveStream(byte[] oggData, Stream output, double? maxDurationSeconds, out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            using var input = new MemoryStream(oggData, writable: false);
            using var reader = new VorbisReader(input, closeOnDispose: false);
            int channels = reader.Channels;
            int sampleRate = reader.SampleRate;
            if (channels <= 0 || sampleRate <= 0)
            {
                errorMessage = "Ogg Vorbis stream has invalid audio parameters.";
                return false;
            }

            output.SetLength(0);
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            WriteWaveHeader(writer, channels, sampleRate, dataBytes: 0);

            int bufferSamples = Math.Max(channels * 4096, channels);
            var floatBuffer = new float[bufferSamples];
            long? maxSamples = maxDurationSeconds is > 0
                ? Math.Max(channels, (long)Math.Ceiling(maxDurationSeconds.Value * sampleRate * channels))
                : null;
            long dataBytes = 0;
            long totalSamples = 0;
            while (true)
            {
                int samplesRead = reader.ReadSamples(floatBuffer, 0, floatBuffer.Length);
                if (samplesRead <= 0)
                {
                    break;
                }

                if (maxSamples is { } sampleLimit && totalSamples + samplesRead > sampleLimit)
                {
                    samplesRead = checked((int)Math.Max(0, sampleLimit - totalSamples));
                    if (samplesRead <= 0)
                    {
                        break;
                    }
                }

                long nextBytes = dataBytes + samplesRead * sizeof(short);
                if (nextBytes > MaxPcmDataBytes)
                {
                    errorMessage = "Decoded Ogg Vorbis audio is too large to preview.";
                    return false;
                }

                for (int i = 0; i < samplesRead; i++)
                {
                    writer.Write(FloatToPcm16(floatBuffer[i]));
                }

                dataBytes = nextBytes;
                totalSamples += samplesRead;
                if (maxSamples is { } limit && totalSamples >= limit)
                {
                    break;
                }
            }

            if (dataBytes == 0)
            {
                errorMessage = "Ogg Vorbis stream did not contain audio samples.";
                return false;
            }

            output.Position = 0;
            WriteWaveHeader(writer, channels, sampleRate, checked((int)dataBytes));
            output.Position = output.Length;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or ArgumentException)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static short FloatToPcm16(float value)
    {
        if (!float.IsFinite(value))
        {
            return 0;
        }

        value = Math.Clamp(value, -1f, 1f);
        return value < 0
            ? (short)(value * 32768f)
            : (short)(value * 32767f);
    }

    private static void WriteWaveHeader(BinaryWriter writer, int channels, int sampleRate, int dataBytes)
    {
        int blockAlign = channels * BitsPerSample / 8;
        int byteRate = sampleRate * blockAlign;

        writer.Write("RIFF"u8);
        writer.Write(HeaderLength - 8 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)BitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataBytes);
    }
}
