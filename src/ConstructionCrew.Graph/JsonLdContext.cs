using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConstructionCrew.Graph;

/// <summary>One term definition from a JSON-LD @context: its @id, and its @type coercion when it has one.</summary>
internal sealed record TermDefinition(string Id, string? Coercion);

/// <summary>
/// A composed JSON-LD @context: prefix map, short-name term definitions, and any
/// keyword aliases ("type": "@type", "id": "@id", JSON-LD 1.1 keyword aliasing).
///
/// A direct port of vault_to_rdf.py's Context/merge_context/context_base,
/// including the composition rule that matters most: a context a document
/// *references* contributes vocabulary but never a base, so each ontology
/// folder keeps its own scoped @base.
/// </summary>
internal sealed class JsonLdContext
{
    /// <summary>Untrusted content: cap context documents before the bytes are in memory.</summary>
    private const long MaxContextBytes = 4L << 20;

    public string Base { get; }

    public Dictionary<string, string> Prefixes { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, TermDefinition> Terms { get; } = new(StringComparer.Ordinal);

    /// <summary>alias name -> keyword, e.g. "type" -> "@type".</summary>
    public Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);

    private JsonLdContext(Dictionary<string, ContextValue> merged)
    {
        Base = merged.TryGetValue("@base", out var b) && b is ContextString bs ? bs.Value : string.Empty;

        foreach (var (key, val) in merged)
        {
            if (key.StartsWith('@'))
            {
                continue;
            }

            var target = val switch
            {
                ContextString s => s.Value,
                ContextMap m => m.Values.GetValueOrDefault("@id"),
                _ => null,
            };

            if (target is "@type" or "@id")
            {
                Aliases[key] = target;
                continue;
            }

            switch (val)
            {
                case ContextString s when s.Value.StartsWith("http", StringComparison.Ordinal)
                                          && (s.Value.EndsWith('/') || s.Value.EndsWith('#')):
                    Prefixes[key] = s.Value;
                    break;
                case ContextString s:
                    Terms[key] = new TermDefinition(s.Value, null);
                    break;
                case ContextMap m when m.Values.GetValueOrDefault("@id") is { } id:
                    Terms[key] = new TermDefinition(id, m.Values.GetValueOrDefault("@type"));
                    break;
            }
        }
    }

    /// <summary>Expand 'prefix:local' to a full IRI; pass full IRIs through; otherwise fall back to the base.</summary>
    public string ExpandCurie(string token)
    {
        token = token.Trim();

        if (token.StartsWith("http://", StringComparison.Ordinal) || token.StartsWith("https://", StringComparison.Ordinal))
        {
            return token;
        }

        var colon = token.IndexOf(':');
        if (colon >= 0)
        {
            var prefix = token[..colon];
            if (Prefixes.TryGetValue(prefix, out var ns))
            {
                return ns + token[(colon + 1)..];
            }
        }

        return Base + token;
    }

    /// <summary>
    /// Load a root context document, composing every context it references. The
    /// root context is load-bearing (it decides where subjects mint), so an
    /// unusable one is a hard error, not a silent fallback.
    /// </summary>
    public static JsonLdContext Load(string path)
    {
        var doc = ReadJsonObject(path)
                  ?? throw new InvalidOperationException($"Vault graph context is unreadable or not a JSON object: {path}");

        var full = Path.GetFullPath(path);
        var merged = Merge(doc["@context"], Path.GetDirectoryName(full)!, new HashSet<string>(StringComparer.Ordinal) { full }, Path.GetDirectoryName(full)!);
        return new JsonLdContext(merged);
    }

    /// <summary>
    /// The @base an ontology/vocabulary context declares, or null. Read in
    /// isolation (never merged into the root) so each ontology keeps its own
    /// scoped base, the namespace its members mint under.
    /// </summary>
    public static string? ScopedBase(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var doc = ReadJsonObject(path);
        if (doc is null)
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        var merged = Merge(doc["@context"], Path.GetDirectoryName(full)!, new HashSet<string>(StringComparer.Ordinal) { full }, Path.GetDirectoryName(full)!);
        return merged.TryGetValue("@base", out var b) && b is ContextString s ? s.Value : null;
    }

    private static Dictionary<string, ContextValue> Merge(JsonNode? node, string baseDir, HashSet<string> seen, string root)
    {
        var merged = new Dictionary<string, ContextValue>(StringComparer.Ordinal);

        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    merged[key] = ToContextValue(value);
                }

                break;

            case JsonArray array:
                foreach (var entry in array)
                {
                    foreach (var (key, value) in Merge(entry, baseDir, seen, root))
                    {
                        merged[key] = value;
                    }
                }

                break;

            case JsonValue value when value.TryGetValue<string>(out var reference):
                foreach (var (key, sub) in MergeReference(reference, baseDir, seen, root))
                {
                    merged[key] = sub;
                }

                break;
        }

        return merged;
    }

    private static Dictionary<string, ContextValue> MergeReference(string reference, string baseDir, HashSet<string> seen, string root)
    {
        var empty = new Dictionary<string, ContextValue>(StringComparer.Ordinal);

        // Remote contexts are never fetched; a reference that is absolute, has
        // backslashes, or climbs out of its own tree is refused outright.
        if (reference.StartsWith("http://", StringComparison.Ordinal) ||
            reference.StartsWith("https://", StringComparison.Ordinal) ||
            !IsSafeRelativeReference(reference))
        {
            return empty;
        }

        var resolved = Path.GetFullPath(Path.Combine(baseDir, reference));
        if (!IsWithin(resolved, root) || seen.Contains(resolved) || !File.Exists(resolved))
        {
            return empty;
        }

        seen.Add(resolved);

        var doc = ReadJsonObject(resolved);
        if (doc is null)
        {
            return empty;
        }

        var sub = Merge(doc["@context"], Path.GetDirectoryName(resolved)!, seen, root);

        // A referenced context contributes vocabulary, not a new document base:
        // its @base scopes only its own ontology's subjects (read separately by
        // ScopedBase), so it must not override the root's @base here.
        sub.Remove("@base");
        return sub;
    }

    private static ContextValue ToContextValue(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                return new ContextString(s);
            case JsonObject o:
            {
                var map = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var (key, value) in o)
                {
                    if (value is JsonValue jv && jv.TryGetValue<string>(out var sv))
                    {
                        map[key] = sv;
                    }
                }

                return new ContextMap(map);
            }

            default:
                return ContextOther.Instance;
        }
    }

    private static bool IsSafeRelativeReference(string reference)
    {
        if (reference.Contains('\\') || reference.Contains('\0') || Path.IsPathRooted(reference))
        {
            return false;
        }

        return !reference.Split('/', StringSplitOptions.None).Contains("..");
    }

    private static bool IsWithin(string candidate, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || candidate.Equals(normalizedRoot, StringComparison.Ordinal);
    }

    private static JsonObject? ReadJsonObject(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxContextBytes)
            {
                return null;
            }

            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private abstract record ContextValue;

    private sealed record ContextString(string Value) : ContextValue;

    private sealed record ContextMap(Dictionary<string, string?> Values) : ContextValue;

    private sealed record ContextOther : ContextValue
    {
        public static readonly ContextOther Instance = new();
    }
}
