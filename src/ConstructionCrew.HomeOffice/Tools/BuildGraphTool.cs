using System.ComponentModel;
using ConstructionCrew.Core.Abstractions;
using ModelContextProtocol.Server;

namespace ConstructionCrew.HomeOffice.Tools;

[McpServerToolType]
public sealed class BuildGraphTool
{
    private readonly IVaultGraph _graph;
    private readonly HomeOfficeVaultOptions _vaultOptions;

    public BuildGraphTool(IVaultGraph graph, HomeOfficeVaultOptions vaultOptions)
    {
        _graph = graph;
        _vaultOptions = vaultOptions;
    }

    [McpServerTool(Name = "build_graph")]
    [Description("Rebuild the Vault's RDF projection (AI/graph/build/schema.ttl and data.ttl) from every typed note's frontmatter. Run this after writing notes, before querying the graph.")]
    public string BuildGraph()
    {
        if (string.IsNullOrWhiteSpace(_vaultOptions.VaultRoot))
        {
            return "build_graph: no Vault is configured. Ask the Boss to run first-run setup (or start with --vault-root).";
        }

        var outDir = Path.Combine(_vaultOptions.VaultRoot, "AI", "graph", "build");
        var result = _graph.Build(_vaultOptions.VaultRoot, outDir);

        return $"Wrote {result.SchemaTripleCount} schema triples to {result.SchemaTtlPath} " +
               $"and {result.DataTripleCount} data triples to {result.DataTtlPath}.";
    }
}
