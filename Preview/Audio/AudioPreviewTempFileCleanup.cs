using System.IO;

namespace CFRezManager;

internal static class AudioPreviewTempFileCleanup
{
    private static readonly string PreviewDirectory =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CFRezManager", "AudioPreview"));

    public static void Queue(IEnumerable<string> paths)
    {
        string[] safePaths = paths
            .Where(IsSafePreviewPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (safePaths.Length == 0)
        {
            return;
        }

        _ = Task.Run(() => DeleteWithRetriesAsync(safePaths));
    }

    private static async Task DeleteWithRetriesAsync(IReadOnlyList<string> paths)
    {
        var remaining = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        for (int attempt = 0; attempt < 12 && remaining.Count > 0; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(Math.Min(250 * attempt, 2_000));
            }

            foreach (string path in remaining.ToArray())
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        remaining.Remove(path);
                        continue;
                    }

                    File.Delete(path);
                    remaining.Remove(path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static bool IsSafePreviewPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(PreviewDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
