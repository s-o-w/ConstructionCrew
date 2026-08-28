using System.Text.Json;

namespace ConstructionCrew.SiteOffice;

/// <summary>
/// Writes a --mcp-config JSON file pointing a hired CLI at the Site Office.
/// Shape verified for Claude Code specifically (2026-08-28, via a scratch
/// `claude mcp add --transport http` probe -- see IMPLEMENTATION-PLAN.md).
/// Codex/Copilot's equivalent shapes are NOT verified yet; do not assume this
/// same JSON works for them without checking first.
/// </summary>
public static class McpConfigWriter
{
    public static string WriteClaudeCodeConfig(string generatedConfigDirectory, Uri siteOfficeBaseAddress, string fileName = "claude-mcp-config.json")
    {
        Directory.CreateDirectory(generatedConfigDirectory);

        var mcpUrl = new Uri(siteOfficeBaseAddress, "mcp").ToString();

        var document = new
        {
            mcpServers = new
            {
                site_office = new
                {
                    type = "http",
                    url = mcpUrl,
                },
            },
        };

        var path = Path.Combine(generatedConfigDirectory, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
