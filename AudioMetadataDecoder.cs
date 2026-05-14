using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace CFRezManager;

internal sealed record AudioMetadata(
    string Container,
    string Codec,
    int? SampleRate,
    int? Channels,
    int? BitRateKbps,
    TimeSpan? Duration,
    string PreferredExtension)
{
    public string Description
    {
        get
        {
            var parts = new List<string> { Codec };
            if (SampleRate is > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{SampleRate.Value / 1000.0:0.#} kHz"));
            }

            if (BitRateKbps is > 0)
            {
                parts.Add($"{BitRateKbps.Value:N0} kbps");
            }

            if (Channels is > 0)
            {
                parts.Add(FormatChannels(Channels.Value));
            }

            if (Duration is { } duration)
            {
                parts.Add(FormatDuration(duration));
            }

            return string.Join(", ", parts);
        }
    }

    private static string FormatChannels(int channels)
    {
        return channels switch
        {
            1 => "mono",
            2 => "stereo",
            _ => $"{channels:N0} ch"
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}

internal static class AudioMetadataDecoder
{
    public static bool TryRead(byte[] data, string fileName, out AudioMetadata metadata)
    {
        if (IsWaveData(data))
        {
            return TryReadWave(data, out metadata) ||
                   TryReadByExtension(data, fileName, out metadata);
        }

        if (IsOggData(data))
        {
            return TryReadOgg(data, out metadata) ||
                   TryReadByExtension(data, fileName, out metadata);
        }

        if (TryReadMp3(data, out metadata))
        {
            return true;
        }

        return TryReadByExtension(data, fileName, out metadata);
    }

    public static bool IsSupportedExtension(string extension)
    {
        extension = NormalizeExtension(extension);
        return string.Equals(extension, "wav", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, "wave", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, "mp3", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, "ogg", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWaveData(byte[] data)
    {
        return data.Length >= 12 &&
               data.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
               data.AsSpan(8, 4).SequenceEqual("WAVE"u8);
    }

    public static bool IsOggData(byte[] data)
    {
        return data.Length >= 27 && data.AsSpan(0, 4).SequenceEqual("OggS"u8);
    }

    private static bool TryReadWave(byte[] data, out AudioMetadata metadata)
    {
        metadata = new AudioMetadata("WAVE", "WAVE audio", null, null, null, null, "wav");
        if (!IsWaveData(data) ||
            !TryFindRiffChunk(data, "fmt "u8, out int fmtOffset, out int fmtSize) ||
            fmtSize < 16)
        {
            return false;
        }

        ushort format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fmtOffset, sizeof(ushort)));
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fmtOffset + 2, sizeof(ushort)));
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(fmtOffset + 4, sizeof(uint)));
        uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(fmtOffset + 8, sizeof(uint)));
        ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fmtOffset + 14, sizeof(ushort)));
        TimeSpan? duration = null;
        if (byteRate > 0 &&
            TryFindRiffChunk(data, "data"u8, out _, out int dataSize))
        {
            duration = TimeSpan.FromSeconds(dataSize / (double)byteRate);
        }

        metadata = new AudioMetadata(
            "WAVE",
            $"WAVE {FormatWaveFormat(format)} {bitsPerSample:N0}-bit",
            checked((int)Math.Min(sampleRate, int.MaxValue)),
            channels,
            null,
            duration,
            "wav");
        return true;
    }

    private static bool TryReadMp3(byte[] data, out AudioMetadata metadata)
    {
        metadata = new AudioMetadata("MP3", "MP3 audio", null, null, null, null, "mp3");
        int offset = SkipId3v2(data);
        int searchLimit = Math.Min(data.Length - 4, offset + 1024 * 1024);
        for (int i = Math.Max(0, offset); i <= searchLimit; i++)
        {
            if (TryParseMp3Frame(data, i, out Mp3Frame frame))
            {
                long audioBytes = Math.Max(0, data.Length - i - (HasId3v1Tag(data) ? 128 : 0));
                TimeSpan? duration = frame.BitRateKbps > 0
                    ? TimeSpan.FromSeconds(audioBytes * 8.0 / (frame.BitRateKbps * 1000.0))
                    : null;
                metadata = new AudioMetadata(
                    "MP3",
                    frame.Codec,
                    frame.SampleRate,
                    frame.Channels,
                    frame.BitRateKbps,
                    duration,
                    "mp3");
                return true;
            }
        }

        return false;
    }

    private static bool TryReadOgg(byte[] data, out AudioMetadata metadata)
    {
        metadata = new AudioMetadata("Ogg", "Ogg audio", null, null, null, null, "ogg");
        if (data.Length < 27 || !data.AsSpan(0, 4).SequenceEqual("OggS"u8))
        {
            return false;
        }

        int offset = 0;
        long lastGranule = -1;
        string? codec = null;
        int? sampleRate = null;
        int? channels = null;
        int granuleRate = 0;
        var packet = new List<byte>();

        while (offset + 27 <= data.Length)
        {
            if (!data.AsSpan(offset, 4).SequenceEqual("OggS"u8))
            {
                break;
            }

            long granule = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset + 6, sizeof(long)));
            if (granule >= 0)
            {
                lastGranule = granule;
            }

            int segmentCount = data[offset + 26];
            int segmentTableOffset = offset + 27;
            int pageDataOffset = segmentTableOffset + segmentCount;
            if (pageDataOffset > data.Length)
            {
                break;
            }

            int pageDataSize = 0;
            for (int i = 0; i < segmentCount; i++)
            {
                pageDataSize += data[segmentTableOffset + i];
            }

            if (pageDataOffset + pageDataSize > data.Length)
            {
                break;
            }

            int payloadOffset = pageDataOffset;
            for (int i = 0; i < segmentCount; i++)
            {
                int segmentSize = data[segmentTableOffset + i];
                if (segmentSize > 0)
                {
                    packet.AddRange(data.AsSpan(payloadOffset, segmentSize).ToArray());
                    payloadOffset += segmentSize;
                }

                if (segmentSize < 255)
                {
                    if (TryParseOggIdentification(packet.ToArray(), out codec, out sampleRate, out channels, out granuleRate))
                    {
                        packet.Clear();
                    }
                    else
                    {
                        packet.Clear();
                    }
                }
            }

            offset = pageDataOffset + pageDataSize;
        }

        if (codec is null)
        {
            return false;
        }

        TimeSpan? duration = lastGranule >= 0 && granuleRate > 0
            ? TimeSpan.FromSeconds(lastGranule / (double)granuleRate)
            : null;
        metadata = new AudioMetadata("Ogg", codec, sampleRate, channels, null, duration, "ogg");
        return true;
    }

    private static bool TryReadByExtension(byte[] data, string fileName, out AudioMetadata metadata)
    {
        string extension = NormalizeExtension(Path.GetExtension(fileName));
        metadata = extension switch
        {
            "mp3" => new AudioMetadata("MP3", "MP3 audio", null, null, null, null, "mp3"),
            "ogg" => new AudioMetadata("Ogg", "Ogg audio", null, null, null, null, "ogg"),
            "wav" or "wave" => new AudioMetadata("WAVE", "WAVE audio", null, null, null, null, "wav"),
            _ => new AudioMetadata("Audio", "Audio", null, null, null, null, "bin")
        };

        return data.Length > 0 && IsSupportedExtension(extension);
    }

    private static bool TryFindRiffChunk(ReadOnlySpan<byte> data, ReadOnlySpan<byte> chunkId, out int dataOffset, out int dataSize)
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

    private static int SkipId3v2(byte[] data)
    {
        if (data.Length < 10 || !data.AsSpan(0, 3).SequenceEqual("ID3"u8))
        {
            return 0;
        }

        int size = (data[6] & 0x7F) << 21 |
                   (data[7] & 0x7F) << 14 |
                   (data[8] & 0x7F) << 7 |
                   (data[9] & 0x7F);
        int footerBytes = (data[5] & 0x10) != 0 ? 10 : 0;
        return Math.Min(data.Length, 10 + size + footerBytes);
    }

    private static bool HasId3v1Tag(byte[] data)
    {
        return data.Length >= 128 && data.AsSpan(data.Length - 128, 3).SequenceEqual("TAG"u8);
    }

    private static bool TryParseMp3Frame(byte[] data, int offset, out Mp3Frame frame)
    {
        frame = default;
        if (offset + 4 > data.Length ||
            data[offset] != 0xFF ||
            (data[offset + 1] & 0xE0) != 0xE0)
        {
            return false;
        }

        int versionBits = (data[offset + 1] >> 3) & 0x03;
        int layerBits = (data[offset + 1] >> 1) & 0x03;
        int bitRateIndex = (data[offset + 2] >> 4) & 0x0F;
        int sampleRateIndex = (data[offset + 2] >> 2) & 0x03;
        if (versionBits == 0x01 ||
            layerBits == 0 ||
            bitRateIndex is 0 or 0x0F ||
            sampleRateIndex == 0x03)
        {
            return false;
        }

        string version = versionBits switch
        {
            0 => "MPEG 2.5",
            2 => "MPEG 2",
            3 => "MPEG 1",
            _ => "MPEG"
        };
        string layer = layerBits switch
        {
            3 => "Layer I",
            2 => "Layer II",
            1 => "Layer III",
            _ => "Layer"
        };

        int bitRate = GetMp3BitRate(versionBits, layerBits, bitRateIndex);
        int sampleRate = GetMp3SampleRate(versionBits, sampleRateIndex);
        if (bitRate <= 0 || sampleRate <= 0)
        {
            return false;
        }

        int channelMode = (data[offset + 3] >> 6) & 0x03;
        int channels = channelMode == 3 ? 1 : 2;
        frame = new Mp3Frame($"{version} {layer}", sampleRate, channels, bitRate);
        return true;
    }

    private static int GetMp3BitRate(int versionBits, int layerBits, int index)
    {
        int[] mpeg1Layer1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
        int[] mpeg1Layer2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
        int[] mpeg1Layer3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        int[] mpeg2Layer1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
        int[] mpeg2Layer23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

        return versionBits == 3
            ? layerBits switch
            {
                3 => mpeg1Layer1[index],
                2 => mpeg1Layer2[index],
                _ => mpeg1Layer3[index]
            }
            : layerBits == 3
                ? mpeg2Layer1[index]
                : mpeg2Layer23[index];
    }

    private static int GetMp3SampleRate(int versionBits, int index)
    {
        int[] mpeg1 = [44100, 48000, 32000];
        int[] mpeg2 = [22050, 24000, 16000];
        int[] mpeg25 = [11025, 12000, 8000];
        return versionBits switch
        {
            3 => mpeg1[index],
            2 => mpeg2[index],
            0 => mpeg25[index],
            _ => 0
        };
    }

    private static bool TryParseOggIdentification(
        byte[] packet,
        out string? codec,
        out int? sampleRate,
        out int? channels,
        out int granuleRate)
    {
        codec = null;
        sampleRate = null;
        channels = null;
        granuleRate = 0;

        if (packet.Length >= 30 &&
            packet[0] == 0x01 &&
            packet.AsSpan(1, 6).SequenceEqual("vorbis"u8))
        {
            channels = packet[11];
            sampleRate = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12, sizeof(int)));
            granuleRate = sampleRate.Value;
            codec = "Ogg Vorbis";
            return true;
        }

        if (packet.Length >= 19 && packet.AsSpan(0, 8).SequenceEqual("OpusHead"u8))
        {
            channels = packet[9];
            int inputSampleRate = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12, sizeof(int)));
            sampleRate = inputSampleRate > 0 ? inputSampleRate : 48000;
            granuleRate = 48000;
            codec = "Ogg Opus";
            return true;
        }

        if (packet.Length >= 7 && packet.AsSpan(0, 7).SequenceEqual("Speex  "u8))
        {
            codec = "Ogg Speex";
            granuleRate = 0;
            return true;
        }

        return false;
    }

    private static string FormatWaveFormat(ushort format)
    {
        return format switch
        {
            1 => "PCM",
            3 => "float",
            6 => "A-law",
            7 => "mu-law",
            17 => "IMA ADPCM",
            85 => "MP3",
            65534 => "extensible",
            _ => $"format {format}"
        };
    }

    private static string NormalizeExtension(string extension)
    {
        return extension.Trim().TrimStart('.');
    }

    private readonly record struct Mp3Frame(string Codec, int SampleRate, int Channels, int BitRateKbps);
}
