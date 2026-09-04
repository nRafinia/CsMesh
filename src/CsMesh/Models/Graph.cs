using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace CsMesh.Models;

/// <summary>
/// In-memory graph structure storing nodes, edges, and indexed file metadata.
/// </summary>
public sealed class Graph
{
    /// <summary>
    /// Bumped whenever node keying, edge semantics or on-disk shape change.
    /// A graph written by an older version is rejected on load so the user is told to re-index
    /// instead of silently querying a graph built with different rules.
    /// </summary>
    public const int CurrentFormatVersion = 11;

    public string Root { get; set; } = string.Empty;
    public DateTimeOffset BuiltAt { get; set; }
    public string BuiltFromCommit { get; set; } = string.Empty;
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>
    /// Metadata assemblies the indexer managed to load. Low numbers mean poor symbol resolution.
    /// </summary>
    public int ReferenceCount { get; set; }

    /// <summary>
    /// Call sites Roslyn could not bind to any symbol, usually caused by missing assembly
    /// references. Every one of these is a call edge that never made it into the graph.
    /// </summary>
    public int UnresolvedCallSites { get; set; }

    /// <summary>Framework assemblies loaded from the runtime directory.</summary>
    /// <summary>
    /// How many global-using sets were compiled in. Zero means no System namespace at all.
    /// </summary>
    public int GlobalUsingSources { get; set; }

    /// <summary>
    /// Project files left out of the index because nothing builds them. Named rather than
    /// silently dropped: quietly ignoring source is worse than indexing dead source, since the
    /// reader has no way to find out it happened.
    /// </summary>
    public List<string> SkippedProjects { get; set; } = [];

    /// <summary>Why they were skipped, in one phrase.</summary>
    public string SkippedProjectsReason { get; set; } = string.Empty;

    /// <summary>
    /// What decided which projects were indexed, reported whether or not anything was excluded.
    /// A filter that fell back to including everything looks the same as one with nothing to
    /// exclude, and that difference is worth a line.
    /// </summary>
    public string ScopeDecision { get; set; } = string.Empty;

    /// <summary>
    /// Declared ProjectReference edges, by project name. The authority on what depends on what;
    /// symbol edges answer a different question and point the other way for dispatch.
    /// </summary>
    public Dictionary<string, List<string>> ProjectReferences { get; set; } = new();

    public int RuntimeReferences { get; set; }

    /// <summary>
    /// Assemblies loaded from bin/. Zero means the solution was never built, which is the single
    /// most common reason a graph is thin, and it is invisible in a plain reference total.
    /// </summary>
    public int OutputReferences { get; set; }

    public int OutputDirectories { get; set; }

    /// <summary>True when a directory scan hit its file cap and stopped early.</summary>
    public bool ReferencesCapped { get; set; }

    /// <summary>Assemblies that could not be opened. Silent failures here look like nothing.</summary>
    public int ReferencesFailed { get; set; }

    /// <summary>
    /// Call sites the indexer attempted to bind, resolved or not. The ratio against
    /// <see cref="UnresolvedCallSites"/> is what 'doctor' reports as call resolution.
    /// </summary>
    public int TotalCallSites { get; set; }

    /// <summary>
    /// DI registrations skipped because the registered type name matched more than one type in
    /// the graph. Each one is a binding the graph does not have.
    /// </summary>
    public int AmbiguousDiRegistrations { get; set; }

    /// <summary>
    /// Send/Publish call sites skipped because the request short name matched handlers for more
    /// than one fully qualified request type. Linking them would be a false dispatch.
    /// </summary>
    public int AmbiguousMessageDispatches { get; set; }

    /// <summary>
    /// Send/Publish call sites whose request type resolved but had no registered handler.
    /// </summary>
    public int UnmatchedMessageDispatches { get; set; }

