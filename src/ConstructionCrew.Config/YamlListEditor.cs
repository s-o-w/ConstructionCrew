namespace ConstructionCrew.Config;

/// <summary>
/// Removes one "- name: X" entry from a hand-formatted YAML list file
/// (foremen.yaml / jobsites.yaml shape), preserving everything else,
/// including header comments, byte for byte. Counterpart to the writers'
/// plain-text append; neither round-trips through YamlDotNet, which would
/// drop comments.
/// </summary>
internal static class YamlListEditor
{
    public static bool RemoveEntry(string path, string topLevelKey, string entryName)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var lines = File.ReadAllLines(path).ToList();
        var keyLineIndex = lines.FindIndex(l => l.TrimEnd() == $"{topLevelKey}:");
        if (keyLineIndex < 0)
        {
            return false;
        }

        var preamble = lines.Take(keyLineIndex + 1).ToList();
        var body = lines.Skip(keyLineIndex + 1).ToList();

        var entries = new List<List<string>>();
        List<string>? current = null;
        foreach (var line in body)
        {
            if (line.StartsWith("  - name:", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    entries.Add(current);
                }

                current = [line];
            }
            else
            {
                current?.Add(line);
            }
        }

        if (current is not null)
        {
            entries.Add(current);
        }

        var removed = false;
        var kept = new List<List<string>>();
        foreach (var entry in entries)
        {
            if (!removed && NameMatches(entry[0], entryName))
            {
                removed = true;
                continue;
            }

            kept.Add(entry);
        }

        if (!removed)
        {
            return false;
        }

        var result = new List<string>(preamble);
        foreach (var entry in kept)
        {
            result.AddRange(entry);
        }

        File.WriteAllLines(path, result);
        return true;
    }

    private static bool NameMatches(string nameLine, string entryName)
    {
        var value = nameLine[(nameLine.IndexOf(':') + 1)..].Trim().Trim('\'', '"');
        return value.Equals(entryName, StringComparison.OrdinalIgnoreCase);
    }
}
