using System.Collections;
using ConstructionCrew.Core.Abstractions;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;
using VDS.RDF.Query.Datasets;
using VDS.RDF.Writing;

namespace ConstructionCrew.Graph;

/// <summary>
/// Pure-.NET projection of a Vault-LD vault to RDF, and SPARQL over the result.
/// <see cref="Build"/> is a port of vault-ld's vault_to_rdf.py -- ported from the
/// live script and verified by triple-level isomorphism against its real output,
/// not from any documentation of it.
///
/// One deliberate divergence from the shell wrapper it replaces: a PLANNING/
/// folder is excluded at any depth, not just at the vault root. export_graph.sh's
/// flat prefix list could not express that, so eight agent-executable planning
/// docs under Notes/&lt;Project&gt;/PLANNING/ leaked into the data graph. The vault's
/// own convention is that agent-executable plans are not in the graph.
/// </summary>
public sealed class VaultGraph : IVaultGraph
{
    public const string DataNamespace = "https://vault.weekly.dev/notes/";
    public const string SchemaNamespace = "https://vault.weekly.dev/schema/";
    public const string VldNamespace = "https://github.com/The-Knowledge-Graph-Guys/vault-ld#";

    private const string VldPathIri = VldNamespace + "path";
    private const string RdfTypeIri = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";

    /// <summary>
    /// The nine root-anchored prefixes export_graph.sh carries as -not -path
    /// "./X/*". They only ever match at the vault root.
    /// </summary>
    private static readonly string[] ExcludedRootPrefixes =
    [
        "AI/claude-home",
        "AI/shared-skills",
        "AI/codex-home",
        "AI/graph/build",
        "Plans",
        "Journal",
        "ARCHIVE",
        ".claude",
        ".agents",
    ];

    /// <summary>Folder names excluded wherever they appear, not only at the root.</summary>
    private static readonly string[] ExcludedFolderNamesAtAnyDepth = ["PLANNING"];

    public GraphBuildResult Build(string vaultRoot, string outDir)
    {
        var vault = Normalize(vaultRoot);
        var contextPath = Path.Combine(vault, "AI", "graph", "context.jsonld");
        if (!File.Exists(contextPath))
        {
            throw new FileNotFoundException($"Vault graph context not found at {contextPath}", contextPath);
        }

        var context = JsonLdContext.Load(contextPath);
        var builder = new BuildRun(vault, context);
        return builder.Run(outDir);
    }

    public string Query(string vaultRoot, string sparql)
    {
        var vault = Normalize(vaultRoot);
        var buildDir = Path.Combine(vault, "AI", "graph", "build");
        var schemaPath = Path.Combine(buildDir, "schema.ttl");
        var dataPath = Path.Combine(buildDir, "data.ttl");

        if (!File.Exists(schemaPath) || !File.Exists(dataPath))
        {
            throw new FileNotFoundException(
                $"The vault graph has not been built yet -- expected {schemaPath} and {dataPath}. Run build_graph first.");
        }

        var store = new TripleStore();
        store.Add(LoadNamedGraph(schemaPath, "urn:constructioncrew:vault-graph:schema"));
        store.Add(LoadNamedGraph(dataPath, "urn:constructioncrew:vault-graph:data"));

        var dataset = new InMemoryDataset(store, unionDefaultGraph: true);
        var query = new SparqlQueryParser().ParseFromString(sparql);
        var result = new LeviathanQueryProcessor(dataset).ProcessQuery(query);

        var output = new System.IO.StringWriter();
        switch (result)
        {
            case SparqlResultSet resultSet:
                new SparqlJsonWriter().Save(resultSet, output);
                break;
            case IGraph graph:
                new CompressingTurtleWriter(TurtleSyntax.W3C).Save(graph, output, leaveOpen: true);
                break;
            default:
                output.Write(result?.ToString() ?? string.Empty);
                break;
        }

        return output.ToString();
    }