    /// <summary>
    /// Where the indexer failed, not just how often. The counters above say a graph is 91%
    /// resolved; these say which 9%, so a thin answer can be told apart from an absent one.
    /// Capped during indexing -- this is a sample to act on, not an exhaustive log.
    /// </summary>
    public List<UnresolvedSite> Unresolved { get; set; } = [];

    /// <summary>
    /// Full counts per kind/reason, independent of the sample cap. The sample is the first N in
    /// traversal order, so proportions taken from it describe the first few files rather than the
    /// solution.
    /// </summary>
    public Dictionary<string, int> UnresolvedByReason { get; set; } = new();

    /// <summary>Full counts per project, so a single bad project is visible as one.</summary>
    public Dictionary<string, int> UnresolvedByProject { get; set; } = new();

    /// <summary>
    /// Compiler errors about declarations, grouped by id. This is where the reason for a thin
    /// graph usually is.
    /// </summary>
    public List<CompilerNote> Diagnostics { get; set; } = [];

    /// <summary>
    /// Registration helpers that bind by convention rather than by name: Scrutor's Scan, MediatR's
    /// assembly registration, FluentValidation, AutoMapper. Recorded so 'doctor' can say the
    /// container is wired by scanning instead of reporting no bindings at all, which reads as a
    /// codebase with no DI rather than one the indexer could not follow.
    /// </summary>
    public List<string> ScanRegistrations { get; set; } = [];

    /// <summary>
    /// Types used here but declared in a referenced assembly. Kept so a lookup that finds nothing
    /// can say why rather than reporting the symbol as absent.
    /// </summary>
    public List<ExternalType> ExternalTypes { get; set; } = [];

    public List<Node> Nodes { get; set; } = [];
    public List<Edge> Edges { get; set; } = [];
    public List<FileStamp> Files { get; set; } = [];

    /// <summary>
    /// Directory stamps used as a cheap gate before rescanning the tree for newly added files.
    /// </summary>
    public List<DirStamp> Dirs { get; set; } = [];

    [JsonIgnore] private FrozenDictionary<int, Edge[]>? _out;
    [JsonIgnore] private FrozenDictionary<int, Edge[]>? _in;
    [JsonIgnore] private FrozenDictionary<int, Node>? _byId;

    /// <summary>
    /// Retrieves a node by its unique ID.
    /// </summary>
    public Node? ById(int id)
    {
        if (id >= 0 && id < Nodes.Count && Nodes[id].Id == id)
            return Nodes[id];

        _byId ??= Nodes.ToFrozenDictionary(n => n.Id);
        return _byId.GetValueOrDefault(id);
    }

    /// <summary>
    /// Freezes the graph and indexes incoming and outgoing edges for efficient lookups.
    /// </summary>
    public void Freeze()
    {
        _out = Edges.GroupBy(e => e.From).ToFrozenDictionary(g => g.Key, g => g.ToArray());
        _in = Edges.GroupBy(e => e.To).ToFrozenDictionary(g => g.Key, g => g.ToArray());
    }

    public IReadOnlyList<Edge> Out(int id)
    {
        if (_out == null) Freeze();
        return _out!.GetValueOrDefault(id, []);
    }

    public IReadOnlyList<Edge> In(int id)
    {
        if (_in == null) Freeze();
        return _in!.GetValueOrDefault(id, []);
    }

    /// <summary>
    /// Resolves symbol queries by exact match, short name, suffix, or substring.
    /// </summary>
    public List<Node> Resolve(string query)
    {
        var q = query.Trim();
        var exact = Nodes.Where(n => string.Equals(n.Name, q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;

        var shortExact = Nodes.Where(n => string.Equals(n.Short, q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (shortExact.Count > 0) return shortExact;

        var suffix = Nodes.Where(n =>
            n.Name.EndsWith("." + q, StringComparison.OrdinalIgnoreCase) ||
            n.Short.EndsWith("." + q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (suffix.Count > 0) return suffix;

        return Nodes.Where(n => n.Short.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .Take(40).ToList();
    }
}