using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace CFRezManager;

internal sealed record FmodBankFsbBlock(
    int Index,
    int Offset,
    int Length,
    uint HeaderVersion,
    uint StreamCount,
    uint SampleHeaderSize,
    uint NameTableSize,
    uint SampleDataSize,
    uint ModeFlags,
    IReadOnlyList<string> SampleNames);

internal sealed record FmodBankDocument(
    string Text,
    string StorageDescription,
    int SourceByteCount,
    int DecodedByteCount,
    int FsbBlockCount,
    int StreamCount);

internal sealed record FmodBankExportResult(
    string DecodedBankPath,
    IReadOnlyList<string> FsbBlockPaths,
    int DecodedByteCount);

internal static class FmodBankDecoder
{
    public const int MaxSourceBytes = 768 * 1024 * 1024;
    public const int MaxDecodedBytes = 768 * 1024 * 1024;
    public const int MaxThumbnailSourceBytes = 96 * 1024 * 1024;

    private const int FsbHeaderLength = 0x3C;
    private const int MaxPreviewNamesPerBlock = 40;
    private const int MaxSampleNamesPerBlock = 8192;
    private const int MaxWholeBankStrings = 120;

    public static bool IsCandidate(string extension)
    {
        return string.Equals(extension, "bank", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryDecode(
        byte[] data,
        string fileName,
        out FmodBankDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (!TryPrepareDecodedData(data, out byte[]? bankData, out bool compressed, out long decodedBytes, out errorMessage) ||
            bankData is null)
        {
            return false;
        }

        if (!IsFmodBankData(bankData))
        {
            errorMessage = "BANK data is not a recognized FMOD RIFF/FEV bank.";
            return false;
        }

        IReadOnlyList<FmodBankFsbBlock> fsbBlocks = ExtractFsbBlocks(bankData);
        int streamCount = fsbBlocks.Sum(block => block.StreamCount > int.MaxValue ? int.MaxValue : (int)block.StreamCount);
        string storage = compressed
            ? "LZMA-compressed FMOD bank"
            : "FMOD bank";
        string text = BuildPreviewText(fileName, data.Length, bankData, storage, fsbBlocks);

        document = new FmodBankDocument(
            text,
            storage,
            data.Length,
            checked((int)Math.Min(decodedBytes, int.MaxValue)),
            fsbBlocks.Count,
            streamCount);
        return true;
    }

    public static FmodBankExportResult ExportDecodedFiles(byte[] data, string fileName, string outputDirectory)
    {
        if (!TryPrepareDecodedData(data, out byte[]? bankData, out _, out _, out string? errorMessage) ||
            bankData is null)
        {
            throw new InvalidDataException(errorMessage ?? "BANK compression could not be decoded.");
        }

        if (!IsFmodBankData(bankData))
        {
            throw new InvalidDataException("BANK data is not a recognized FMOD RIFF/FEV bank.");
        }

        Directory.CreateDirectory(outputDirectory);
        string baseName = MakeSafeFileName(Path.GetFileNameWithoutExtension(fileName));
        string decodedBankPath = MakeUniquePath(Path.Combine(outputDirectory, $"{baseName}.decoded.bank"));
        File.WriteAllBytes(decodedBankPath, bankData);

        var fsbPaths = new List<string>();
        foreach (FmodBankFsbBlock block in ExtractFsbBlocks(bankData))
        {
            string fsbPath = MakeUniquePath(Path.Combine(outputDirectory, $"{baseName}.fsb{block.Index:D2}.fsb"));
            using FileStream output = File.Create(fsbPath);
            output.Write(bankData.AsSpan(block.Offset, block.Length));
            fsbPaths.Add(fsbPath);
        }

        return new FmodBankExportResult(decodedBankPath, fsbPaths, bankData.Length);
    }

    public static bool IsCompressedBank(byte[] data)
    {
        return LzmaAloneDecoder.IsCompressed(data);
    }

    public static bool TryPrepareDecodedData(
        byte[] data,
        out byte[]? bankData,
        out bool compressed,
        out long decodedBytes,
        out string? errorMessage)
    {
        bankData = null;
        errorMessage = null;
        compressed = LzmaAloneDecoder.IsCompressed(data);
        decodedBytes = data.Length;

        if (!compressed)
        {
            bankData = data;
            return true;
        }

        if (!LzmaAloneDecoder.TryGetDecodedByteCount(data, out decodedBytes) ||
            decodedBytes <= 0 ||
            decodedBytes > MaxDecodedBytes ||
            decodedBytes > int.MaxValue)
        {
            errorMessage = $"BANK decoded size is too large or invalid: {decodedBytes:N0} bytes.";
            return false;
        }

        bankData = LzmaAloneDecoder.TryPrepareData(data, MaxDecodedBytes);
        if (bankData is null)
        {
            errorMessage = "BANK compression could not be decoded.";
            return false;
        }

        return true;
    }

    private static bool IsFmodBankData(byte[] data)
    {
        return data.Length >= 12 &&
               data.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
               (data.AsSpan(8, 4).SequenceEqual("FEV "u8) ||
                data.AsSpan(8, 4).SequenceEqual("FSB5"u8));
    }

    public static IReadOnlyList<FmodBankFsbBlock> ExtractFsbBlocks(byte[] data)
    {
        var blocks = new List<FmodBankFsbBlock>();
        int offset = 0;
        while ((offset = IndexOf(data, "FSB5"u8, offset)) >= 0)
        {
            if (offset + FsbHeaderLength > data.Length)
            {
                offset += 4;
                continue;
            }

            uint headerVersion = ReadUInt32(data, offset + 4);
            uint streamCount = ReadUInt32(data, offset + 8);
            uint sampleHeaderSize = ReadUInt32(data, offset + 12);
            uint nameTableSize = ReadUInt32(data, offset + 16);
            uint sampleDataSize = ReadUInt32(data, offset + 20);
            uint modeFlags = ReadUInt32(data, offset + 24);

            ulong blockLength = (ulong)FsbHeaderLength + sampleHeaderSize + nameTableSize + sampleDataSize;
            if (blockLength > int.MaxValue || offset + (long)blockLength > data.Length)
            {
                offset += 4;
                continue;
            }

            int nameOffset = checked(offset + FsbHeaderLength + (int)sampleHeaderSize);
            int sampleNameLimit = streamCount > int.MaxValue
                ? MaxSampleNamesPerBlock
                : Math.Min((int)streamCount, MaxSampleNamesPerBlock);
            IReadOnlyList<string> sampleNames = TryExtractFmodSampleNames(data, offset, (int)blockLength, sampleNameLimit);
            if (sampleNames.Count == 0 && nameTableSize > 0 && nameOffset + (long)nameTableSize <= data.Length)
            {
                sampleNames =
                    ExtractNullTerminatedStrings(data.AsSpan(nameOffset, (int)nameTableSize), sampleNameLimit)
                    .Where(IsLikelySampleName)
                    .ToList();
            }

            blocks.Add(new FmodBankFsbBlock(
                blocks.Count,
                offset,
                (int)blockLength,
                headerVersion,
                streamCount,
                sampleHeaderSize,
                nameTableSize,
                sampleDataSize,
                modeFlags,
                sampleNames));

            offset += Math.Max(4, (int)blockLength);
        }

        return blocks;
    }

    private static IReadOnlyList<string> TryExtractFmodSampleNames(byte[] data, int offset, int length, int maxCount)
    {
        if (maxCount <= 0 || offset < 0 || length <= 0 || offset + length > data.Length)
        {
            return Array.Empty<string>();
        }

        try
        {
            byte[] fsbData = data.AsSpan(offset, length).ToArray();
            Fmod5Sharp.FmodTypes.FmodSoundBank soundBank = Fmod5Sharp.FsbLoader.LoadFsbFromByteArray(fsbData);
            var names = new List<string>(Math.Min(soundBank.Samples.Count, maxCount));
            foreach (Fmod5Sharp.FmodTypes.FmodSample sample in soundBank.Samples)
            {
                if (names.Count >= maxCount)
                {
                    break;
                }

                names.Add(sample.Name ?? string.Empty);
            }

            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string BuildPreviewText(
        string fileName,
        int sourceByteCount,
        byte[] bankData,
        string storage,
        IReadOnlyList<FmodBankFsbBlock> fsbBlocks)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"FMOD bank: {fileName}");
        builder.AppendLine($"Storage: {storage}, {sourceByteCount.ToString("N0", CultureInfo.CurrentCulture)} bytes -> {bankData.Length.ToString("N0", CultureInfo.CurrentCulture)} bytes");
        builder.AppendLine($"Container: {ReadAscii(bankData, 0, 4)} / {ReadAscii(bankData, 8, 4)}");

        if (bankData.Length >= 8)
        {
            uint riffBodyBytes = ReadUInt32(bankData, 4);
            builder.AppendLine($"RIFF declared body: {riffBodyBytes.ToString("N0", CultureInfo.CurrentCulture)} bytes");
        }

        int totalStreams = fsbBlocks.Sum(block => block.StreamCount > int.MaxValue ? int.MaxValue : (int)block.StreamCount);
        long totalSampleBytes = fsbBlocks.Sum(block => (long)block.SampleDataSize);
        builder.AppendLine($"FSB5 blocks: {fsbBlocks.Count.ToString("N0", CultureInfo.CurrentCulture)}");
        builder.AppendLine($"Audio streams: {totalStreams.ToString("N0", CultureInfo.CurrentCulture)}");
        builder.AppendLine($"Sample data: {totalSampleBytes.ToString("N0", CultureInfo.CurrentCulture)} bytes");
        builder.AppendLine();

        if (fsbBlocks.Count == 0)
        {
            builder.AppendLine("No embedded FSB5 audio block was found.");
        }
        else
        {
            builder.AppendLine("Embedded FSB5 blocks:");
            foreach (FmodBankFsbBlock block in fsbBlocks)
            {
                builder.AppendLine(
                    FormattableString.Invariant(
                        $"#{block.Index:D2} offset=0x{block.Offset:X} bytes={block.Length} streams={block.StreamCount} names={block.NameTableSize} sampleData={block.SampleDataSize} mode=0x{block.ModeFlags:X} version={block.HeaderVersion}"));

                if (block.SampleNames.Count > 0)
                {
                    builder.AppendLine("  Sample names:");
                    foreach (string name in block.SampleNames.Take(MaxPreviewNamesPerBlock))
                    {
                        builder.AppendLine($"  - {name}");
                    }

                    if (block.SampleNames.Count > MaxPreviewNamesPerBlock)
                    {
                        builder.AppendLine($"  ... {block.SampleNames.Count - MaxPreviewNamesPerBlock:N0} more names");
                    }
                }
            }
        }

        IReadOnlyList<string> visibleStrings = ExtractPrintableStrings(bankData, MaxWholeBankStrings);
        if (visibleStrings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Readable strings:");
            foreach (string value in visibleStrings)
            {
                builder.AppendLine($"- {value}");
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> ExtractNullTerminatedStrings(ReadOnlySpan<byte> data, int maxCount)
    {
        if (data.IsEmpty || maxCount <= 0)
        {
            return Array.Empty<string>();
        }

        var strings = new List<string>();
        int start = 0;
        for (int i = 0; i <= data.Length; i++)
        {
            bool atEnd = i == data.Length;
            if (!atEnd && data[i] != 0)
            {
                continue;
            }

            if (i > start)
            {
                string value = DecodePrintable(data[start..i]).Trim();
                if (value.Length >= 2)
                {
                    strings.Add(value);
                    if (strings.Count >= maxCount)
                    {
                        break;
                    }
                }
            }

            start = i + 1;
        }

        return strings;
    }

    private static IReadOnlyList<string> ExtractPrintableStrings(byte[] data, int maxCount)
    {
        int scanLength = Math.Min(data.Length, 4 * 1024 * 1024);
        var strings = new List<string>();
        int start = -1;

        for (int i = 0; i < scanLength; i++)
        {
            if (IsPrintable(data[i]))
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else if (start >= 0)
            {
                AddPrintableString(data.AsSpan(start, i - start), strings, maxCount);
                if (strings.Count >= maxCount)
                {
                    break;
                }

                start = -1;
            }
        }

        if (start >= 0 && strings.Count < maxCount)
        {
            AddPrintableString(data.AsSpan(start, scanLength - start), strings, maxCount);
        }

        return strings;
    }

    private static void AddPrintableString(ReadOnlySpan<byte> value, List<string> strings, int maxCount)
    {
        if (value.Length < 4 || strings.Count >= maxCount)
        {
            return;
        }

        string text = DecodePrintable(value).Trim();
        if (text.Length >= 4 && !strings.Contains(text, StringComparer.Ordinal))
        {
            strings.Add(text);
        }
    }

    private static string DecodePrintable(ReadOnlySpan<byte> value)
    {
        Span<char> chars = value.Length <= 512 ? stackalloc char[value.Length] : new char[value.Length];
        int count = 0;
        foreach (byte b in value)
        {
            chars[count++] = IsPrintable(b) ? (char)b : ' ';
        }

        return new string(chars[..count]);
    }

    private static bool IsPrintable(byte value)
    {
        return value is >= 0x20 and <= 0x7E;
    }

    private static bool IsLikelySampleName(string value)
    {
        if (value.Length < 2)
        {
            return false;
        }

        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '/' or '\\' or '.')
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> signature, int start)
    {
        for (int i = start; i <= data.Length - signature.Length; i++)
        {
            if (data.AsSpan(i, signature.Length).SequenceEqual(signature))
            {
                return i;
            }
        }

        return -1;
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));
    }

    private static string ReadAscii(byte[] data, int offset, int length)
    {
        if (offset < 0 || length <= 0 || offset + length > data.Length)
        {
            return string.Empty;
        }

        return Encoding.ASCII.GetString(data, offset, length);
    }

    private static string MakeSafeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "bank" : value;
    }

    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string fileName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int index = 1; ; index++)
        {
            string candidate = Path.Combine(directory, $"{fileName} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
