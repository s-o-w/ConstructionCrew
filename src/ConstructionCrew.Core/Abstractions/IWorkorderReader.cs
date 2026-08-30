namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Reads a WORKORDER.md GC wrote into the Vault. The interface lives in Core so
/// HomeOffice can depend on it without referencing Config (where the YamlDotNet
/// implementation lives) -- see Architecture §3.7.
/// </summary>
public interface IWorkorderReader
{
    /// <summary>
    /// Parses the workorder at <paramref name="path"/> and checks the file
    /// against ITSELF: the path must resolve inside
    /// <c>&lt;vaultRoot&gt;/Plans/&lt;jobsite&gt;/&lt;feature&gt;/</c>, and the
    /// frontmatter's <c>jobsite</c>/<c>feature</c> must agree with those path
    /// segments. Checking the workorder against the dispatch target is the
    /// caller's job, not this one's; so is resolving SourceBranch's fallback
    /// chain.
    /// </summary>
    ParsedWorkorder Read(string path, string vaultRoot);
}

/// <summary>What the WORKORDER.md file itself says, after self-consistency validation.</summary>
public sealed record ParsedWorkorder(string Feature, string Jobsite, string? SourceBranch);

/// <summary>
/// The fully resolved workorder a Foreman holds a slot for. Built by
/// DispatchTaskTool (which knows the Vault root and the Jobsite registry), never
/// by the reader and never by JobRegistry.
/// </summary>
public sealed record ActiveWorkorder(
    string Feature,
    string Jobsite,
    string PlansFolder,
    string SourceBranch,
    string FeatureBranch,
    DateTimeOffset OpenedAt);
