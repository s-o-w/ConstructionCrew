namespace ConstructionCrew.Core.Abstractions;

public sealed record GraphBuildResult(
    string SchemaTtlPath,
    string DataTtlPath,
    int SchemaTripleCount,
    int DataTripleCount);

/// <summary>
/// The vault's RDF projection: build it from the vault's Markdown frontmatter,
/// and run SPARQL against the built artifact. Pure .NET -- nothing here shells
/// python3, vault_to_rdf.py, or export_graph.sh.
/// </summary>
public interface IVaultGraph
{
    GraphBuildResult Build(string vaultRoot, string outDir);

    string Query(string vaultRoot, string sparql);
}
