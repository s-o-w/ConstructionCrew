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

    /// <summary>GC reads and dispatches; it never edits code or runs shell commands.</summary>
    [Fact]
    public void GcToolPolicy_Claude_DispatchesButNeverWrites()
    {
        var allowed = ProviderDefaults.GcToolPolicy("claude")["allowedTools"].Split(',');

        Assert.Contains("mcp__home_office__dispatch_task", allowed);
        Assert.DoesNotContain("Bash", allowed);
        Assert.DoesNotContain("Write", allowed);
    }
}
