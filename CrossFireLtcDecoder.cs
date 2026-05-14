using System.Diagnostics;
using System.IO;
using System.Text;

namespace CFRezManager;

internal sealed record CrossFireLtcTextDocument(
    string Text,
    string EncodingName,
    string StorageDescription,
    int SourceByteCount,
    int DecodedByteCount);

internal static class CrossFireLtcDecoder
{
    private const int ExternalConverterTimeoutMilliseconds = 15_000;

    // CrossFire stores standard LithTech LTC with a small repeating XOR layer.
    private static readonly byte[] CrossFireLtcMagic = [0x54, 0x83, 0xB2, 0xE1];
    private static readonly byte[] CrossFireLtcXorKey =
    [
        0x54, 0x83, 0xB2, 0xE1,
        0x10, 0x3F, 0x6E, 0x9D,
        0xCC, 0xFB, 0x2A, 0x59,
        0x88, 0xB7, 0xE6, 0x15
    ];

    public static bool IsCandidate(string extension)
    {
        return string.Equals(extension, "ltc", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasCrossFireMagic(byte[] data)
    {
        return HasPrefix(data, CrossFireLtcMagic);
    }

    public static string GetUnsupportedMessage(string? converterError)
    {
        if (!string.IsNullOrWhiteSpace(converterError))
        {
            return converterError;
        }

        return "检测到 CrossFire LTC 头 54 83 B2 E1。外层 XOR 密钥已内置，解锁后会优先使用内置 LithTech LTC 解码器；如果仍失败，可在 tools 目录放置 LTC.exe，或设置 CFREZ_LTC_TO_LTA 指向可用的 LTC->LTA 转换器。";
    }

    public static bool TryUnlockCrossFirePayload(byte[] data, out byte[] unlocked)
    {
        unlocked = data;
        if (!HasCrossFireMagic(data))
        {
            return false;
        }

        unlocked = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            unlocked[i] = (byte)(data[i] ^ CrossFireLtcXorKey[i & 15]);
        }

        return true;
    }

    public static bool TryDecodeText(
        byte[] data,
        string fallbackName,
        out CrossFireLtcTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data);
        if (prepared is not null &&
            TextPreviewDecoder.TryDecode(prepared, preferKorean: false, out string directText, out string directEncoding) &&
            LooksLikeStructuredText(directText))
        {
            string directStorage = ReferenceEquals(prepared, data) ? "LTC text" : "LZMA-compressed LTC";
            document = new CrossFireLtcTextDocument(directText, directEncoding, directStorage, data.Length, prepared.Length);
            return true;
        }

        if (TryConvertToText(data, fallbackName, out document, out errorMessage))
        {
            return true;
        }

        if (HasCrossFireMagic(data))
        {
            errorMessage = GetUnsupportedMessage(errorMessage);
            return false;
        }

        errorMessage = string.IsNullOrWhiteSpace(errorMessage)
            ? "LTC 不是可直接识别的文本/压缩文本，也未能用内置 LTC 解码器还原为 LTA。可配置 CFREZ_LTC_TO_LTA 作为外部兜底。"
            : errorMessage;
        return false;
    }

