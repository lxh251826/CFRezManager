using System.Buffers.Binary;
using System.IO;
using NVorbis;

namespace CFRezManager;

internal static class OggVorbisWaveDecoder
{
    private const int BitsPerSample = 16;
    private const int HeaderLength = 44;
    private const long MaxPcmDataBytes = 512L * 1024 * 1024;

    public static bool TryDecodeToWave(byte[] oggData, string outputPath, out string? errorMessage)
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

            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new BinaryWriter(output);
            WriteWaveHeader(writer, channels, sampleRate, dataBytes: 0);

            int bufferSamples = Math.Max(channels * 4096, channels);
            var floatBuffer = new float[bufferSamples];
            long dataBytes = 0;
            while (true)
            {
                int samplesRead = reader.ReadSamples(floatBuffer, 0, floatBuffer.Length);
                if (samplesRead <= 0)
                {
                    break;
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
            }

            if (dataBytes == 0)
            {
                errorMessage = "Ogg Vorbis stream did not contain audio samples.";
                return false;
            }

            output.Position = 0;
            WriteWaveHeader(writer, channels, sampleRate, checked((int)dataBytes));
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
