using System.Globalization;
using System.Text.RegularExpressions;

namespace CFRezManager;

internal sealed record CfgTextDocument(
    string Text,
    string Description,
    int SourceByteCount,
    int DecodedByteCount,
    int Score);

internal static class CfgTextDecoder
{
    private const int MaxRezPhaseBytes = 8 * 1024;
    private const int MinStructuredScore = 12;
    private static readonly Regex SectionHeaderRegex = new(@"(?m)^\s*\[[^\]\r\n]{1,80}\]\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AssignmentRegex = new(@"(?m)^\s*[^=\r\n]{1,120}=\s*.+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TextureReferenceRegex = new(@"(?i)\.(png|dds|dtx|tga|jpg|jpeg|bmp|bin)\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryDecode(
        byte[] data,
        string fileName,
        int maxDecodedBytes,
        out CfgTextDocument? document)
    {
        document = null;
        if (data.Length == 0)
        {
            return false;
        }

        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data, maxDecodedBytes);
        bool lzmaExpanded = prepared is not null && !ReferenceEquals(prepared, data);
        byte[] preparedBytes = prepared ?? data;

        if (TryDecodeStructuredText(
                preparedBytes,
                lzmaExpanded ? "lzma-text" : "plain-text",
                data.Length,
                preparedBytes.Length,
                out document))
        {
            return true;
        }

        if (TryDecodeEncText(preparedBytes, data.Length, out document) ||
            lzmaExpanded && TryDecodeEncText(data, data.Length, out document))
        {
            return true;
        }

        if (!CfgBinaryStripDecoder.TryDetect(preparedBytes, out _) &&
            preparedBytes.Length <= MaxRezPhaseBytes &&
            TryDecodeRezPhase(preparedBytes, data.Length, out document))
        {
            return true;
        }

        return false;
    }

    public static int ScoreStructuredText(string text)
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

        if (SectionHeaderRegex.IsMatch(text))
        {
            score += 24;
        }

        int assignmentCount = AssignmentRegex.Matches(text).Count;
        score += Math.Min(40, assignmentCount * 4);

        int textureReferenceCount = TextureReferenceRegex.Matches(text).Count;
        score += Math.Min(30, textureReferenceCount * 3);

        int lineCount = text.Count(ch => ch == '\n') + 1;
        if (lineCount >= 4)
        {
            score += 8;
        }

        int suspicious = text.Count(ch => ch == '\uFFFD' || char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'));
        score -= suspicious * 10;

        return score;
    }

    private static bool TryDecodeStructuredText(
        byte[] data,
        string method,
        int sourceByteCount,
        int decodedByteCount,
        out CfgTextDocument? document)
    {
        document = null;
        if (!TextPreviewDecoder.TryDecode(data, preferKorean: false, out string text, out string encodingName))
        {
            return false;
        }

        int score = ScoreStructuredText(text);
        if (score < MinStructuredScore)
        {
            return false;
        }

        document = new CfgTextDocument(
            text,
            string.Create(CultureInfo.InvariantCulture, $"{method} / {encodingName} / score={score}"),
            sourceByteCount,
            decodedByteCount,
            score);
        return true;
    }

    private static bool TryDecodeEncText(byte[] data, int sourceByteCount, out CfgTextDocument? document)
    {
        document = null;
        if (!EncTextDecoder.TryDecode(data, out byte[] decoded))
        {
            return false;
        }

        return TryDecodeStructuredText(decoded, "enc-base64", sourceByteCount, decoded.Length, out document);
    }

    private static bool TryDecodeRezPhase(byte[] data, int sourceByteCount, out CfgTextDocument? document)
    {
        CfgTextDocument? best = null;
        for (int phase = 0; phase < RezCrypto.KeyLength; phase++)
        {
            if (TryDecodeRezPhaseVariant(data, phase, decode: true, sourceByteCount, out CfgTextDocument? decoded) &&
                (best is null || decoded!.Score > best.Score))
            {
                best = decoded;
            }

            if (TryDecodeRezPhaseVariant(data, phase, decode: false, sourceByteCount, out CfgTextDocument? encoded) &&
                (best is null || encoded!.Score > best.Score))
            {
                best = encoded;
            }

            if (best is not null && best.Score >= 100)
            {
                break;
            }
        }

        document = best;
        return document is not null;
    }

    private static bool TryDecodeRezPhaseVariant(
        byte[] data,
        int phase,
        bool decode,
        int sourceByteCount,
        out CfgTextDocument? document)
    {
        document = null;
        byte[] buffer = data.ToArray();
        if (decode)
        {
            RezCrypto.Decode(buffer, phase);
        }
        else
        {
            RezCrypto.Encode(buffer, phase);
        }

        return TryDecodeStructuredText(
            buffer,
            decode ? $"rez-decode-phase-{phase}" : $"rez-encode-phase-{phase}",
            sourceByteCount,
            buffer.Length,
            out document);
    }
}
