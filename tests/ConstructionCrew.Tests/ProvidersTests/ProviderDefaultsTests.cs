using ConstructionCrew.Core.Models;
using ConstructionCrew.Providers;

namespace ConstructionCrew.Tests.ProvidersTests;

public class ProviderDefaultsTests
{
    /// <summary>
    /// Under `claude -p`, --allowedTools is a hard allow-list: a Foreman policy that
    /// lists only Bash/Edit/Read/Write silently DENIES file_sitrep and ask_gc, which
    /// reads as a Foreman that works but never reports -- and would make Phase 4's
    /// sitewalk milestone never reach the GC.
    /// </summary>
    [Fact]
    public void ToolPolicy_Claude_LetsAForemanReportAndEscalate()
    {
        var allowed = ProviderDefaults.ToolPolicy("claude")["allowedTools"].Split(',');

        Assert.Contains("Bash", allowed);
        Assert.Contains("Edit", allowed);
        Assert.Contains("Read", allowed);
        Assert.Contains("Write", allowed);
        Assert.Contains("mcp__home_office__file_sitrep", allowed);
        Assert.Contains("mcp__home_office__ask_gc", allowed);
        Assert.Contains("mcp__home_office__build_graph", allowed);
        Assert.Contains("mcp__home_office__spawn_worker", allowed);
        // A Foreman never dispatches to another Foreman.
        Assert.DoesNotContain("mcp__home_office__dispatch_task", allowed);
    }

    /// <summary>
    /// GC authors and dispatches; it still never runs shell commands. Writing the
    /// workorder is step one of the work loop, and filing a sitrep is how the Boss
    /// sees a turn happened -- under `claude -p` a tool outside --allowedTools is
    /// auto-denied, so omitting either reads back as an unapproved permission prompt.
    /// </summary>
    [Fact]
    public void GcToolPolicy_Claude_GrantsWriteEditAndFileSitrep()
    {
        var allowed = ProviderDefaults.GcToolPolicy("claude")["allowedTools"].Split(',');

        Assert.Contains("Write", allowed);
        Assert.Contains("Edit", allowed);
        Assert.Contains("mcp__home_office__file_sitrep", allowed);

        Assert.Contains("mcp__home_office__dispatch_task", allowed);
        Assert.DoesNotContain("Bash", allowed);

        // GC never calls ask_gc: it IS the GC. Granting it would let GC escalate to itself.
        Assert.DoesNotContain("mcp__home_office__ask_gc", allowed);
    }

    /// <summary>
    /// Codex has no per-tool allow-list, so the sandbox policy is the only lever --
    /// and GC's WorkingDirectory is the Vault, so workspace-write scopes its writes
    /// to the Vault and nothing else.
    /// </summary>
    [Fact]
    public void GcToolPolicy_Codex_IsWorkspaceWrite()
    {
        Assert.Equal("workspace-write", ProviderDefaults.GcToolPolicy("codex")["sandbox"]);
    }

    /// <summary>
    /// A roster that predates a ProviderDefaults change never picks it up on its own:
    /// GcToolPolicy is consulted at first-run hire and nowhere else. The merge is a
    /// union, and it preserves the order the roster already had.
    /// </summary>
    [Fact]
    public void EnsureGcToolPolicy_UnionsMissingClaudeTools()
    {
        var current = new Dictionary<string, string> { ["allowedTools"] = "Read" };

        var merged = ProviderDefaults.EnsureGcToolPolicy("claude", current)["allowedTools"].Split(',');

        Assert.Equal("Read", merged[0]);
        Assert.Contains("Write", merged);
        Assert.Contains("Edit", merged);
        Assert.Contains("Glob", merged);
        Assert.Contains("Grep", merged);
        Assert.Contains("mcp__home_office__dispatch_task", merged);
        Assert.Contains("mcp__home_office__file_sitrep", merged);
        Assert.Contains("mcp__home_office__query_graph", merged);

        // Union, not replacement -- and no duplicates from what was already there.
        Assert.Equal(1, merged.Count(t => t == "Read"));
    }

