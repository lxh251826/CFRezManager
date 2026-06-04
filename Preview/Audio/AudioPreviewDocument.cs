using System.IO;

namespace CFRezManager;

public sealed record AudioPreviewDocument(
    string AudioName,
    string AudioPath,
    string? AudioInfo,
    IReadOnlyList<string> TemporaryPaths,
    string FormatLabel = "AUDIO");

internal static class AudioPreviewDocumentFactory
{
    public const int MaxAudioPreviewBytes = 512 * 1024 * 1024;

    public static bool TryCreate(
        string fileName,
        string? sourcePath,
        byte[] data,
        bool canUseSourcePath,
        out AudioPreviewDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        byte[]? audioData = LzmaAloneDecoder.TryPrepareData(data, MaxAudioPreviewBytes);
        if (audioData is null)
        {
            errorMessage = "Audio compression could not be decoded.";
            return false;
        }

        if (!AudioMetadataDecoder.TryRead(audioData, fileName, out AudioMetadata metadata))
        {
            errorMessage = "Audio data is not a supported WAVE, MP3, or Ogg stream.";
            return false;
        }

        var temporaryPaths = new List<string>();
        string audioPath;
        if (AudioMetadataDecoder.IsOggData(audioData))
        {
            string wavePath = CreateTemporaryAudioPath("wav");
            if (!OggVorbisWaveDecoder.TryDecodeToWave(audioData, wavePath, out errorMessage))
            {
                TryDeleteFile(wavePath);
                return false;
            }

            audioPath = wavePath;
            temporaryPaths.Add(wavePath);
        }
        else if (canUseSourcePath && !string.IsNullOrWhiteSpace(sourcePath) && ReferenceEquals(audioData, data))
        {
            audioPath = sourcePath;
        }
        else
        {
            audioPath = CreateTemporaryAudioPath(metadata.PreferredExtension);
            File.WriteAllBytes(audioPath, audioData);
            temporaryPaths.Add(audioPath);
        }

        bool compressed = LzmaAloneDecoder.IsCompressed(data);
        string storage = compressed
            ? $"LZMA-compressed {metadata.Container}, {data.Length:N0} bytes -> {audioData.Length:N0} bytes"
            : $"{metadata.Container}, {data.Length:N0} bytes";
        string info = $"{storage} - {metadata.Description}";
        document = new AudioPreviewDocument(fileName, audioPath, info, temporaryPaths, metadata.Container.ToUpperInvariant());
        return true;
    }

    public static bool IsWaveData(byte[] data)
    {
        return AudioMetadataDecoder.IsWaveData(data);
    }

    internal static string CreateTemporaryAudioPath(string extension)
    {
        string previewDirectory = Path.Combine(Path.GetTempPath(), "CFRezManager", "AudioPreview");
        Directory.CreateDirectory(previewDirectory);
        return Path.Combine(previewDirectory, $"{Guid.NewGuid():N}.{extension.TrimStart('.')}");
    }

    internal static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
