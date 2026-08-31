using System.ComponentModel;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ModelContextProtocol.Server;

namespace ConstructionCrew.HomeOffice.Tools;

/// <summary>
/// Files a sitrep into the caller's own Notes/ folder, then acts on
/// <c>kind</c>: nothing (status), escalate to GC (milestone), or free the
/// Foreman's workorder slot and notify (pr-opened).
///
/// Takes <see cref="ISitrepWriter"/>, never the concrete SitrepWriter, which
/// lives in Config, which this project does not reference.
/// </summary>
[McpServerToolType]
public sealed class FileSitrepTool
{
    private const string ToolName = "file_sitrep";

    private static readonly string[] Altitudes = ["summary", "detail"];
    private static readonly string[] Kinds = ["status", "milestone", "pr-opened"];

    private readonly IForemanDirectory _foremen;
    private readonly HomeOfficeVaultOptions _vaultOptions;
    private readonly JobRegistry _jobs;
    private readonly ISitrepWriter _sitrepWriter;

    public FileSitrepTool(
        IForemanDirectory foremen,
        HomeOfficeVaultOptions vaultOptions,
        JobRegistry jobs,
        ISitrepWriter sitrepWriter)
    {
        _foremen = foremen;
        _vaultOptions = vaultOptions;
        _jobs = jobs;
        _sitrepWriter = sitrepWriter;
    }

    [McpServerTool(Name = "file_sitrep")]
    [Description("Write a sitrep into your own Notes/<Jobsite>/Sitreps/ folder. Pass YOUR OWN job id -- the one at the top of your task text. kind='status' just records it; kind='milestone' also escalates a one-line summary to the GC; kind='pr-opened' frees your workorder slot so you can take new work and notifies the Boss. altitude='summary' for the short version the Boss reads, 'detail' for the long one.")]
    public async Task<string> FileSitrep(
        [Description("Your own Foreman name, exactly as hired.")] string foreman,
        [Description("Your own job id, copied from the 'ConstructionCrew job id:' line at the top of your task text.")] string jobId,
        [Description("'summary' or 'detail'.")] string altitude,
        [Description("'status', 'milestone', or 'pr-opened'.")] string kind,
        [Description("The sitrep itself, in markdown. What happened, what passed, what is blocked.")] string body,
        CancellationToken cancellationToken)
    {
        var caller = _foremen.Find(foreman)
            ?? throw new InvalidOperationException(
                $"{ToolName}: no Foreman named '{foreman}' is hired. Known Foremen: " +
                $"{string.Join(", ", _foremen.All().Select(f => f.Name))}.");

        if (string.IsNullOrWhiteSpace(jobId) || _jobs.GetJob(jobId) is null)
        {
            throw new InvalidOperationException(
                $"{ToolName} needs your own job id, and '{jobId}' is not a tracked job. Copy the id from the " +
                "'ConstructionCrew job id:' line at the top of your task text.");
        }

        var normalizedAltitude = Normalize(altitude, Altitudes, nameof(altitude));
        var normalizedKind = Normalize(kind, Kinds, nameof(kind));

        var vaultRoot = _vaultOptions.VaultRoot;
        if (string.IsNullOrWhiteSpace(vaultRoot))
        {
            throw new InvalidOperationException(
                $"{ToolName} cannot write anything: no Vault is configured. Ask the Boss to configure a Vault root first.");
        }

        var path = _sitrepWriter.Write(new SitrepRequest(
            vaultRoot,
            caller.VaultFolders ?? [],
            normalizedAltitude,
            body,
            AuthoredBy(caller)));

        // Everything below happens only AFTER the Vault write succeeded.
        switch (normalizedKind)
        {
            case "milestone":
                // Same JobRegistry.AskGc ask_gc uses: one GC conversation, not a
                // separate one.
                var gcReply = await _jobs.AskGc(jobId, MilestoneSummary(caller.Name, jobId, body, path), cancellationToken);
                return $"sitrep filed: {path}{Environment.NewLine}GC: {gcReply}";

            case "pr-opened":
                // No AskGc: GC learns of the PR opportunistically. Releasing by
                // job id, not Foreman name, makes a stale release harmless.
                _jobs.ReleaseWorkorder(jobId);
                _jobs.NotifyPrOpened(jobId, caller.Name);
                return $"sitrep filed: {path}{Environment.NewLine}Workorder slot released -- you can take new work now.";

            default:
                return $"sitrep filed: {path}";
        }
    }

    /// <summary>One line, auto-composed: the GC gets told what happened, not handed the whole sitrep.</summary>
    private static string MilestoneSummary(string foremanName, string jobId, string body, string path)
    {
        var firstLine = body
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))
            ?.Trim() ?? "(no detail given)";

        if (firstLine.Length > 200)
        {
            firstLine = firstLine[..200] + "...";
        }

        return $"Milestone from {foremanName} (job {jobId}): {firstLine} -- full sitrep at {path}";
    }

    /// <summary>
    /// Mirrors InstructionsComposer.AuthoredBy (Architecture §3.1). Duplicated on
    /// purpose: that method lives in Config, which HomeOffice does not reference.
    /// </summary>
    private static string AuthoredBy(ForemanConfig caller) =>
        caller.Role == CrewRole.GC ? "GC" : $"Foreman:{caller.Name}:{caller.JobsiteName ?? "unassigned"}";

    private static string Normalize(string value, string[] allowed, string parameterName)
    {
        var match = allowed.FirstOrDefault(a => a.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));

        return match ?? throw new InvalidOperationException(
            $"{ToolName}: '{value}' is not a valid {parameterName}. Use one of: {string.Join(", ", allowed)}.");
    }
}
