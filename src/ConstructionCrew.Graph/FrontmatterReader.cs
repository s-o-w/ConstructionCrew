using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace ConstructionCrew.Graph;

/// <summary>
/// Reads a note's YAML frontmatter block and nothing else -- the Markdown body
/// is never read. Port of vault_to_rdf.py's parse_frontmatter, including its
/// size cap and its "malformed frontmatter skips the note, it never aborts the
/// sweep" behaviour.
/// </summary>
internal static class FrontmatterReader
{
    private const int MaxFrontmatterBytes = 1 << 20;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    /// <summary>The frontmatter as a mapping, or null when there is none / it is unusable.</summary>
    public static Dictionary<string, object?>? Read(string path)
    {
        string text;
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[MaxFrontmatterBytes + 4096];
            var read = reader.Read(buffer, 0, buffer.Length);
            text = new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = text.Split("---", 3, StringSplitOptions.None);
        if (parts.Length < 3 || System.Text.Encoding.UTF8.GetByteCount(parts[1]) > MaxFrontmatterBytes)
        {
            return null;
        }

        object? parsed;
        try
        {
            parsed = Deserializer.Deserialize<object>(parts[1]);
        }
        catch (YamlException)
        {
            return null;
        }

        if (parsed is not System.Collections.IDictionary map)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            if (entry.Key is string name)
            {
                result[name] = entry.Value;
            }
        }

        return result;
    }
}
