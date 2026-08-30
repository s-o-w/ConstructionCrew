using ConstructionCrew.Graph;
using VDS.RDF;

namespace ConstructionCrew.Tests.GraphTests;

/// <summary>
/// Unit coverage for the port's decision points, against a purpose-built scratch
/// vault: folder classification, per-folder scoped bases (and the fallback nothing
/// on the real vault reaches), subject minting, wikilink resolution, both exclusion
/// lists, and a query_graph round trip over the union dataset.
/// </summary>
public class VaultGraphTests : IDisposable
{
    private const string ScratchVaultMetaBase = "https://scratch.test/vaultmeta#";
    private const string AlphaId = "urn:uuid:11111111-2222-3333-4444-555555555555";

    private readonly string _vault = Directory.CreateTempSubdirectory("vault-graph-tests-").FullName;
    private readonly string _out;

    public VaultGraphTests()
    {
        _out = Path.Combine(_vault, "AI", "graph", "build");
        Scaffold();
    }

    public void Dispose() => Directory.Delete(_vault, recursive: true);

    [Fact]
    public void Build_ClassifiesOntologyAndVocabularyNotesAsSchema_EverythingElseAsData()
    {
        var (schema, data) = Build();

        Assert.Contains(schema.Triples, t => Subject(t) == ScratchVaultMetaBase + "VaultMeta");
        Assert.Contains(schema.Triples, t => Subject(t) == ScratchVaultMetaBase + "Project");
        Assert.Contains(data.Triples, t => Subject(t) == VaultGraph.DataNamespace + "Beta");

        // A schema note never leaks into the data graph, and vice versa.
        Assert.DoesNotContain(data.Triples, t => Subject(t) == ScratchVaultMetaBase + "Project");
        Assert.DoesNotContain(schema.Triples, t => Subject(t) == VaultGraph.DataNamespace + "Beta");
    }

