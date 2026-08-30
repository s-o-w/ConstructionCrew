using System.Text.Json;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.HomeOffice;
using ConstructionCrew.Providers;

namespace ConstructionCrew.Tests.HomeOfficeTests;

/// <summary>
/// Round-trips each provider's Home Office wiring: write the config, read it back off
/// disk, then feed the returned ProviderOptions through that provider and confirm the
/// Home Office URL actually survives into the argv the CLI would be launched with.
/// Writing a file nobody can consume is the failure mode these guard.
/// </summary>
public class McpConfigWriterTests : IDisposable
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:5199/");
    private const string ExpectedUrl = "http://127.0.0.1:5199/mcp";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cc-mcp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static string ReadServerUrlFromJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement
            .GetProperty("mcpServers")
            .GetProperty(McpConfigWriter.HomeOfficeServerName)
            .GetProperty("url")
            .GetString()!;
    }

    private static CliTaskRequest RequestWith(IReadOnlyDictionary<string, string> options) =>
        new("do the thing", "/work", options);

    [Fact]
    public void Claude_RoundTripsThroughTheConfigFileAndOntoTheCommandLine()
    {
        var wiring = McpConfigWriter.WriteClaudeCodeConfig(_directory, BaseAddress);

        Assert.Equal(ExpectedUrl, ReadServerUrlFromJson(wiring.ConfigPath));

        var args = new ClaudeCodeProvider().BuildInvocation(RequestWith(wiring.ProviderOptions)).Arguments.ToList();
        Assert.Equal(wiring.ConfigPath, args[args.IndexOf("--mcp-config") + 1]);
    }

    [Fact]
    public void Codex_RoundTripsThroughTomlAndOntoTheCommandLineAsADottedOverride()
    {
        var wiring = McpConfigWriter.WriteCodexConfig(_directory, BaseAddress);

        // The file matches exactly what `codex mcp add --url` writes into config.toml.
        var toml = File.ReadAllText(wiring.ConfigPath);
        Assert.Contains($"[mcp_servers.{McpConfigWriter.HomeOfficeServerName}]", toml);
        Assert.Contains($"url = \"{ExpectedUrl}\"", toml);

        // Codex has no config-file flag, so the URL is what has to reach the CLI.
        var args = new CodexProvider().BuildInvocation(RequestWith(wiring.ProviderOptions)).Arguments.ToList();
        var index = args.IndexOf("-c");
        Assert.True(index >= 0, "Expected a -c config override carrying the MCP server URL.");
        Assert.Equal($"mcp_servers.{McpConfigWriter.HomeOfficeServerName}.url=\"{ExpectedUrl}\"", args[index + 1]);
    }

    [Fact]
    public void Copilot_RoundTripsThroughTheConfigFileAndOntoTheCommandLine()
    {
        var wiring = McpConfigWriter.WriteCopilotConfig(_directory, BaseAddress);

        Assert.Equal(ExpectedUrl, ReadServerUrlFromJson(wiring.ConfigPath));

        var args = new CopilotProvider().BuildInvocation(RequestWith(wiring.ProviderOptions)).Arguments.ToList();
        Assert.Equal("@" + wiring.ConfigPath, args[args.IndexOf("--additional-mcp-config") + 1]);
    }

    [Fact]
    public void Write_DispatchesByProviderId_AndReturnsNullForAnUnverifiedProvider()
    {
        Assert.NotNull(McpConfigWriter.Write("claude", _directory, BaseAddress));
        Assert.NotNull(McpConfigWriter.Write("CODEX", _directory, BaseAddress));
        Assert.NotNull(McpConfigWriter.Write("copilot", _directory, BaseAddress));

        // Gemini's MCP shape has never been verified -- no config, no exception.
        Assert.Null(McpConfigWriter.Write("gemini", _directory, BaseAddress));
    }

    [Fact]
    public void CopilotServerName_SatisfiesCopilotsOwnNameConstraint()
    {
        // Copilot's bundled schema rejects anything but alphanumerics, underscores
        // and hyphens in an MCP server name.
        Assert.Matches("^[A-Za-z0-9_-]+$", McpConfigWriter.HomeOfficeServerName);
        Assert.Equal(McpConfigWriter.HomeOfficeServerName, CopilotProvider.HomeOfficeServerName);
        Assert.Equal(McpConfigWriter.HomeOfficeServerName, CodexProvider.HomeOfficeServerName);
    }
}
