using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using NAudio.Wave;

namespace CFRezManager;

internal sealed record AudioSpectrumData(float[][] Frames, double FrameDurationSeconds, int BandCount);

internal static class AudioSpectrumAnalyzer
{
    private const int TargetFramesPerSecond = 40;
    private const int MaxFrames = 8_000;

    public static bool TryAnalyzeFilePreview(string audioPath, int bandCount, double previewSeconds, out AudioSpectrumData? spectrum)
    {
        spectrum = null;
        try
        {
            byte[] data = File.ReadAllBytes(audioPath);
            if (TryAnalyze(data, bandCount, previewSeconds, out spectrum))
            {
                return true;
            }

            if (!string.Equals(Path.GetExtension(audioPath), ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var reader = new MediaFoundationReader(audioPath);
            using var decodedWave = new MemoryStream();
            using (var writer = new WaveFileWriter(decodedWave, reader.WaveFormat))
            {
                int maxBytes = checked((int)Math.Min(
                    reader.WaveFormat.AverageBytesPerSecond * Math.Max(0.5, previewSeconds),
                    int.MaxValue));
                byte[] buffer = new byte[Math.Min(64 * 1024, Math.Max(4096, maxBytes))];
                int bytesWritten = 0;
                while (bytesWritten < maxBytes)
                {
                    int bytesToRead = Math.Min(buffer.Length, maxBytes - bytesWritten);
                    int bytesRead = reader.Read(buffer, 0, bytesToRead);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    writer.Write(buffer, 0, bytesRead);
                    bytesWritten += bytesRead;
                }
            }

            return TryAnalyze(decodedWave.ToArray(), bandCount, previewSeconds, out spectrum);
        }
        catch
        {
            spectrum = null;
            return false;
        }
    }

    public static bool TryAnalyzeFile(string audioPath, int bandCount, out AudioSpectrumData? spectrum)
    {
        spectrum = null;
        try
        {
            byte[] data = File.ReadAllBytes(audioPath);
            if (TryAnalyze(data, bandCount, out spectrum))
            {
                return true;
            }

            if (!string.Equals(Path.GetExtension(audioPath), ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var reader = new MediaFoundationReader(audioPath);
            using var decodedWave = new MemoryStream();
            WaveFileWriter.WriteWavFileToStream(decodedWave, reader);
            return TryAnalyze(decodedWave.ToArray(), bandCount, out spectrum);
        }
        catch
        {
            spectrum = null;
            return false;
        }
    }

    public static bool TryAnalyze(byte[] wavData, int bandCount, out AudioSpectrumData? spectrum)
    {
        return TryAnalyze(wavData, bandCount, null, out spectrum);
    }

    private static bool TryAnalyze(byte[] wavData, int bandCount, double? maxDurationSeconds, out AudioSpectrumData? spectrum)
    {
        spectrum = null;
        if (bandCount <= 0 ||
            !AudioMetadataDecoder.IsWaveData(wavData) ||
            !TryReadWaveInfo(wavData, out WaveInfo wave) ||
            wave.SampleRate <= 0 ||
            wave.FrameCount <= 0)
        {
            return false;
        }

        int fftSize = wave.SampleRate >= 32_000 ? 2048 : 1024;
        if (wave.FrameCount < fftSize / 2)
        {
            return false;
        }

        int hopSize = Math.Max(1, wave.SampleRate / TargetFramesPerSecond);
        int frameCount = Math.Min(MaxFrames, Math.Max(1, (wave.FrameCount + hopSize - 1) / hopSize));
        if (maxDurationSeconds is > 0)
        {
            int previewFrameCount = Math.Max(1, (int)Math.Ceiling(maxDurationSeconds.Value * TargetFramesPerSecond));
            frameCount = Math.Min(frameCount, previewFrameCount);
        }

        var frames = new float[frameCount][];
        double[] window = BuildHannWindow(fftSize);
        int[] bandStarts = new int[bandCount];
        int[] bandEnds = new int[bandCount];
        BuildLogBands(bandCount, fftSize, wave.SampleRate, bandStarts, bandEnds);

        var positiveMagnitudes = new List<float>(frameCount * Math.Min(bandCount, 96));
        var fft = new Complex[fftSize];
        for (int frame = 0; frame < frameCount; frame++)
        {
            Array.Clear(fft);
            int startFrame = frame * hopSize - fftSize / 4;
            for (int i = 0; i < fftSize; i++)
            {
                int sourceFrame = startFrame + i;
                float sample = sourceFrame >= 0 && sourceFrame < wave.FrameCount
                    ? ReadMixedSample(wavData, wave, sourceFrame)
                    : 0;
                fft[i] = new Complex(sample * window[i], 0);
            }

            FastFourierTransform(fft);
            var bands = new float[bandCount];
            for (int band = 0; band < bandCount; band++)
            {
                int startBin = bandStarts[band];
                int endBin = bandEnds[band];
                double total = 0;
                int count = 0;
                for (int bin = startBin; bin <= endBin; bin++)
                {
                    total += fft[bin].Magnitude;
                    count++;
                }

                float magnitude = count > 0 ? (float)(total / count) : 0;
                bands[band] = magnitude;
                if (magnitude > 0.0001f)
                {
                    positiveMagnitudes.Add(magnitude);
                }
            }

            frames[frame] = bands;
        }

        NormalizeFrames(frames, positiveMagnitudes);
        spectrum = new AudioSpectrumData(frames, hopSize / (double)wave.SampleRate, bandCount);
        return true;
    }

    private static bool TryReadWaveInfo(byte[] data, out WaveInfo info)
    {
        info = default;
        if (!TryFindChunk(data, "fmt "u8, out int fmtOffset, out int fmtSize) ||
            !TryFindChunk(data, "data"u8, out int dataOffset, out int dataSize) ||
            fmtSize < 16)
        {
            return false;
        }

        ushort format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fmtOffset, sizeof(ushort)));
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fmtOffset + 2, sizeof(ushort)));
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(fmtOffset + 4, sizeof(int)));
        ushort blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fmtOffset + 12, sizeof(ushort)));
        ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fmtOffset + 14, sizeof(ushort)));
        int bytesPerSample = Math.Max(1, bitsPerSample / 8);
        if (channels == 0 ||
            sampleRate <= 0 ||
            blockAlign == 0 ||
            dataSize <= 0 ||
            dataOffset + dataSize > data.Length ||
            blockAlign < channels * bytesPerSample ||
            !IsSupportedFormat(format, bitsPerSample))
        {
            return false;
        }

        info = new WaveInfo(
            format,
            channels,
            sampleRate,
            blockAlign,
            bitsPerSample,
            bytesPerSample,
            dataOffset,
            dataSize / blockAlign);
        return true;
    }

    private static float ReadMixedSample(byte[] data, WaveInfo wave, int frame)
    {
        int frameOffset = wave.DataOffset + frame * wave.BlockAlign;
        double total = 0;
        int count = 0;
        for (int channel = 0; channel < wave.Channels; channel++)
        {
            int sampleOffset = frameOffset + channel * wave.BytesPerSample;
            if (sampleOffset + wave.BytesPerSample > data.Length)
            {
                continue;
            }

            total += ReadSample(data, sampleOffset, wave.Format, wave.BitsPerSample);
            count++;
        }

        return count > 0 ? (float)(total / count) : 0;
    }

    private static float ReadSample(byte[] data, int offset, ushort format, ushort bitsPerSample)
    {
        if (format == 3 && bitsPerSample == 32)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, sizeof(float)));
            return float.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
        }

        return bitsPerSample switch
        {
            8 => (data[offset] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, sizeof(short))) / 32768f,
            24 => ReadInt24(data, offset) / 8_388_608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int))) / 2_147_483_648f,
            _ => 0
        };
    }

    private static void BuildLogBands(int bandCount, int fftSize, int sampleRate, int[] starts, int[] ends)
    {
        double minHz = 42;
        double maxHz = Math.Min(sampleRate / 2.0, 14_000);
        int maxBin = fftSize / 2 - 1;
        for (int band = 0; band < bandCount; band++)
        {
            double startRatio = band / (double)bandCount;
            double endRatio = (band + 1) / (double)bandCount;
            double startHz = minHz * Math.Pow(maxHz / minHz, startRatio);
            double endHz = minHz * Math.Pow(maxHz / minHz, endRatio);
            int startBin = Math.Clamp((int)Math.Floor(startHz * fftSize / sampleRate), 1, maxBin);
            int endBin = Math.Clamp((int)Math.Ceiling(endHz * fftSize / sampleRate), startBin, maxBin);
            starts[band] = startBin;
            ends[band] = endBin;
        }
    }

    private static double[] BuildHannWindow(int length)
    {
        var window = new double[length];
        for (int i = 0; i < length; i++)
        {
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (length - 1));
        }

        return window;
    }

    private static void FastFourierTransform(Complex[] buffer)
    {
        int n = buffer.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
            }
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            var phaseStep = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < n; i += length)
            {
                Complex phase = Complex.One;
                int halfLength = length / 2;
                for (int j = 0; j < halfLength; j++)
                {
                    Complex even = buffer[i + j];
                    Complex odd = phase * buffer[i + j + halfLength];
                    buffer[i + j] = even + odd;
                    buffer[i + j + halfLength] = even - odd;
                    phase *= phaseStep;
                }
            }
        }
    }

    private static void NormalizeFrames(float[][] frames, List<float> positiveMagnitudes)
    {
        if (positiveMagnitudes.Count == 0)
        {
            return;
        }

        positiveMagnitudes.Sort();
        float reference = positiveMagnitudes[Math.Clamp((int)(positiveMagnitudes.Count * 0.88), 0, positiveMagnitudes.Count - 1)];
        if (reference <= 0)
        {
            return;
        }

        for (int frame = 0; frame < frames.Length; frame++)
        {
            float frameMax = 0;
            foreach (float value in frames[frame])
            {
                frameMax = Math.Max(frameMax, value);
            }

            double frameLevel = frameMax / reference;
            if (frameLevel < 0.055)
            {
                Array.Clear(frames[frame]);
                continue;
            }

            double gateScale = Math.Clamp((frameLevel - 0.055) / 0.145, 0, 1);
            double frameReference = frameMax > 0
                ? Math.Max(reference * 0.30, frameMax * 0.72)
                : reference;
            for (int band = 0; band < frames[frame].Length; band++)
            {
                double globalNormalized = frames[frame][band] / reference;
                double frameNormalized = frames[frame][band] / frameReference;
                double normalized = globalNormalized * 1.16 + frameNormalized * 0.18;
                frames[frame][band] = (float)Math.Clamp(Math.Pow(normalized, 0.60) * gateScale, 0, 1);
            }
        }
    }

    private static bool TryFindChunk(ReadOnlySpan<byte> data, ReadOnlySpan<byte> chunkId, out int dataOffset, out int dataSize)
    {
        dataOffset = 0;
        dataSize = 0;
        int offset = 12;
        while (offset + 8 <= data.Length)
        {
            ReadOnlySpan<byte> id = data.Slice(offset, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, sizeof(uint)));
            long payloadOffset = offset + 8L;
            long nextOffset = payloadOffset + size + (size & 1);
            if (nextOffset > data.Length + 1L || nextOffset <= offset)
            {
                return false;
            }

            if (id.SequenceEqual(chunkId))
            {
                dataOffset = checked((int)payloadOffset);
                dataSize = checked((int)Math.Min(size, data.Length - payloadOffset));
                return true;
            }

            offset = checked((int)nextOffset);
        }

        return false;
    }

    private static bool IsSupportedFormat(ushort format, ushort bitsPerSample)
    {
        return format switch
        {
            1 => bitsPerSample is 8 or 16 or 24 or 32,
            3 => bitsPerSample == 32,
            65534 => bitsPerSample is 8 or 16 or 24 or 32,
            _ => false
        };
    }

    private static int ReadInt24(byte[] data, int offset)
    {
        int value = data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    private readonly record struct WaveInfo(
        ushort Format,
        ushort Channels,
        int SampleRate,
        ushort BlockAlign,
        ushort BitsPerSample,
        int BytesPerSample,
        int DataOffset,
        int FrameCount);
}
