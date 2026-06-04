using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CFRezManager;

internal static partial class CfgDecodeCommand
{
    private const int MaxConfigBytes = 8 * 1024 * 1024;
    private const int MaxRezPhaseBytes = 8 * 1024;

    private static readonly HashSet<string> KnownSystemConfigNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "appinfo.cfg",
        "commonconfig.cfg",
        "dirsystemconfig.cfg",
        "updateconfig.cfg"
    };

    private sealed record Options(string RootPath, string OutputDirectory, string? FailedListPath);

    private sealed record DecodeResult(
        string Path,
        string RelativePath,
        long SourceByteCount,
        string Status,
        string Method,
        string OutputPath,
        string Notes);

    private sealed record StructuredTextCandidate(string Text, string EncodingName, int Score, string Method);

    public static bool IsInvocation(string[] args)
    {
        return args.Any(arg =>
            string.Equals(arg, "--decode-cfg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "decode-cfg", StringComparison.OrdinalIgnoreCase));
    }

    public static int Run(string[] args)
    {
        try
        {
            Options options = ParseOptions(args);
            List<string> files = ResolveInputFiles(options);
            List<DecodeResult> results = files
                .Select(path => DecodeFile(options, path))
                .ToList();

            string reportPath = Path.Combine(options.OutputDirectory, "_cfg_decode_report.txt");
            string csvPath = Path.Combine(options.OutputDirectory, "_cfg_decode_report.csv");
            WriteSummary(reportPath, csvPath, options, results);

            Console.WriteLine($"Input CFG files: {results.Count.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Decoded text: {results.Count(result => result.Status.StartsWith("decoded", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Binary strip previews: {results.Count(result => result.Status == "binary-strip-preview").ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"System encrypted: {results.Count(result => result.Status == "system-encrypted").ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Unresolved: {results.Count(result => result.Status == "unresolved").ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Output: {options.OutputDirectory}");
            Console.WriteLine($"Report: {reportPath}");
            Console.WriteLine($"Details: {csvPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Options ParseOptions(string[] args)
    {
        string? rootPath = null;
        string? outputDirectory = null;
        string? failedListPath = null;

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (string.Equals(arg, "--decode-cfg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "decode-cfg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--root", out string? rootValue) ||
                TryReadOptionValue(args, ref index, "--source-root", out rootValue) ||
                TryReadOptionValue(args, ref index, "--input", out rootValue))
            {
                rootPath = rootValue;
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--output", out string? outputValue) ||
                TryReadOptionValue(args, ref index, "-o", out outputValue))
            {
                outputDirectory = outputValue;
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--failed-list", out string? failedValue))
            {
                failedListPath = failedValue;
                continue;
            }

            rootPath ??= arg;
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new InvalidOperationException("Missing --root <directory>.");
        }

        rootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"CFG decode root does not exist: {rootPath}");
        }

        if (!string.IsNullOrWhiteSpace(failedListPath))
        {
            failedListPath = Path.GetFullPath(failedListPath);
            if (!File.Exists(failedListPath))
            {
                throw new FileNotFoundException($"Failed-list file does not exist: {failedListPath}", failedListPath);
            }
        }
        else
        {
            string defaultFailedList = Path.Combine(rootPath, "_cfg_failed_list.txt");
            if (File.Exists(defaultFailedList))
            {
                failedListPath = defaultFailedList;
            }
        }

        outputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(rootPath, "_cfg_decode")
            : Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(Path.Combine(outputDirectory, "decoded"));
        Directory.CreateDirectory(Path.Combine(outputDirectory, "strip_previews"));

        return new Options(rootPath, outputDirectory, failedListPath);
    }

    private static bool TryReadOptionValue(string[] args, ref int index, string optionName, out string? value)
    {
        value = null;
        string arg = args[index];
        if (!string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Missing value for {optionName}.");
        }

        index++;
        value = args[index];
        return true;
    }

    private static List<string> ResolveInputFiles(Options options)
    {
        IEnumerable<string> files;
        if (!string.IsNullOrWhiteSpace(options.FailedListPath))
        {
            files = File.ReadLines(options.FailedListPath!)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Select(line => Path.IsPathRooted(line)
                    ? Path.GetFullPath(line)
                    : Path.GetFullPath(Path.Combine(options.RootPath, line)))
                .Where(File.Exists);
        }
        else
        {
            files = Directory.EnumerateFiles(options.RootPath, "*.cfg", SearchOption.AllDirectories);
        }

        return files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DecodeResult DecodeFile(Options options, string path)
    {
        string relativePath = Path.GetRelativePath(options.RootPath, path);
        byte[] data = ReadPrefix(path, MaxConfigBytes);
        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data, MaxConfigBytes);
        bool lzmaExpanded = prepared is not null && !ReferenceEquals(prepared, data);
        byte[] preparedBytes = prepared ?? data;

        if (TryDecodeStructuredText(preparedBytes, lzmaExpanded ? "lzma-text" : "plain-text", out StructuredTextCandidate? plainCandidate))
        {
            StructuredTextCandidate candidate = plainCandidate!;
            string outputPath = WriteDecodedText(options.OutputDirectory, relativePath, candidate.Text);
            return new DecodeResult(
                path,
                relativePath,
                data.Length,
                lzmaExpanded ? "decoded-lzma" : "decoded-text",
                $"{candidate.Method} / {candidate.EncodingName}",
                outputPath,
                $"score={candidate.Score.ToString(CultureInfo.InvariantCulture)}");
        }

        if (TryDecodeEncText(preparedBytes, out StructuredTextCandidate? encCandidate) ||
            (lzmaExpanded && TryDecodeEncText(data, out encCandidate)))
        {
            StructuredTextCandidate candidate = encCandidate!;
            string outputPath = WriteDecodedText(options.OutputDirectory, relativePath, candidate.Text);
            return new DecodeResult(
                path,
                relativePath,
                data.Length,
                "decoded-enc",
                $"{candidate.Method} / {candidate.EncodingName}",
                outputPath,
                $"score={candidate.Score.ToString(CultureInfo.InvariantCulture)}");
        }

        if (preparedBytes.Length <= MaxRezPhaseBytes &&
            TryDecodeRezPhase(preparedBytes, out StructuredTextCandidate? rezCandidate))
        {
            StructuredTextCandidate candidate = rezCandidate!;
            string outputPath = WriteDecodedText(options.OutputDirectory, relativePath, candidate.Text);
            return new DecodeResult(
                path,
                relativePath,
                data.Length,
                "decoded-rez-phase",
                $"{candidate.Method} / {candidate.EncodingName}",
                outputPath,
                $"score={candidate.Score.ToString(CultureInfo.InvariantCulture)}");
        }

        if (CfgBinaryStripDecoder.TryWritePng(
                preparedBytes,
                BuildPreviewPath(options.OutputDirectory, relativePath),
                out CfgBinaryStripInfo stripInfo))
        {
            string outputPath = BuildPreviewPath(options.OutputDirectory, relativePath);
            return new DecodeResult(
                path,
                relativePath,
                data.Length,
                "binary-strip-preview",
                "raw-rgb-strip",
                outputPath,
                CfgBinaryStripDecoder.Describe(stripInfo));
        }

        if (LooksLikeSystemEncryptedConfig(relativePath, preparedBytes))
        {
            return new DecodeResult(
                path,
                relativePath,
                data.Length,
                "system-encrypted",
                "launcher-or-protection-config",
                string.Empty,
                "High-entropy TCLS/TenProtect-style config; not a model material text CFG.");
        }

        return new DecodeResult(
            path,
            relativePath,
            data.Length,
            "unresolved",
            "unknown",
            string.Empty,
            "No supported text decode succeeded.");
    }

    private static string WriteDecodedText(string outputRoot, string relativePath, string text)
    {
        string outputPath = Path.Combine(outputRoot, "decoded", NormalizeRelativePath(relativePath));
        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(outputPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return outputPath;
    }

    private static string BuildPreviewPath(string outputRoot, string relativePath)
    {
        return Path.Combine(outputRoot, "strip_previews", NormalizeRelativePath(relativePath) + ".png");
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
    }

    private static bool TryDecodeStructuredText(byte[] data, string method, out StructuredTextCandidate? candidate)
    {
        candidate = null;
        if (!TextPreviewDecoder.TryDecode(data, preferKorean: false, out string text, out string encodingName))
        {
            return false;
        }

        int score = ScoreStructuredText(text);
        if (score < 12)
        {
            return false;
        }

        candidate = new StructuredTextCandidate(text, encodingName, score, method);
        return true;
    }

    private static bool TryDecodeEncText(byte[] data, out StructuredTextCandidate? candidate)
    {
        candidate = null;
        if (!EncTextDecoder.TryDecode(data, out byte[] decoded))
        {
            return false;
        }

        return TryDecodeStructuredText(decoded, "enc-base64", out candidate);
    }

    private static bool TryDecodeRezPhase(byte[] data, out StructuredTextCandidate? candidate)
    {
        candidate = null;
        StructuredTextCandidate? best = null;

        for (int phase = 0; phase < RezCrypto.KeyLength; phase++)
        {
            if (TryDecodeRezPhaseVariant(data, phase, decode: true, out StructuredTextCandidate? decodedCandidate) &&
                (best is null || decodedCandidate!.Score > best.Score))
            {
                best = decodedCandidate;
            }

            if (TryDecodeRezPhaseVariant(data, phase, decode: false, out StructuredTextCandidate? encodedCandidate) &&
                (best is null || encodedCandidate!.Score > best.Score))
            {
                best = encodedCandidate;
            }

            if (best is not null && best.Score >= 100)
            {
                break;
            }
        }

        candidate = best;
        return candidate is not null;
    }

    private static bool TryDecodeRezPhaseVariant(byte[] data, int phase, bool decode, out StructuredTextCandidate? candidate)
    {
        candidate = null;
        byte[] buffer = data.ToArray();
        if (decode)
        {
            RezCrypto.Decode(buffer, phase);
        }
        else
        {
            RezCrypto.Encode(buffer, phase);
        }

        if (!TryDecodeStructuredText(buffer, decode ? $"rez-decode-phase-{phase}" : $"rez-encode-phase-{phase}", out StructuredTextCandidate? decoded))
        {
            return false;
        }

        candidate = decoded;
        return true;
    }

    private static int ScoreStructuredText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        int score = 0;
        if (text.Contains("[Textures]", StringComparison.OrdinalIgnoreCase))
        {
            score += 120;
        }

        if (text.Contains("SpecularMapName", StringComparison.OrdinalIgnoreCase))
        {
            score += 80;
        }

        if (text.Contains("NormalMapName", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("EnvCubeMapName", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("DiffuseMappingEnabled", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        if (text.Contains("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            score += 60;
        }

        if (Regex.IsMatch(text, @"(?m)^\s*\[[^\]\r\n]{1,80}\]\s*$"))
        {
            score += 24;
        }

        int assignmentCount = Regex.Matches(text, @"(?m)^\s*[^=\r\n]{1,120}=\s*.+$").Count;
        score += Math.Min(40, assignmentCount * 4);

        int textureReferenceCount = Regex.Matches(text, @"(?i)\.(png|dds|dtx|tga|jpg|jpeg|bmp|bin)\b").Count;
        score += Math.Min(30, textureReferenceCount * 3);

        int lineCount = text.Count(ch => ch == '\n') + 1;
        if (lineCount >= 4)
        {
            score += 8;
        }

        int suspicious = text.Count(ch => ch == '\uFFFD' || (char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')));
        score -= suspicious * 10;

        return score;
    }

    private static bool LooksLikeSystemEncryptedConfig(string relativePath, byte[] data)
    {
        string fileName = NormalizeDuplicateSuffix(Path.GetFileName(relativePath));
        if (KnownSystemConfigNames.Contains(fileName))
        {
            return true;
        }

        HashSet<byte> unique = [];
        int printable = 0;
        foreach (byte value in data)
        {
            unique.Add(value);
            if (value is >= 0x20 and <= 0x7E or 0x09 or 0x0A or 0x0D)
            {
                printable++;
            }
        }

        double printableRatio = data.Length == 0 ? 0 : printable / (double)data.Length;
        return unique.Count >= 90 && printableRatio >= 0.25 && printableRatio <= 0.45;
    }

    private static string NormalizeDuplicateSuffix(string fileName)
    {
        return DuplicateSuffixRegex().Replace(fileName, string.Empty);
    }

    private static byte[] ReadPrefix(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int length = (int)Math.Min(stream.Length, maxBytes);
        byte[] data = new byte[length];
        int offset = 0;
        while (offset < data.Length)
        {
            int read = stream.Read(data, offset, data.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (offset == data.Length)
        {
            return data;
        }

        Array.Resize(ref data, offset);
        return data;
    }

    private static void WriteSummary(string reportPath, string csvPath, Options options, IReadOnlyList<DecodeResult> results)
    {
        using (var writer = new StreamWriter(reportPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.WriteLine("CFG decode report");
            writer.WriteLine();
            writer.WriteLine($"Root: {options.RootPath}");
            writer.WriteLine($"Output: {options.OutputDirectory}");
            writer.WriteLine($"Failed list: {options.FailedListPath ?? "<all cfg files>"}");
            writer.WriteLine($"Files: {results.Count.ToString(CultureInfo.InvariantCulture)}");
            writer.WriteLine($"Decoded text: {results.Count(result => result.Status.StartsWith("decoded", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture)}");
            writer.WriteLine($"Binary strip previews: {results.Count(result => result.Status == "binary-strip-preview").ToString(CultureInfo.InvariantCulture)}");
            writer.WriteLine($"System encrypted: {results.Count(result => result.Status == "system-encrypted").ToString(CultureInfo.InvariantCulture)}");
            writer.WriteLine($"Unresolved: {results.Count(result => result.Status == "unresolved").ToString(CultureInfo.InvariantCulture)}");
            writer.WriteLine();

            foreach (IGrouping<string, DecodeResult> group in results.GroupBy(result => result.Status))
            {
                writer.WriteLine(group.Key);
                foreach (DecodeResult result in group.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    writer.WriteLine($"- {result.RelativePath}");
                    writer.WriteLine($"  Method: {result.Method}");
                    if (!string.IsNullOrWhiteSpace(result.OutputPath))
                    {
                        writer.WriteLine($"  Output: {result.OutputPath}");
                    }

                    if (!string.IsNullOrWhiteSpace(result.Notes))
                    {
                        writer.WriteLine($"  Notes: {result.Notes}");
                    }
                }

                writer.WriteLine();
            }
        }

        using var csv = new StreamWriter(csvPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        csv.WriteLine("RelativePath,SourceBytes,Status,Method,OutputPath,Notes");
        foreach (DecodeResult result in results.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            csv.WriteLine(string.Join(",",
                EscapeCsv(result.RelativePath),
                result.SourceByteCount.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(result.Status),
                EscapeCsv(result.Method),
                EscapeCsv(result.OutputPath),
                EscapeCsv(result.Notes)));
        }
    }

    private static string EscapeCsv(string value)
    {
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    [GeneratedRegex(@"\s+\(\d+\)(?=\.[^.]+$)", RegexOptions.CultureInvariant)]
    private static partial Regex DuplicateSuffixRegex();
}
