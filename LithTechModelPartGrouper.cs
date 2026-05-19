using System.IO;

namespace CFRezManager;

internal static class LithTechModelPartGrouper
{
    public static List<ExplorerItem> ExpandNumberedSiblingParts(IEnumerable<ExplorerItem> items)
    {
        var expanded = new List<ExplorerItem>();
        var seen = new HashSet<ExplorerItem>();
        foreach (ExplorerItem item in items)
        {
            foreach (ExplorerItem part in ExpandNumberedSiblingParts(item))
            {
                if (seen.Add(part))
                {
                    expanded.Add(part);
                }
            }
        }

        return expanded;
    }

    public static List<ExplorerItem> ExpandNumberedSiblingParts(ExplorerItem item)
    {
        if (!IsModelFile(item) || item.Parent is null)
        {
            return [item];
        }

        string baseStem = GetNumberedPartBase(Path.GetFileNameWithoutExtension(item.Name));
        List<ExplorerItem> siblings = item.Parent.Children
            .Where(IsModelFile)
            .Where(candidate => string.Equals(
                GetNumberedPartBase(Path.GetFileNameWithoutExtension(candidate.Name)),
                baseStem,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => GetNumberedPartOrder(Path.GetFileNameWithoutExtension(candidate.Name), baseStem))
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return siblings.Count > 1 ? siblings : [item];
    }

    public static string GetNumberedPartBase(string stem)
    {
        return TryStripNumericSuffix(stem, out string? baseStem)
            ? baseStem!
            : stem;
    }

    public static bool TryStripNumericSuffix(string stem, out string? baseStem)
    {
        baseStem = null;
        int separatorIndex = stem.LastIndexOf('_');
        if (separatorIndex <= 0 || separatorIndex + 1 >= stem.Length)
        {
            return false;
        }

        ReadOnlySpan<char> suffix = stem.AsSpan(separatorIndex + 1);
        if (!suffix.ToString().All(char.IsDigit))
        {
            return false;
        }

        baseStem = stem[..separatorIndex];
        return !string.IsNullOrWhiteSpace(baseStem);
    }

    private static int GetNumberedPartOrder(string stem, string baseStem)
    {
        if (string.Equals(stem, baseStem, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return TryStripNumericSuffix(stem, out string? stripped) &&
               string.Equals(stripped, baseStem, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(stem[(stem.LastIndexOf('_') + 1)..], out int order)
            ? order
            : int.MaxValue;
    }

    private static bool IsModelFile(ExplorerItem item)
    {
        return item.IsFile &&
               (LithTechModelDecoder.IsCandidate(item.FileExtension) ||
                LithTechWorldDatDecoder.IsCandidate(item.FileExtension));
    }
}
