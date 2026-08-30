using System.ComponentModel;
using ConstructionCrew.Core.Abstractions;
using ModelContextProtocol.Server;

namespace ConstructionCrew.HomeOffice.Tools;

[McpServerToolType]
public sealed class QueryGraphTool
{
    private readonly IVaultGraph _graph;
    private readonly HomeOfficeVaultOptions _vaultOptions;

    public QueryGraphTool(IVaultGraph graph, HomeOfficeVaultOptions vaultOptions)
    {
        _graph = graph;
        _vaultOptions = vaultOptions;
    }

    [McpServerTool(Name = "query_graph")]
    [Description("""
        Run a SPARQL query over the Vault's RDF projection (schema.ttl + data.ttl seen as one union graph).
        SELECT/ASK return SPARQL Results JSON; CONSTRUCT/DESCRIBE return Turtle.
        Address a known node by its full IRI under <https://vault.weekly.dev/notes/>, or discover it
        structurally (?p a vm:Project ; vld:path "Notes/X/X.md"). Never FILTER on the string form of a subject IRI.
        """)]
    public string QueryGraph(
        [Description("The SPARQL query text. PREFIX vm: <https://vault.weekly.dev/vaultmeta#>, vld: <https://github.com/The-Knowledge-Graph-Guys/vault-ld#>, data: <https://vault.weekly.dev/notes/>.")] string sparql)
    {
        if (string.IsNullOrWhiteSpace(_vaultOptions.VaultRoot))
        {
            return "query_graph: no Vault is configured. Ask the Boss to run first-run setup (or start with --vault-root).";
        }

        try
        {
            return _graph.Query(_vaultOptions.VaultRoot, sparql);
        }
        catch (Exception ex)
        {
            // A malformed query is the agent's own problem to fix, not a tool
            // crash -- hand back the parser's message so it can correct itself.
            return $"query_graph failed: {ex.Message}";
        }
    }
}
