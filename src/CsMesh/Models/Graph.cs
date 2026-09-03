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
    public const int CurrentFormatVersion = 2;

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