    [Fact]
    public void Build_MintsSchemaSubjectsUnderTheirOwnFoldersScopedBase()
    {
        var (schema, _) = Build();

        // The folder's own context.jsonld @base governs -- NOT one shared
        // ".../schema/" namespace.
        Assert.Contains(schema.Triples, t => Subject(t) == ScratchVaultMetaBase + "Project");
        Assert.DoesNotContain(schema.Triples, t => Subject(t).StartsWith(VaultGraph.SchemaNamespace + "VaultMeta", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_FallsBackToTheSchemaNamespace_OnlyWhenTheFolderHasNoContext()
    {
        var (schema, _) = Build();

        Assert.Contains(schema.Triples, t => Subject(t) == VaultGraph.SchemaNamespace + "Orphan#Orphan");
    }

    [Fact]
    public void Build_UsesTheFileStemWhenThereIsNoId_AndFlattensANonAbsoluteId()
    {
        var (_, data) = Build();

        // No @id -> readable data:<FileStem>.
        Assert.Contains(data.Triples, t => Subject(t) == VaultGraph.DataNamespace + "Beta");

        // A urn:uuid: @id is NOT an opaque subject -- it flattens onto the
        // governing base. This is the live behaviour, whatever the docs claimed.
        Assert.Contains(data.Triples, t => Subject(t) == VaultGraph.DataNamespace + AlphaId);
        Assert.DoesNotContain(data.Triples, t => Subject(t) == AlphaId);
    }

    [Fact]
    public void Build_ResolvesAWikilinkToItsTargetSubject_AndMintsAnUnresolvedOneUnderTheVaultBase()
    {
        var (_, data) = Build();

        var touches = new Uri(ScratchVaultMetaBase + "touchesProject");

        // Hit: [[Alpha]] resolves to Alpha's own (flattened) subject IRI.
        Assert.Contains(
            data.GetTriplesWithPredicate(touches),
            t => Subject(t) == VaultGraph.DataNamespace + "Beta" && Object(t) == VaultGraph.DataNamespace + AlphaId);

        // Miss: a dangling link mints under the vault data base -- it does not
        // stay a literal.
        Assert.Contains(
            data.GetTriplesWithPredicate(touches),
            t => Subject(t) == VaultGraph.DataNamespace + "Gamma" && Object(t) == VaultGraph.DataNamespace + "Nowhere");
    }

    [Fact]
    public void Build_ExcludesRootAnchoredPrefixesAndPlanningFoldersAtAnyDepth()
    {
        var (_, data) = Build();

        // Root-anchored: Plans/ at the vault root.
        Assert.DoesNotContain(data.Triples, t => Subject(t) == VaultGraph.DataNamespace + "PlansExcluded");

        // Any depth: Notes/DesignAssistant/PLANNING/ -- the leak the shell
        // script's flat prefix list could not express.
        Assert.DoesNotContain(data.Triples, t => Subject(t) == VaultGraph.DataNamespace + "NestedPlanning");

        // ...while its non-PLANNING sibling under the same project still lands.
        Assert.Contains(data.Triples, t => Subject(t) == VaultGraph.DataNamespace + "Sibling");
    }

    [Fact]
    public void Query_SeesSchemaAndDataAsOneUnionGraph()
    {
        new VaultGraph().Build(_vault, _out);

        var results = new VaultGraph().Query(_vault, $$"""
            PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>
            PREFIX vm:   <{{ScratchVaultMetaBase}}>
            PREFIX data: <{{VaultGraph.DataNamespace}}>

            SELECT ?note ?classLabel WHERE {
              ?note vm:touchesProject <{{VaultGraph.DataNamespace}}{{AlphaId}}> .
              <{{VaultGraph.DataNamespace}}{{AlphaId}}> a vm:Project .
              vm:Project rdfs:label ?classLabel .
            }
            """);

        // vm:Project's rdfs:label lives in schema.ttl and the note's
        // touchesProject edge lives in data.ttl -- a single query joining both
        // only binds if the union dataset really carries them together.
        Assert.Contains("Project Class", results);
        Assert.Contains("Beta", results);
    }

    private (IGraph Schema, IGraph Data) Build()
    {
        var result = new VaultGraph().Build(_vault, _out);
        return (Load(result.SchemaTtlPath), Load(result.DataTtlPath));
    }

    private static IGraph Load(string path)
    {
        var graph = new VDS.RDF.Graph();
        graph.LoadFromFile(path);
        return graph;
    }

    private static string Subject(Triple triple) => triple.Subject.ToString();

    private static string Object(Triple triple) => triple.Object.ToString();

    private void Scaffold()
    {
        Write("AI/graph/context.jsonld", $$"""
            {
              "@context": [
                {
                  "@base": "{{VaultGraph.DataNamespace}}",
                  "type": "@type",
                  "id": "@id",
                  "owl": "http://www.w3.org/2002/07/owl#",
                  "rdfs": "http://www.w3.org/2000/01/rdf-schema#",
                  "label": "rdfs:label"
                },
                "Ontologies/VaultMeta/context.jsonld"
              ]
            }
            """);

        Write("AI/graph/Ontologies/VaultMeta/context.jsonld", $$"""
            {
              "@context": {
                "@base": "{{ScratchVaultMetaBase}}",
                "vm": "{{ScratchVaultMetaBase}}",
                "touchesProject": { "@id": "vm:touchesProject", "@type": "@id", "@container": "@set" }
              }
            }
            """);

        Write("AI/graph/Ontologies/VaultMeta/VaultMeta.md", """
            ---
            type: owl:Ontology
            label: Vault Meta
            ---
            """);

        Write("AI/graph/Ontologies/VaultMeta/Classes/Project.md", """
            ---
            type: owl:Class
            label: Project Class
            ---
            """);

        // No context.jsonld in this folder -- the only way to reach the
        // schema-namespace fallback.
        Write("AI/graph/Ontologies/Orphan/Orphan.md", """
            ---
            type: owl:Ontology
            label: Orphan Ontology
            ---
            """);

        Write("Notes/Alpha.md", $"""
            ---
            type: "[[Project]]"
            id: {AlphaId}
            label: Alpha
            ---
            """);

        Write("Notes/Beta.md", """
            ---
            type: "[[Project]]"
            touchesProject: "[[Alpha]]"
            ---
            """);

        Write("Notes/Gamma.md", """
            ---
            type: "[[Project]]"
            touchesProject: "[[Nowhere]]"
            ---
            """);

        Write("Plans/PlansExcluded.md", """
            ---
            type: "[[Project]]"
            ---
            """);

        Write("Notes/DesignAssistant/PLANNING/NestedPlanning.md", """
            ---
            type: planning-doc
            ---
            """);

        Write("Notes/DesignAssistant/Sibling.md", """
            ---
            type: "[[Project]]"
            ---
            """);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_vault, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
