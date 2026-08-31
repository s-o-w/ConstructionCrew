using ConstructionCrew.App.Tui;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// The /foreman command's testable pieces: parsing the verb (mirrors
/// DriveCommands.TryParseDrive's shape exactly) and the list/dict edit-field
/// parsing that turns a comma-separated prompt answer back into config data.
/// The interactive view/edit loop itself (Spectre prompts) is smoke-tested by
/// hand only, same as HireWizard.Run and FirstRunWizard.Run.
/// </summary>
public class ForemanDetailsCommandTests
{
    [Theory]
    [InlineData("/foreman", true, "")]
    [InlineData("/foreman Frontend", true, "Frontend")]
    [InlineData("/foreman   Frontend  ", true, "Frontend")]
    [InlineData("/foremanx", false, "")]
    [InlineData("/drive Frontend", false, "")]
    public void TryParse_MatchesTheVerbAndExtractsTheTarget(string command, bool expectedMatch, string expectedTarget)
    {
        var matched = ForemanDetailsCommand.TryParse(command, out var target);

        Assert.Equal(expectedMatch, matched);
        Assert.Equal(expectedTarget, target);
    }

    [Fact]
    public void ParseList_BlankInput_ReturnsNull()
    {
        Assert.Null(ForemanDetailsCommand.ParseList(""));
        Assert.Null(ForemanDetailsCommand.ParseList("   "));
        Assert.Null(ForemanDetailsCommand.ParseList(null));
    }

    [Fact]
    public void ParseList_SplitsTrimsAndDropsEmptyEntries()
    {
        var result = ForemanDetailsCommand.ParseList("/vault, /repo ,, /extra");

        Assert.Equal(["/vault", "/repo", "/extra"], result);
    }

    [Fact]
    public void FormatList_RoundTripsWithParseList()
    {
        IReadOnlyList<string> original = ["/vault", "/repo"];

        var formatted = ForemanDetailsCommand.FormatList(original);
        var reparsed = ForemanDetailsCommand.ParseList(formatted);

        Assert.Equal(original, reparsed);
    }

    [Fact]
    public void FormatList_NullOrEmpty_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, ForemanDetailsCommand.FormatList(null));
        Assert.Equal(string.Empty, ForemanDetailsCommand.FormatList([]));
    }

    [Fact]
    public void ParseDict_BlankInput_ReturnsNull()
    {
        Assert.Null(ForemanDetailsCommand.ParseDict(""));
        Assert.Null(ForemanDetailsCommand.ParseDict(null));
    }

    [Fact]
    public void ParseDict_SplitsPairsAndTrims()
    {
        var result = ForemanDetailsCommand.ParseDict("board=https://example/board, prefix = XI");

        Assert.Equal("https://example/board", result!["board"]);
        Assert.Equal("XI", result["prefix"]);
    }

    [Fact]
    public void ParseDict_EntryWithNoEqualsSign_IsSkippedNotThrown()
    {
        // A typo here should cost one entry, not the whole edit.
        var result = ForemanDetailsCommand.ParseDict("board=https://example/board, garbage, prefix=XI");

        Assert.Equal(2, result!.Count);
        Assert.False(result.ContainsKey("garbage"));
    }

    [Fact]
    public void FormatDict_RoundTripsWithParseDict()
    {
        var original = new Dictionary<string, string> { ["board"] = "https://example/board", ["prefix"] = "XI" };

        var formatted = ForemanDetailsCommand.FormatDict(original);
        var reparsed = ForemanDetailsCommand.ParseDict(formatted);

        Assert.Equal(original, reparsed);
    }

    [Fact]
    public void FormatDict_NullOrEmpty_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, ForemanDetailsCommand.FormatDict(null));
        Assert.Equal(string.Empty, ForemanDetailsCommand.FormatDict(new Dictionary<string, string>()));
    }

    [Fact]
    public void ToolPolicyForSwitch_ForemanRole_UsesTheForemanPolicy()
    {
        // Codex has no per-tool allow-list -- sandbox is its analogue, and a
        // Foreman needs write access to do its job.
        var policy = ForemanDetailsCommand.ToolPolicyForSwitch(CrewRole.Foreman, "codex");

        Assert.Equal("workspace-write", policy["sandbox"]);
    }

    [Fact]
    public void ToolPolicyForSwitch_GcRole_UsesTheGcPolicy()
    {
        // GC also writes now (the workorder is step one of the work loop), so codex's
        // sandbox no longer tells the two roles apart -- GC's WorkingDirectory is the
        // Vault, which is what workspace-write scopes it to. Claude's allow-list is
        // where the roles still differ: GC dispatches and never runs shell commands.
        Assert.Equal("workspace-write", ForemanDetailsCommand.ToolPolicyForSwitch(CrewRole.GC, "codex")["sandbox"]);

        var allowed = ForemanDetailsCommand.ToolPolicyForSwitch(CrewRole.GC, "claude")["allowedTools"].Split(',');

        Assert.Contains("mcp__home_office__dispatch_task", allowed);
        Assert.DoesNotContain("Bash", allowed);
    }

    [Fact]
    public void ToolPolicyForSwitch_ClaudeForeman_GrantsTheHomeOfficeToolsNotJustBashEditReadWrite()
    {
        var policy = ForemanDetailsCommand.ToolPolicyForSwitch(CrewRole.Foreman, "claude");

        Assert.Contains("mcp__home_office__file_sitrep", policy["allowedTools"]);
    }
}
