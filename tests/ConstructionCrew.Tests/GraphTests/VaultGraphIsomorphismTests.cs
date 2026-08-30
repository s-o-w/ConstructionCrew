using System.Text;
using ConstructionCrew.Graph;
using VDS.RDF;

namespace ConstructionCrew.Tests.GraphTests;

/// <summary>
/// Phase 1d's acceptance gate: the .NET port's output must be RDF-isomorphic to
/// the Python reference implementation's output, run over the same real vault --
/// never against documented behaviour, which disagrees with the live output in
/// two confirmed places.
///
/// The committed fixture pair under Fixtures/VaultGraph/ is a snapshot of a real
/// `AI/graph/tools/export_graph.sh` run. It is ground truth for THAT vault state:
/// re-snapshot it (copy the vault's AI/graph/build/*.ttl over the fixtures after
/// re-running the exporter) whenever the vault's typed notes change, or this test
/// reports vault drift as a port defect.
/// </summary>
public class VaultGraphIsomorphismTests
{
    /// <summary>
    /// The one expected difference: the Python data.ttl carries these PLANNING/
    /// subjects and the .NET data.ttl deliberately does not. Anything beyond this
    /// list is a port defect.
    /// </summary>
    private static readonly string[] ExpectedPlanningDelta =
    [
        VaultGraph.DataNamespace + "DA-Architecture",
        VaultGraph.DataNamespace + "DA-Architecture-DistributionTools",
        VaultGraph.DataNamespace + "DA-Architecture-GTI",
        VaultGraph.DataNamespace + "DA-Architecture-Overview",
        VaultGraph.DataNamespace + "DA-Architecture-Reference",
        VaultGraph.DataNamespace + "DA-Architecture-SharedServices",
        VaultGraph.DataNamespace + "DA-Architecture-SubstationTools",
        VaultGraph.DataNamespace + "WORK-TODO",
    ];

    [Fact]
    public void Build_AgainstTheRealVault_IsIsomorphicToThePythonExport()
    {
        var vaultRoot = TestVault.Locate();
        Assert.True(
            vaultRoot is not null,
            "This gate diffs against a real vault. Point CONSTRUCTIONCREW_VAULT_ROOT at one, or at ~/Documents/MyObsidianVault.");

        var scratch = Directory.CreateTempSubdirectory("vault-graph-gate-").FullName;
        try
        {
            var built = new VaultGraph().Build(vaultRoot!, scratch);

            // Guard against the whole comparison passing on two empty graphs.
            Assert.True(built.SchemaTripleCount > 100, $"Only {built.SchemaTripleCount} schema triples were produced.");
            Assert.True(built.DataTripleCount > 500, $"Only {built.DataTripleCount} data triples were produced.");

            AssertIsomorphic(
                Load(Path.Combine(TestVault.FixturesDirectory, "schema.ttl")),
                Load(Path.Combine(scratch, "schema.ttl")),
                "schema.ttl",
                subjectsToDropFromExpected: []);

            AssertIsomorphic(
                Load(Path.Combine(TestVault.FixturesDirectory, "data.ttl")),
                Load(Path.Combine(scratch, "data.ttl")),
                "data.ttl",
                ExpectedPlanningDelta);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void Build_AgainstTheRealVault_DropsEveryPlanningSubject()
    {
        var vaultRoot = TestVault.Locate();
        Assert.True(
            vaultRoot is not null,
            "This gate diffs against a real vault. Point CONSTRUCTIONCREW_VAULT_ROOT at one, or at ~/Documents/MyObsidianVault.");

        var pythonData = Load(Path.Combine(TestVault.FixturesDirectory, "data.ttl"));

        // Proves the delta list above is the *real* live gap, not an invented one:
        // every enumerated subject is genuinely present in the Python output.
        foreach (var subject in ExpectedPlanningDelta)
        {
            Assert.True(
                pythonData.GetTriplesWithSubject(new Uri(subject)).Any(),
                $"Expected the Python export to carry PLANNING subject <{subject}>; the fixture may be out of date.");
        }
    }

    [Fact]
    public void Query_AnswersTheDocumentedConsultThaGraphQuery_AgainstTheRealVault()
    {
        var vaultRoot = TestVault.Locate();
        Assert.True(
            vaultRoot is not null,
            "This gate diffs against a real vault. Point CONSTRUCTIONCREW_VAULT_ROOT at one, or at ~/Documents/MyObsidianVault.");

        // Verbatim the shape consult-tha-graph documents: a known node written as
        // a full angle-bracket IRI, joined on rdf:type and named predicates. No
        // FILTER over the string form of a subject URI anywhere.
        var results = new VaultGraph().Query(vaultRoot!, """
            PREFIX vm:   <https://vault.weekly.dev/vaultmeta#>
            PREFIX vld:  <https://github.com/The-Knowledge-Graph-Guys/vault-ld#>
            PREFIX data: <https://vault.weekly.dev/notes/>

            SELECT ?n ?path WHERE {
              <https://vault.weekly.dev/notes/urn:uuid:11225767-2e88-485a-82b5-093a1070676b>
                  a vm:Project .
              ?n  vm:touchesProject <https://vault.weekly.dev/notes/urn:uuid:11225767-2e88-485a-82b5-093a1070676b> ;
                  vm:audience vm:External ;
                  vld:path ?path .
            }
            """);

        Assert.Contains("\"path\"", results);
        Assert.Contains("Notes/", results);
    }

    private static IGraph Load(string path)
    {
        var graph = new VDS.RDF.Graph();
        graph.LoadFromFile(path);
        return graph;
    }

    private static void AssertIsomorphic(IGraph expected, IGraph actual, string label, string[] subjectsToDropFromExpected)
    {
        foreach (var subject in subjectsToDropFromExpected)
        {
            foreach (var triple in expected.GetTriplesWithSubject(new Uri(subject)).ToList())
            {
                expected.Retract(triple);
            }
        }

        var report = expected.Difference(actual);
        if (report.AreEqual)
        {
            return;
        }

        var message = new StringBuilder();
        message.AppendLine($"{label} is not isomorphic to the Python export ({expected.Triples.Count} expected vs {actual.Triples.Count} actual triples).");
        Describe(message, "Missing from the .NET output", report.RemovedTriples);
        Describe(message, "Extra in the .NET output", report.AddedTriples);
        Assert.Fail(message.ToString());
    }

    private static void Describe(StringBuilder message, string heading, IEnumerable<Triple> triples)
    {
        var list = triples.ToList();
        if (list.Count == 0)
        {
            return;
        }

        message.AppendLine($"{heading} ({list.Count}):");
        foreach (var triple in list.Take(40))
        {
            message.AppendLine($"  {triple}");
        }

        if (list.Count > 40)
        {
            message.AppendLine($"  ... and {list.Count - 40} more");
        }
    }
}