    /// <summary>The MCP wiring Program.cs stamps on lives in the same dictionary.</summary>
    [Fact]
    public void EnsureGcToolPolicy_PreservesUnrelatedKeys()
    {
        var current = new Dictionary<string, string>
        {
            ["allowedTools"] = "Read",
            ["mcpConfigPath"] = "/generated/mcp.json",
        };

        var merged = ProviderDefaults.EnsureGcToolPolicy("claude", current);

        Assert.Equal("/generated/mcp.json", merged["mcpConfigPath"]);
    }

    /// <summary>
    /// read-only is the value this app itself used to write, so it is the one safe to
    /// replace. Any other sandbox is the Boss's own choice and stays.
    /// </summary>
    [Fact]
    public void EnsureGcToolPolicy_CodexUpgradesReadOnlyOnly()
    {
        var upgraded = ProviderDefaults.EnsureGcToolPolicy(
            "codex", new Dictionary<string, string> { ["sandbox"] = "read-only" });
        Assert.Equal("workspace-write", upgraded["sandbox"]);

        var untouched = ProviderDefaults.EnsureGcToolPolicy(
            "codex", new Dictionary<string, string> { ["sandbox"] = "danger-full-access" });
        Assert.Equal("danger-full-access", untouched["sandbox"]);
    }

    /// <summary>
    /// Nothing to add means nothing allocated -- Program.cs uses reference equality to
    /// decide whether the roster on disk actually needs rewriting.
    /// </summary>
    [Fact]
    public void EnsureGcToolPolicy_AlreadyComplete_ReturnsSameInstance()
    {
        var current = new Dictionary<string, string>(ProviderDefaults.GcToolPolicy("claude"));

        Assert.Same(current, ProviderDefaults.EnsureGcToolPolicy("claude", current));

        var codex = new Dictionary<string, string>(ProviderDefaults.GcToolPolicy("codex"));
        Assert.Same(codex, ProviderDefaults.EnsureGcToolPolicy("codex", codex));
    }

    /// <summary>
    /// The whole point of the composer: the tool policy AND the Home Office wiring,
    /// so a provider switch cannot silently drop what Program.cs stamped.
    /// </summary>
    [Fact]
    public void ComposeProviderOptions_Foreman_CarriesToolPolicyAndMcpWiring()
    {
        var composed = ProviderDefaults.ComposeProviderOptions(
            CrewRole.Foreman,
            "claude",
            McpWiring(("claude", new Dictionary<string, string> { ["mcpConfigPath"] = "/tmp/claude-mcp.json" })));

        Assert.Equal("/tmp/claude-mcp.json", composed["mcpConfigPath"]);
        Assert.Contains("Bash", composed["allowedTools"].Split(','));
        Assert.Contains("mcp__home_office__file_sitrep", composed["allowedTools"].Split(','));
    }

    /// <summary>
    /// Role decides which policy is the base. GC dispatches and authors; it never runs
    /// shell commands, so Bash must not appear in its claude allow-list.
    /// </summary>
    [Fact]
    public void ComposeProviderOptions_Gc_UsesGcPolicy()
    {
        var composed = ProviderDefaults.ComposeProviderOptions(
            CrewRole.GC,
            "claude",
            McpWiring(("claude", new Dictionary<string, string> { ["mcpConfigPath"] = "/tmp/gc-mcp.json" })));

        var allowed = composed["allowedTools"].Split(',');

        Assert.DoesNotContain("Bash", allowed);
        Assert.Contains("mcp__home_office__dispatch_task", allowed);
        Assert.Equal("/tmp/gc-mcp.json", composed["mcpConfigPath"]);
    }