    public static bool TryConvertToText(
        byte[] ltcData,
        string fallbackName,
        out CrossFireLtcTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        bool isCrossFireLocked = TryUnlockCrossFirePayload(ltcData, out byte[] converterInput);
        if (TryDecodeNativeToText(converterInput, ltcData.Length, isCrossFireLocked, out document, out string? nativeError) &&
            document is not null)
        {
            return true;
        }

        string? converterPath = ResolveLtcConverterPath();
        if (converterPath is null)
        {
            errorMessage = string.IsNullOrWhiteSpace(nativeError)
                ? "内置 LTC 解码失败，且未找到外部 LTC->LTA 转换器。"
                : nativeError;
            return false;
        }

        string workingDirectory = Path.Combine(Path.GetTempPath(), "CFRezManager", "LtcConvert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        string baseName = Path.GetFileNameWithoutExtension(fallbackName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "model";
        }

        string inputPath = Path.Combine(workingDirectory, $"{baseName}.ltc");
        string outputPath = Path.Combine(workingDirectory, $"{baseName}.lta");
        File.WriteAllBytes(inputPath, converterInput);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = converterPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            AddConverterArguments(startInfo, converterPath, inputPath, outputPath);

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                errorMessage = $"无法启动 LTC 转换器: {converterPath}";
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ExternalConverterTimeoutMilliseconds))
            {
                TryKillProcess(process);
                errorMessage = "LTC 转换器转换超时。请检查 CFREZ_LTC_TO_LTA 或 tools\\ltc_to_lta.cmd 的命令行参数。";
                return false;
            }

            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                errorMessage = string.IsNullOrWhiteSpace(detail)
                    ? $"LTC 转换器转换失败，退出码 {process.ExitCode}。"
                    : detail.Trim();
                return false;
            }

            byte[] convertedBytes = File.ReadAllBytes(outputPath);
            if (!TextPreviewDecoder.TryDecode(convertedBytes, preferKorean: isCrossFireLocked, out string text, out string encodingName))
            {
                errorMessage = "LTC 转换器已输出文件，但文本编码无法识别。";
                return false;
            }

            string storageDescription = isCrossFireLocked ? "CrossFire LTC XOR -> LTA" : "LTC -> LTA";
            document = new CrossFireLtcTextDocument(
                text,
                encodingName,
                storageDescription,
                ltcData.Length,
                convertedBytes.Length);
            return true;
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    private static bool TryDecodeNativeToText(
        byte[] converterInput,
        int sourceByteCount,
        bool isCrossFireLocked,
        out CrossFireLtcTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (!LithTechLtcNativeDecoder.TryDecode(converterInput, out byte[] convertedBytes, out string? nativeError))
        {
            errorMessage = nativeError;
            return false;
        }

        if (!TextPreviewDecoder.TryDecode(convertedBytes, preferKorean: isCrossFireLocked, out string text, out string encodingName) ||
            !LooksLikeStructuredText(text))
        {
            errorMessage = "内置 LTC 解码器已输出数据，但结果不是可识别的 LTA 文本。";
            return false;
        }

        string storageDescription = isCrossFireLocked ? "CrossFire LTC native -> LTA" : "LTC native -> LTA";
        document = new CrossFireLtcTextDocument(
            text,
            encodingName,
            storageDescription,
            sourceByteCount,
            convertedBytes.Length);
        return true;
    }

    private static string? ResolveLtcConverterPath()
    {
        string? configured = Environment.GetEnvironmentVariable("CFREZ_LTC_TO_LTA");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string[] relativeCandidates =
        [
            Path.Combine("tools", "ltc_to_lta.exe"),
            Path.Combine("tools", "ltc_to_lta.cmd"),
            Path.Combine("tools", "ltc_to_lta.bat"),
            Path.Combine("tools", "LTC.exe"),
            Path.Combine("tools", "CFLTC_Converter.exe"),
            Path.Combine("tools", "guao_ltc.exe"),
            Path.Combine("tools", "WinLTC.exe"),
            "LTC.exe"
        ];

        foreach (string root in EnumerateToolSearchRoots())
        {
            foreach (string relativeCandidate in relativeCandidates)
            {
                string candidate = Path.Combine(root, relativeCandidate);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateToolSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in EnumerateRootAndParents(AppContext.BaseDirectory))
        {
            if (seen.Add(root))
            {
                yield return root;
            }
        }

        foreach (string root in EnumerateRootAndParents(Environment.CurrentDirectory))
        {
            if (seen.Add(root))
            {
                yield return root;
            }
        }
    }

    private static IEnumerable<string> EnumerateRootAndParents(string startPath)
    {
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(Path.GetFullPath(startPath));
        }
        catch
        {
            yield break;
        }

        for (int depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            yield return directory.FullName;
        }
    }

    private static void AddConverterArguments(ProcessStartInfo startInfo, string converterPath, string inputPath, string outputPath)
    {
        string fileName = Path.GetFileName(converterPath);
        if (string.Equals(fileName, "LTC.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "WinLTC.exe", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(inputPath);
            startInfo.ArgumentList.Add("-out");
            startInfo.ArgumentList.Add(outputPath);
            return;
        }

        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add(outputPath);
    }

    private static bool HasPrefix(byte[] data, byte[] prefix)
    {
        return data.Length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);
    }

    private static bool LooksLikeStructuredText(string text)
    {
        ReadOnlySpan<char> sample = text.AsSpan(0, Math.Min(text.Length, 4096));
        if (sample.IsEmpty)
        {
            return true;
        }

        int asciiText = 0;
        int structural = 0;
        foreach (char ch in sample)
        {
            if (ch is >= ' ' and <= '~' || ch is '\r' or '\n' or '\t')
            {
                asciiText++;
            }

            if (ch is '\r' or '\n' or '(' or ')' or '[' or ']' or '{' or '}' or '=' or ':' or '_' or '"' or '\'')
            {
                structural++;
            }
        }

        return asciiText >= sample.Length * 85 / 100 && structural > 0;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // External converters may already have exited after the timeout check.
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Temporary conversion files are best-effort cleanup only.
        }
    }
}
