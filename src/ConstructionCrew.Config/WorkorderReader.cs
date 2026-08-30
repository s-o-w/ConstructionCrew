using ConstructionCrew.Core;
using ConstructionCrew.Core.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace ConstructionCrew.Config;

/// <summary>
/// Reads GC's WORKORDER.md. Deliberately validates the file against ITSELF only
/// -- path segments vs. frontmatter -- because that is the whole of what this
/// file can be checked against without knowing who the work is being dispatched
/// to. DispatchTaskTool owns the second half of the validation (workorder vs.
/// dispatch target) and the SourceBranch fallback chain.
/// </summary>
public sealed class WorkorderReader : IWorkorderReader
{
    private const int MaxFrontmatterBytes = 1 << 20;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    public ParsedWorkorder Read(string path, string vaultRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("A workorder path is required.");
        }

        if (string.IsNullOrWhiteSpace(vaultRoot))
        {
            throw new InvalidOperationException("A Vault root is required to read a workorder.");
        }

        var fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"No workorder file at '{fullPath}'. Write it before dispatching.");
        }

        var (pathJobsite, pathFeature) = ResolvePlansSegments(fullPath, vaultRoot);

        var frontmatter = ReadFrontmatter(fullPath)
            ?? throw new InvalidOperationException(
                $"Workorder '{fullPath}' has no readable YAML frontmatter block. It must open with '---' and carry 'feature' and 'jobsite'.");

        var feature = Scalar(frontmatter, "feature")
            ?? throw new InvalidOperationException($"Workorder '{fullPath}' frontmatter is missing 'feature'.");
        var jobsite = Scalar(frontmatter, "jobsite")
            ?? throw new InvalidOperationException($"Workorder '{fullPath}' frontmatter is missing 'jobsite'.");
        var sourceBranch = Scalar(frontmatter, "sourceBranch");

        if (!string.Equals(jobsite, pathJobsite, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Workorder '{fullPath}' frontmatter says jobsite '{jobsite}', but it sits under Plans/{pathJobsite}/. " +
                "Move the file, or fix the frontmatter -- they have to agree.");
        }

        if (!string.Equals(feature, pathFeature, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Workorder '{fullPath}' frontmatter says feature '{feature}', but it sits under Plans/{pathJobsite}/{pathFeature}/. " +
                "Move the file, or fix the frontmatter -- they have to agree.");
        }

        return new ParsedWorkorder(feature, jobsite, sourceBranch);
    }

    /// <summary>
    /// The &lt;jobsite&gt;/&lt;feature&gt; pair the file's own location asserts.
    /// Anything that is not exactly two segments under &lt;vaultRoot&gt;/Plans/
    /// is a hard rejection -- a workorder outside the Plans tree has no jobsite
    /// or feature to be checked against.
    /// </summary>
    private static (string Jobsite, string Feature) ResolvePlansSegments(string fullPath, string vaultRoot)
    {
        var plansRoot = Path.GetFullPath(Path.Combine(vaultRoot, "Plans"));
        var directory = Path.GetDirectoryName(fullPath);

        if (string.IsNullOrEmpty(directory) ||
            !directory.StartsWith(plansRoot + Path.DirectorySeparatorChar, PathComparison.ForPathPrefix))
        {
            throw new InvalidOperationException(
                $"Workorder '{fullPath}' is not inside '{plansRoot}'. A workorder must live at " +
                "<vaultRoot>/Plans/<Jobsite>/<Feature>/WORKORDER.md.");
        }

        var segments = directory[(plansRoot.Length + 1)..]
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length != 2)
        {
            throw new InvalidOperationException(
                $"Workorder '{fullPath}' is {segments.Length} folder(s) under '{plansRoot}', expected exactly two " +
                "(<Jobsite>/<Feature>).");
        }

        return (segments[0], segments[1]);
    }

    /// <summary>
    /// The leading YAML frontmatter block, or null when there is none. Same
    /// shape and size cap as the graph exporter's reader; duplicated rather than
    /// shared because that one is internal to ConstructionCrew.Graph, which
    /// Config does not reference.
    /// </summary>
    private static Dictionary<string, object?>? ReadFrontmatter(string path)
    {
        var text = File.ReadAllText(path);

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
        catch (YamlException ex)
        {
            throw new InvalidOperationException($"Workorder '{path}' has malformed YAML frontmatter: {ex.Message}");
        }

        if (parsed is not System.Collections.IDictionary map)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            if (entry.Key is string name)
            {
                result[name] = entry.Value;
            }
        }

        return result;
    }

    /// <summary>A frontmatter scalar, trimmed, with blank treated as absent.</summary>
    private static string? Scalar(Dictionary<string, object?> frontmatter, string key) =>
        frontmatter.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
