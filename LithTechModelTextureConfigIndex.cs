using System.IO;
using System.Text;

namespace CFRezManager;

internal static class LithTechModelTextureConfigIndex
{
    private const int MaxConfigBytes = 1024 * 1024;

    private static readonly string[] TextureExtensions =
    [
        ".dtx",
        ".dds",
        ".tga",
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".bin"
    ];

    public static Func<IEnumerable<string>, IReadOnlyList<string>>? CreateResolver(ExplorerItem root)
    {
        TextureConfigIndex index = BuildIndex(root);
        if (index.IsEmpty)
        {
            return null;
        }

        var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var textureCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        return names =>
        {
            string cacheKey = string.Join("|", names.Where(name => !string.IsNullOrWhiteSpace(name)));
            if (cache.TryGetValue(cacheKey, out IReadOnlyList<string>? cached))
            {
                return cached;
            }

            IReadOnlyList<string> result = Resolve(index, names, textureCache);
            cache[cacheKey] = result;
            return result;
        };
    }

    private static TextureConfigIndex BuildIndex(ExplorerItem root)
    {
        var byName = new Dictionary<string, List<TextureConfigItem>>(StringComparer.OrdinalIgnoreCase);
        var byLooseName = new Dictionary<string, List<TextureConfigItem>?>(StringComparer.OrdinalIgnoreCase);

        foreach (ExplorerItem item in EnumerateFiles(root))
        {
            if (!string.Equals(item.FileExtension, "cfg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string configName = Path.GetFileNameWithoutExtension(item.Name);
            if (string.IsNullOrWhiteSpace(configName))
            {
                continue;
            }

            var configItem = new TextureConfigItem(item.OutputRelativePath, configName, item);
            AddIndexCandidate(byName, NormalizeKey(configName), configItem);
            AddIndexCandidate(byName, NormalizeKey(LithTechModelPartGrouper.GetNumberedPartBase(configName)), configItem);

            string looseKey = NormalizeLooseKey(configName);
            if (!string.IsNullOrWhiteSpace(looseKey))
            {
                AddLooseIndexCandidate(byLooseName, looseKey, configItem);
            }
        }

        return new TextureConfigIndex(byName, byLooseName);
    }

    private static IReadOnlyList<string> Resolve(
        TextureConfigIndex index,
        IEnumerable<string> names,
        Dictionary<string, IReadOnlyList<string>> textureCache)
    {
        var textures = new List<string>();
        var seenConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            foreach (TextureConfigItem configItem in EnumerateConfigItems(index, name))
            {
                if (!seenConfigs.Add(configItem.Path))
                {
                    continue;
                }

                IReadOnlyList<string> configTextures = GetConfigTextures(configItem, textureCache);
                foreach (string texture in configTextures)
                {
                    if (seenTextures.Add(texture))
                    {
                        textures.Add(texture);
                    }
                }
            }
        }

        return textures;
    }

    private static IReadOnlyList<string> GetConfigTextures(
        TextureConfigItem configItem,
        Dictionary<string, IReadOnlyList<string>> textureCache)
    {
        if (textureCache.TryGetValue(configItem.Path, out IReadOnlyList<string>? cached))
        {
            return cached;
        }

        string? text = TryReadConfigText(configItem.Item);
        IReadOnlyList<string> textures = string.IsNullOrWhiteSpace(text)
            ? []
            : ExtractTextureReferences(text, configItem.Name);
        textureCache[configItem.Path] = textures;
        return textures;
    }

    private static IEnumerable<TextureConfigItem> EnumerateConfigItems(TextureConfigIndex index, string name)
    {
        string normalized = NormalizeKey(Path.GetFileNameWithoutExtension(name));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        foreach (TextureConfigItem candidate in LookupExact(index, normalized))
        {
            yield return candidate;
        }

        string numberedBase = LithTechModelPartGrouper.GetNumberedPartBase(normalized);
        if (!string.Equals(numberedBase, normalized, StringComparison.OrdinalIgnoreCase))
        {
            foreach (TextureConfigItem candidate in LookupExact(index, numberedBase))
            {
                yield return candidate;
            }
        }

        string looseKey = NormalizeLooseKey(normalized);
        if (!string.IsNullOrWhiteSpace(looseKey) &&
            index.ByLooseName.TryGetValue(looseKey, out List<TextureConfigItem>? looseCandidates) &&
            looseCandidates is not null)
        {
            foreach (TextureConfigItem candidate in looseCandidates)
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<TextureConfigItem> LookupExact(TextureConfigIndex index, string name)
    {
        return index.ByName.TryGetValue(name, out List<TextureConfigItem>? candidates)
            ? candidates
            : [];
    }

    private static void AddIndexCandidate(
        Dictionary<string, List<TextureConfigItem>> index,
        string key,
        TextureConfigItem candidate)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!index.TryGetValue(key, out List<TextureConfigItem>? candidates))
        {
            candidates = [];
            index[key] = candidates;
        }

        if (!candidates.Any(existing => string.Equals(existing.Path, candidate.Path, StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(candidate);
        }
    }

    private static void AddLooseIndexCandidate(
        Dictionary<string, List<TextureConfigItem>?> index,
        string key,
        TextureConfigItem candidate)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!index.TryGetValue(key, out List<TextureConfigItem>? candidates))
        {
            index[key] = [candidate];
            return;
        }

        if (candidates is null)
        {
            return;
        }

        if (candidates.Any(existing => string.Equals(existing.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(candidate);
            return;
        }

        index[key] = null;
    }

    private static List<string> ExtractTextureReferences(string text, string configName)
    {
        var candidates = new List<ScoredTextureReference>();
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            int equals = line.IndexOf('=');
            string key = equals > 0 ? line[..equals].Trim() : string.Empty;
            string value = equals >= 0 ? line[(equals + 1)..] : line;
            foreach (string texture in ExtractTextureReferencesFromValue(value))
            {
                candidates.Add(new ScoredTextureReference(texture, ScoreTextureReference(key, texture, configName)));
            }
        }

        if (candidates.Count == 0)
        {
            foreach (string texture in ExtractTextureReferencesFromValue(text))
            {
                candidates.Add(new ScoredTextureReference(texture, ScoreTextureReference(string.Empty, texture, configName)));
            }
        }

        return candidates
            .GroupBy(candidate => candidate.Texture, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Texture, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Texture)
            .ToList();
    }

    private static int ScoreTextureReference(string key, string texture, string configName)
    {
        string lowerKey = key.ToLowerInvariant();
        string textureStem = Path.GetFileNameWithoutExtension(texture).ToLowerInvariant();
        string configStem = configName.ToLowerInvariant();
        int score = 50;

        if (lowerKey.Contains("diffuse") ||
            lowerKey.Contains("albedo") ||
            lowerKey.Contains("base") ||
            lowerKey.Contains("color") ||
            lowerKey.Contains("main") ||
            lowerKey == "texturename" ||
            lowerKey == "texture")
        {
            score += 100;
        }

        if (textureStem.Contains(configStem, StringComparison.OrdinalIgnoreCase) ||
            configStem.Contains(textureStem, StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }

        if (LooksLikeAuxiliaryTexture(lowerKey, textureStem))
        {
            score -= 80;
        }

        if (textureStem.Contains("lobbycube") ||
            textureStem.Contains("gold_map") ||
            textureStem.Contains("black_shader"))
        {
            score -= 120;
        }

        return score;
    }

    private static bool LooksLikeAuxiliaryTexture(string lowerKey, string textureStem)
    {
        return lowerKey.Contains("normal") ||
               lowerKey.Contains("specular") ||
               lowerKey.Contains("env") ||
               lowerKey.Contains("cube") ||
               lowerKey.Contains("alpha") ||
               lowerKey.Contains("mask") ||
               lowerKey.Contains("bump") ||
               lowerKey.Contains("glow") ||
               textureStem.EndsWith("_n", StringComparison.OrdinalIgnoreCase) ||
               textureStem.EndsWith("_s", StringComparison.OrdinalIgnoreCase) ||
               textureStem.EndsWith("_sp", StringComparison.OrdinalIgnoreCase) ||
               textureStem.EndsWith("_alpha", StringComparison.OrdinalIgnoreCase) ||
               textureStem.EndsWith("_mask", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractTextureReferencesFromValue(string value)
    {
        foreach (string extension in TextureExtensions)
        {
            int searchStart = 0;
            while (searchStart < value.Length)
            {
                int extensionIndex = value.IndexOf(extension, searchStart, StringComparison.OrdinalIgnoreCase);
                if (extensionIndex < 0)
                {
                    break;
                }

                int start = extensionIndex;
                while (start > 0 && IsTexturePathCharacter(value[start - 1]))
                {
                    start--;
                }

                int end = extensionIndex + extension.Length;
                string reference = NormalizeTextureReference(value[start..end]);
                if (!string.IsNullOrWhiteSpace(reference) && reference.Length > extension.Length)
                {
                    yield return reference;
                }

                searchStart = end;
            }
        }
    }

    private static bool IsTexturePathCharacter(char value)
    {
        return char.IsLetterOrDigit(value) ||
               value is '_' or '-' or '.' or '/' or '\\' or ' ';
    }

    private static string NormalizeTextureReference(string value)
    {
        return value
            .Trim()
            .Trim('"', '\'', '(', ')', '[', ']', '{', '}', '<', '>', ',', ';', ':')
            .Replace('\\', '/')
            .TrimStart('/');
    }

    private static string? TryReadConfigText(ExplorerItem item)
    {
        byte[]? data = TryReadItemBytes(item);
        if (data is null || data.Length == 0)
        {
            return null;
        }

        byte[] prepared = LzmaAloneDecoder.TryPrepareData(data, MaxConfigBytes) ?? data;
        return TextPreviewDecoder.TryDecode(prepared, preferKorean: false, out string text, out _)
            ? text
            : Encoding.ASCII.GetString(prepared);
    }

    private static byte[]? TryReadItemBytes(ExplorerItem item)
    {
        try
        {
            if (item.Kind == ExplorerItemKind.LocalFile)
            {
                var info = new FileInfo(item.SourcePath);
                if (!info.Exists || info.Length <= 0 || info.Length > MaxConfigBytes || info.Length > int.MaxValue)
                {
                    return null;
                }

                return File.ReadAllBytes(item.SourcePath);
            }

            if (item.Kind == ExplorerItemKind.RezFile &&
                item.Archive is not null &&
                item.ArchiveFile is not null &&
                item.ArchiveFile.Size > 0 &&
                item.ArchiveFile.Size <= MaxConfigBytes)
            {
                byte[] data = new byte[item.ArchiveFile.Size];
                using FileStream source = File.OpenRead(item.Archive.FilePath);
                source.Position = item.ArchiveFile.DataOffset;
                source.ReadExactly(data);
                return data;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static IEnumerable<ExplorerItem> EnumerateFiles(ExplorerItem item)
    {
        if (item.IsFile)
        {
            yield return item;
        }

        foreach (ExplorerItem child in item.Children)
        {
            foreach (ExplorerItem file in EnumerateFiles(child))
            {
                yield return file;
            }
        }
    }

    private static string NormalizeKey(string value)
    {
        return value
            .Trim()
            .Trim('"', '\'')
            .Replace('\\', '/')
            .TrimStart('/');
    }

    private static string NormalizeLooseKey(string value)
    {
        var builder = new StringBuilder();
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private sealed record TextureConfigIndex(
        Dictionary<string, List<TextureConfigItem>> ByName,
        Dictionary<string, List<TextureConfigItem>?> ByLooseName)
    {
        public bool IsEmpty => ByName.Count == 0 && ByLooseName.Count == 0;
    }

    private sealed record TextureConfigItem(string Path, string Name, ExplorerItem Item);

    private readonly record struct ScoredTextureReference(string Texture, int Score);
}