    /// <summary>
    /// A provider the Home Office could not wire (not installed, no verified MCP shape)
    /// still gets its own tool policy -- the overlay is the only part that is skipped.
    /// </summary>
    [Fact]
    public void ComposeProviderOptions_UnwiredProvider_StillReturnsToolPolicy()
    {
        var composed = ProviderDefaults.ComposeProviderOptions(
            CrewRole.Foreman,
            "codex",
            McpWiring(("claude", new Dictionary<string, string> { ["mcpConfigPath"] = "/tmp/claude-mcp.json" })));

        Assert.Equal("workspace-write", composed["sandbox"]);
        Assert.False(composed.ContainsKey("mcpConfigPath"));
    }

    /// <summary>
    /// A newly hired Claude crew member reports its own session id from turn
    /// one. Without --output-format json the CLI never states session_id, so
    /// there is nothing to resume one exact conversation against and nothing to
    /// point a transcript tail at.
    /// </summary>
    [Theory]
    [InlineData(CrewRole.Foreman)]
    [InlineData(CrewRole.GC)]
    public void ComposeProviderOptions_Claude_DefaultsToJsonOutput(CrewRole role)
    {
        var composed = ProviderDefaults.ComposeProviderOptions(role, "claude", McpWiring());

        Assert.Equal("json", composed["outputFormat"]);
    }

    /// <summary>Claude's envelope is the only one verified to carry a session id; nobody else gets a flag invented for them.</summary>
    [Fact]
    public void ComposeProviderOptions_OtherProviders_GetNoOutputFormat()
    {
        Assert.False(ProviderDefaults
            .ComposeProviderOptions(CrewRole.Foreman, "codex", McpWiring())
            .ContainsKey("outputFormat"));
        Assert.False(ProviderDefaults
            .ComposeProviderOptions(CrewRole.Foreman, "copilot", McpWiring())
            .ContainsKey("outputFormat"));
    }

    /// <summary>A default, not a hardcoded flag: a Boss who picked a format keeps it.</summary>
    [Fact]
    public void EnsureSessionAccounting_AnExplicitChoice_IsLeftAlone()
    {
        var current = new Dictionary<string, string> { ["outputFormat"] = "text" };

        Assert.Same(current, ProviderDefaults.EnsureSessionAccounting("claude", current));
    }

    /// <summary>
    /// The startup self-heal: a Claude crew member hired before session
    /// accounting existed has no outputFormat, so its turns report no
    /// session_id. Repaired in place, with everything else it carries kept.
    /// </summary>
    [Fact]
    public void EnsureSessionAccounting_LegacyClaudeMember_GainsJsonAndKeepsTheRest()
    {
        var current = new Dictionary<string, string>
        {
            ["allowedTools"] = "Read",
            ["mcpConfigPath"] = "/generated/mcp.json",
        };

        var repaired = ProviderDefaults.EnsureSessionAccounting("claude", current);

        Assert.NotSame(current, repaired);
        Assert.Equal("json", repaired["outputFormat"]);
        Assert.Equal("Read", repaired["allowedTools"]);
        Assert.Equal("/generated/mcp.json", repaired["mcpConfigPath"]);
    }

    /// <summary>Reference equality is how Program.cs decides whether foremen.yaml actually needs rewriting.</summary>
    [Fact]
    public void EnsureSessionAccounting_NothingToDo_ReturnsSameInstance()
    {
        var claude = new Dictionary<string, string> { ["outputFormat"] = "json" };
        Assert.Same(claude, ProviderDefaults.EnsureSessionAccounting("claude", claude));

        var codex = new Dictionary<string, string> { ["sandbox"] = "workspace-write" };
        Assert.Same(codex, ProviderDefaults.EnsureSessionAccounting("codex", codex));
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> McpWiring(
        params (string Provider, IReadOnlyDictionary<string, string> Options)[] entries) =>
        entries.ToDictionary(e => e.Provider, e => e.Options);
}
