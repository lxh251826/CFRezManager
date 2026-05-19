using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CFRezManager;

internal static partial class CfgScanCommand
{
    private const int MaxConfigBytes = 8 * 1024 * 1024;

    private static readonly Regex TextureReferenceRegex = CreateTextureReferenceRegex();

    private sealed record Options(string RootPath, string ReportPath);

    private sealed record CfgScanResult(
        string Path,
        string RelativePath,
        long SourceByteCount,
        int ReadByteCount,
        int DecodedByteCount,
        string Status,
        string EncodingName,
        bool LzmaHeader,
        bool Sampled,
        int TextureReferenceCount,
        string TextureReferencePreview,
        string ErrorMessage);

    public static bool IsInvocation(string[] args)
    {
        return args.Any(arg =>
            string.Equals(arg, "--scan-cfg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "scan-cfg", StringComparison.OrdinalIgnoreCase));
    }

    public static int Run(string[] args)
    {
        try
        {
            Options options = ParseOptions(args);
            List<CfgScanResult> results = Directory
                .EnumerateFiles(options.RootPath, "*.cfg", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => ScanFile(options.RootPath, path))
                .ToList();

            string csvPath = Path.ChangeExtension(options.ReportPath, ".csv");
            WriteSummary(options.ReportPath, options, csvPath, results);
            WriteCsv(csvPath, results);

            Console.WriteLine($"CFG files: {results.Count.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Decoded text: {results.Count(result => result.Status == "decoded-text").ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Decoded LZMA: {results.Count(result => result.Status == "decoded-lzma").ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Decode failed: {results.Count(result => result.Status == "decode-failed").ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"LZMA failed: {results.Count(result => result.Status == "lzma-decode-failed").ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Read errors: {results.Count(result => result.Status == "read-error").ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Report: {options.ReportPath}");
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
        string? reportPath = null;

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (string.Equals(arg, "--scan-cfg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "scan-cfg", StringComparison.OrdinalIgnoreCase))
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
                reportPath = outputValue;
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
            throw new DirectoryNotFoundException($"CFG scan root does not exist: {rootPath}");
        }

        reportPath = string.IsNullOrWhiteSpace(reportPath)
            ? Path.Combine(rootPath, "_cfg_scan_report.txt")
            : Path.GetFullPath(reportPath);

        string? directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new Options(rootPath, reportPath);
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

    private static CfgScanResult ScanFile(string rootPath, string path)
    {
        string relativePath = Path.GetRelativePath(rootPath, path);
        try
        {
            var info = new FileInfo(path);
            byte[] data = ReadPrefix(path, MaxConfigBytes);
            bool sampled = info.Length > data.Length;
            bool lzmaHeader = LzmaAloneDecoder.IsCompressed(data);
            byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data, MaxConfigBytes);
            bool decodedLzma = prepared is not null && !ReferenceEquals(prepared, data);
            byte[] textBytes = prepared ?? data;

            if (lzmaHeader && prepared is null)
            {
                return new CfgScanResult(
                    path,
                    relativePath,
                    info.Length,
                    data.Length,
                    0,
                    "lzma-decode-failed",
                    string.Empty,
                    lzmaHeader,
                    sampled,
                    0,
                    string.Empty,
                    "LZMA header was present, but decompression failed.");
            }

            if (!TextPreviewDecoder.TryDecode(textBytes, preferKorean: false, out string text, out string encodingName))
            {
                return new CfgScanResult(
                    path,
                    relativePath,
                    info.Length,
                    data.Length,
                    textBytes.Length,
                    "decode-failed",
                    string.Empty,
                    lzmaHeader,
                    sampled,
                    0,
                    string.Empty,
                    "Text decoder rejected this CFG as binary, encrypted, or unsupported text.");
            }

            List<string> textureReferences = ExtractTextureReferences(text);
            return new CfgScanResult(
                path,
                relativePath,
                info.Length,
                data.Length,
                textBytes.Length,
                decodedLzma ? "decoded-lzma" : "decoded-text",
                encodingName,
                lzmaHeader,
                sampled,
                textureReferences.Count,
                string.Join("; ", textureReferences.Take(12)),
                string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new CfgScanResult(
                path,
                relativePath,
                0,
                0,
                0,
                "read-error",
                string.Empty,
                false,
                false,
                0,
                string.Empty,
                ex.Message);
        }
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

    private static List<string> ExtractTextureReferences(string text)
    {
        return TextureReferenceRegex
            .Matches(text)
            .Select(match => match.Value.Trim().Replace('\\', '/'))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void WriteSummary(
        string reportPath,
        Options options,
        string csvPath,
        IReadOnlyList<CfgScanResult> results)
    {
        using var writer = new StreamWriter(reportPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("CF Rez Manager CFG scan");
        writer.WriteLine();
        writer.WriteLine($"Root: {options.RootPath}");
        writer.WriteLine($"CFG files: {results.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Details CSV: {csvPath}");
        writer.WriteLine();
        writer.WriteLine("Status counts:");
        foreach (IGrouping<string, CfgScanResult> group in results
                     .GroupBy(result => result.Status)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine($"  {group.Key}: {group.Count().ToString(CultureInfo.InvariantCulture)}");
        }

        writer.WriteLine();
        writer.WriteLine($"Files with texture references: {results.Count(result => result.TextureReferenceCount > 0).ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Texture references total: {results.Sum(result => result.TextureReferenceCount).ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine();
        writer.WriteLine("Preview behavior:");
        writer.WriteLine("  decoded-text and decoded-lzma CFG files render text thumbnails with a CFG badge.");
        writer.WriteLine("  decode-failed and lzma-decode-failed CFG files now open a diagnostic fallback preview with a CFG? badge.");
        writer.WriteLine();
        WriteTopList(writer, "Decode failures", results.Where(result => result.Status == "decode-failed" || result.Status == "lzma-decode-failed"));
        WriteTopList(writer, "CFG files with texture references", results.Where(result => result.TextureReferenceCount > 0)
            .OrderByDescending(result => result.TextureReferenceCount)
            .ThenBy(result => result.RelativePath, StringComparer.OrdinalIgnoreCase));
    }

    private static void WriteTopList(TextWriter writer, string title, IEnumerable<CfgScanResult> values)
    {
        writer.WriteLine($"{title}:");
        int count = 0;
        foreach (CfgScanResult result in values.Take(80))
        {
            string detail = result.TextureReferenceCount > 0
                ? $" ({result.TextureReferenceCount.ToString(CultureInfo.InvariantCulture)} refs)"
                : string.Empty;
            writer.WriteLine($"  {result.RelativePath}{detail}");
            count++;
        }

        if (count == 0)
        {
            writer.WriteLine("  (none)");
        }

        writer.WriteLine();
    }

    private static void WriteCsv(string csvPath, IReadOnlyList<CfgScanResult> results)
    {
        using var writer = new StreamWriter(csvPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("relative_path,status,encoding,source_bytes,read_bytes,decoded_bytes,lzma_header,sampled,texture_refs,texture_ref_preview,error");
        foreach (CfgScanResult result in results)
        {
            writer.WriteLine(string.Join(
                ",",
                Csv(result.RelativePath),
                Csv(result.Status),
                Csv(result.EncodingName),
                result.SourceByteCount.ToString(CultureInfo.InvariantCulture),
                result.ReadByteCount.ToString(CultureInfo.InvariantCulture),
                result.DecodedByteCount.ToString(CultureInfo.InvariantCulture),
                Csv(result.LzmaHeader ? "yes" : "no"),
                Csv(result.Sampled ? "yes" : "no"),
                result.TextureReferenceCount.ToString(CultureInfo.InvariantCulture),
                Csv(result.TextureReferencePreview),
                Csv(result.ErrorMessage)));
        }
    }

    private static string Csv(string value)
    {
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    [GeneratedRegex(@"(?i)(?:[A-Za-z]:)?[A-Za-z0-9_ ./\\\-\[\]\(\)]+?\.(?:dtx|dds|tga|png|jpg|jpeg|bmp)")]
    private static partial Regex CreateTextureReferenceRegex();
}