    private static IGraph LoadNamedGraph(string path, string name)
    {
        var graph = new VDS.RDF.Graph(new UriNode(new Uri(name)));
        graph.LoadFromFile(path);
        return graph;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private enum Layer
    {
        Schema,
        Data,
    }

    /// <summary>
    /// What kind of resource a note's folder implies. Used only for the canonical
    /// placement check that decides whether a schema note needs a vld:path -- it
    /// never classifies the note (folder location does that).
    /// </summary>
    private enum Expected
    {
        None,
        Class,
        Property,
        Ontology,
        Scheme,
        Concept,
    }

    private sealed record Note(
        string AbsolutePath,
        string RelativePath,
        string[] RelativeParts,
        string Stem,
        Dictionary<string, object?> Frontmatter,
        Layer Layer,
        Expected Expected);

    /// <summary>One whole Build call's state -- caches, indexes, and the two output graphs.</summary>
    private sealed class BuildRun
    {
        private readonly string _vault;
        private readonly JsonLdContext _context;
        private readonly string _rootBase = DataNamespace;

        // Base cache, per governing folder (absolute path).
        private readonly Dictionary<string, string> _folderBases = new(StringComparer.Ordinal);

        private readonly Dictionary<string, string> _subjectByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _subjectByRelPath = new(StringComparer.Ordinal);

        public BuildRun(string vault, JsonLdContext context)
        {
            _vault = vault;
            _context = context;
        }

        public GraphBuildResult Run(string outDir)
        {
            var notes = Discover();

            // Pass 1b: mint every subject first, then index by note name AND by
            // vault-relative path, so a path-qualified wikilink can select among
            // same-named notes.
            var subjects = new string[notes.Count];
            for (var i = 0; i < notes.Count; i++)
            {
                var note = notes[i];
                var subject = SubjectIri(note);
                subjects[i] = subject;

                _subjectByRelPath[StripExtension(note.RelativePath)] = subject;
                _subjectByName[note.Stem] = subject;
            }

            var schemaGraph = NewGraph();
            var dataGraph = NewGraph();

            for (var i = 0; i < notes.Count; i++)
            {
                Emit(notes[i], subjects[i], notes[i].Layer == Layer.Schema ? schemaGraph : dataGraph);
            }

            Directory.CreateDirectory(outDir);
            var schemaOut = Path.Combine(outDir, "schema.ttl");
            var dataOut = Path.Combine(outDir, "data.ttl");

            var writer = new CompressingTurtleWriter(TurtleSyntax.W3C);
            writer.Save(schemaGraph, schemaOut);
            writer.Save(dataGraph, dataOut);

            return new GraphBuildResult(schemaOut, dataOut, schemaGraph.Triples.Count, dataGraph.Triples.Count);
        }

        private IGraph NewGraph()
        {
            var graph = new VDS.RDF.Graph();
            foreach (var (prefix, ns) in _context.Prefixes)
            {
                graph.NamespaceMap.AddNamespace(prefix, new Uri(ns));
            }

            graph.NamespaceMap.AddNamespace("vld", new Uri(VldNamespace));
            graph.NamespaceMap.AddNamespace("data", new Uri(_rootBase));
            return graph;
        }

        /// <summary>
        /// Pass 1a: every *.md under the vault whose frontmatter parses to a
        /// mapping and carries a type/@type key, minus both exclusion lists.
        /// Ordered by vault-relative path so a duplicate note name resolves the
        /// same way the reference implementation resolves it (last one wins).
        /// </summary>
        private List<Note> Discover()
        {
            var notes = new List<Note>();

            var candidates = Directory
                .EnumerateFiles(_vault, "*.md", SearchOption.AllDirectories)
                .Select(p => (Absolute: p, Relative: RelativePosix(p)))
                .Where(p => !IsExcluded(p.Relative))
                .OrderBy(p => p.Relative, StringComparer.Ordinal);

            foreach (var (absolute, relative) in candidates)
            {
                // A note reached through a symlink is skipped: a hostile vault must
                // not pull files from outside its own tree into the export.
                if (IsSymlink(absolute))
                {
                    continue;
                }

                var frontmatter = FrontmatterReader.Read(absolute);
                if (frontmatter is null)
                {
                    continue;
                }

                frontmatter = CanonicalKeywords(frontmatter);
                if (!frontmatter.ContainsKey("@type"))
                {
                    continue;
                }

                var parts = relative.Split('/');
                var stem = Path.GetFileNameWithoutExtension(absolute);
                var (layer, expected) = Locate(parts, stem);

                notes.Add(new Note(absolute, relative, parts, stem, frontmatter, layer, expected));
            }

            return notes;
        }

        private static bool IsSymlink(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        private string RelativePosix(string absolute) =>
            Path.GetRelativePath(_vault, absolute).Replace(Path.DirectorySeparatorChar, '/');

        private static bool IsExcluded(string relative)
        {
            foreach (var prefix in ExcludedRootPrefixes)
            {
                if (relative.StartsWith(prefix + "/", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            var parts = relative.Split('/');
            // The last part is the file name itself; only folder segments count.
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (ExcludedFolderNamesAtAnyDepth.Contains(parts[i], StringComparer.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Rename context-declared keyword aliases to the keywords themselves
        /// ("type" -> "@type", "id" -> "@id"), so everything downstream reasons
        /// over one canonical spelling.
        /// </summary>
        private Dictionary<string, object?> CanonicalKeywords(Dictionary<string, object?> frontmatter)
        {
            if (_context.Aliases.Count == 0)
            {
                return frontmatter;
            }

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in frontmatter)
            {
                result[_context.Aliases.GetValueOrDefault(key, key)] = value;
            }

            return result;
        }

        /// <summary>Folder location decides the layer: Ontologies/ and Vocabularies/ are schema, everything else is data.</summary>
        private static (Layer Layer, Expected Expected) Locate(string[] parts, string stem)
        {
            if (parts.Contains("Ontologies", StringComparer.Ordinal))
            {
                if (parts.Contains("Classes", StringComparer.Ordinal))
                {
                    return (Layer.Schema, Expected.Class);
                }

                if (parts.Contains("Properties", StringComparer.Ordinal))
                {
                    return (Layer.Schema, Expected.Property);
                }

                return (Layer.Schema, Expected.Ontology);
            }

            if (parts.Contains("Vocabularies", StringComparer.Ordinal))
            {
                // The scheme file shares its name with its folder; the rest are concepts.
                var parent = parts.Length >= 2 ? parts[^2] : string.Empty;
                return stem.Equals(parent, StringComparison.Ordinal)
                    ? (Layer.Schema, Expected.Scheme)
                    : (Layer.Schema, Expected.Concept);
            }

            return (Layer.Data, Expected.None);
        }

        /// <summary>
        /// The governing folder for a schema note: .../Ontologies/&lt;Name&gt;/ or
        /// .../Vocabularies/&lt;Name&gt;/, plus that &lt;Name&gt;.
        /// </summary>
        private (string Folder, string Name) SchemaGoverningFolder(Note note)
        {
            foreach (var anchor in new[] { "Ontologies", "Vocabularies" })
            {
                var index = Array.IndexOf(note.RelativeParts, anchor);
                if (index < 0)
                {
                    continue;
                }

                if (index + 1 >= note.RelativeParts.Length)
                {
                    throw new InvalidOperationException(
                        $"'{note.RelativePath}' sits at the '{anchor}' anchor with no ontology/vocabulary name after it.");
                }

                var name = note.RelativeParts[index + 1];
                var segments = new List<string> { _vault };
                segments.AddRange(note.RelativeParts[..(index + 1)]);
                segments.Add(name);
                return (Path.Combine([.. segments]), name);
            }

            throw new InvalidOperationException($"'{note.RelativePath}' was classified as schema but sits under neither anchor.");
        }

        /// <summary>
        /// The (base, folder) of the note's governing context: a schema note's own
        /// ontology/vocabulary context, otherwise the nearest context.jsonld at or
        /// above the note -- the vault root in the end.
        /// </summary>
        private (string Base, string Folder) GoverningFor(Note note)
        {
            if (note.Layer == Layer.Schema)
            {
                var (folder, name) = SchemaGoverningFolder(note);
                return (BaseForSchemaFolder(folder, name), folder);
            }

            var directory = Path.GetDirectoryName(note.AbsolutePath)!;
            while (!SamePath(directory, _vault) && !File.Exists(Path.Combine(directory, "context.jsonld")))
            {
                var parent = Path.GetDirectoryName(directory);
                if (parent is null || parent == directory)
                {
                    return (_rootBase, _vault);
                }

                directory = parent;
            }

            if (SamePath(directory, _vault))
            {
                return (_rootBase, _vault);
            }

            return (ScopedBaseCached(directory) ?? _rootBase, directory);
        }

        /// <summary>
        /// Each ontology/vocabulary folder mints its members under the @base its
        /// own context.jsonld declares. Only when that folder has no context does
        /// the schema-namespace fallback apply -- nothing on a real Vault-LD vault
        /// with per-folder contexts reaches it.
        /// </summary>
        private string BaseForSchemaFolder(string folder, string name)
        {
            if (_folderBases.TryGetValue(folder, out var cached))
            {
                return cached;
            }

            var scoped = JsonLdContext.ScopedBase(Path.Combine(folder, "context.jsonld"))
                         ?? SchemaNamespace.TrimEnd('/') + "/" + name + "#";
            _folderBases[folder] = scoped;
            return scoped;
        }

        private string? ScopedBaseCached(string folder)
        {
            if (_folderBases.TryGetValue(folder, out var cached))
            {
                return cached;
            }

            var scoped = JsonLdContext.ScopedBase(Path.Combine(folder, "context.jsonld"));
            if (scoped is not null)
            {
                _folderBases[folder] = scoped;
            }

            return scoped;
        }

        /// <summary>Identity from the file name alone: base + stem, percent-encoded. Folders never enter an IRI.</summary>
        private string MintedIri(Note note) => GoverningFor(note).Base + Uri.EscapeDataString(note.Stem);

        private string SubjectIri(Note note)
        {
            if (note.Frontmatter.TryGetValue("@id", out var raw))
            {
                var token = Scalar(raw).Trim();

                // A non-absolute @id (every urn:uuid: in this vault) is flattened
                // onto the governing base -- it does NOT stay an opaque urn:uuid:
                // IRI. That is the real, verified behaviour of the reference
                // implementation, whatever the skill's docs used to claim.
                return token.StartsWith("http://", StringComparison.Ordinal) || token.StartsWith("https://", StringComparison.Ordinal)
                    ? token
                    : GoverningFor(note).Base + token;
            }

            return MintedIri(note);
        }

        private void Emit(Note note, string subjectIri, IGraph graph)
        {
            var subject = graph.CreateUriNode(new Uri(subjectIri));

            EmitPath(note, subjectIri, subject, graph);

            foreach (var (key, raw) in note.Frontmatter)
            {
                if (key == "@id")
                {
                    continue;
                }

                var values = raw is IList list
                    ? list.Cast<object?>().ToList()
                    : [raw];

                if (key == "@type")
                {
                    foreach (var value in values)
                    {
                        graph.Assert(subject, graph.CreateUriNode(new Uri(RdfTypeIri)), graph.CreateUriNode(new Uri(ResolveIri(Scalar(value)))));
                    }

                    continue;
                }

                // An unmapped key contributes nothing: a host key (tags, aliases,
                // cssclasses) is an editor affordance, and anything else is simply
                // not in the context.
                if (!_context.Terms.TryGetValue(key, out var term))
                {
                    continue;
                }

                var predicate = graph.CreateUriNode(new Uri(_context.ExpandCurie(term.Id)));

                foreach (var value in values)
                {
                    var text = Scalar(value);
                    INode obj = term.Coercion switch
                    {
                        "@id" => graph.CreateUriNode(new Uri(ResolveIri(text))),
                        { } coercion => graph.CreateLiteralNode(text, new Uri(_context.ExpandCurie(coercion))),
                        _ => graph.CreateLiteralNode(text),
                    };

                    graph.Assert(subject, predicate, obj);
                }
            }
        }

        /// <summary>
        /// vld:path carries the true path of every note whose location cannot be
        /// reconstructed from the graph, relative to its governing folder.
        /// </summary>
        private void EmitPath(Note note, string subjectIri, INode subject, IGraph graph)
        {
            var (_, folder) = GoverningFor(note);
            var relativeToFolder = Path.GetRelativePath(folder, note.AbsolutePath).Replace(Path.DirectorySeparatorChar, '/');

            var divergent = note.Layer == Layer.Data
                ? !string.Equals(subjectIri, MintedIri(note), StringComparison.Ordinal)
                  || !SamePath(Path.GetDirectoryName(note.AbsolutePath)!, folder)
                : !string.Equals(relativeToFolder, CanonicalRelative(note), StringComparison.Ordinal);

            if (divergent)
            {
                graph.Assert(subject, graph.CreateUriNode(new Uri(VldPathIri)), graph.CreateLiteralNode(relativeToFolder));
            }
        }

        /// <summary>
        /// The flat, reconstructable placement for a schema note. Anything else
        /// (hierarchy nesting included) is organisational and travels as vld:path.
        /// </summary>
        private static string CanonicalRelative(Note note)
        {
            var fileName = Path.GetFileName(note.AbsolutePath);
            return note.Expected switch
            {
                Expected.Class => "Classes/" + fileName,
                Expected.Property => "Properties/" + fileName,
                _ => fileName,
            };
        }

        /// <summary>
        /// Resolve a wikilink or CURIE/IRI value to a full IRI. A path-qualified
        /// link selects among same-named notes by matching its path right-aligned
        /// on segment boundaries, the way Obsidian's shortest-sufficient-path
        /// links work. A link that resolves to nothing mints under the vault data
        /// base rather than staying a literal.
        /// </summary>
        private string ResolveIri(string token)
        {
            if (!IsWikilink(token))
            {
                return _context.ExpandCurie(token);
            }

            var target = WikilinkTarget(token);
            var name = target[(target.LastIndexOf('/') + 1)..];

            if (target.Contains('/'))
            {
                var hits = _subjectByRelPath
                    .Where(kv => kv.Key == target || kv.Key.EndsWith("/" + target, StringComparison.Ordinal))
                    .Select(kv => kv.Value)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (hits.Count == 1)
                {
                    return hits[0];
                }

                if (hits.Count > 1)
                {
                    hits.Sort(StringComparer.Ordinal);
                    return hits[0];
                }
            }

            return _subjectByName.TryGetValue(name, out var iri)
                ? iri
                : _rootBase + Uri.EscapeDataString(name);
        }

        private static bool IsWikilink(string token)
        {
            var trimmed = token.Trim();
            return trimmed.StartsWith("[[", StringComparison.Ordinal) && trimmed.EndsWith("]]", StringComparison.Ordinal);
        }

        /// <summary>Alias and fragment stripped, any disambiguating path kept.</summary>
        private static string WikilinkTarget(string token)
        {
            var inner = token.Trim();
            inner = inner[2..^2];
            inner = inner.Split('|', 2)[0];
            inner = inner.Split('#', 2)[0];
            return inner.Trim();
        }

        private static string StripExtension(string relativePosixPath)
        {
            var lastSlash = relativePosixPath.LastIndexOf('/');
            var lastDot = relativePosixPath.LastIndexOf('.');
            return lastDot > lastSlash ? relativePosixPath[..lastDot] : relativePosixPath;
        }

        private static string Scalar(object? value) => value switch
        {
            null => string.Empty,
            string s => s,
            bool b => b ? "true" : "false",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        };

        private static bool SamePath(string a, string b) =>
            string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                Core.PathComparison.ForPathPrefix);
    }
}
