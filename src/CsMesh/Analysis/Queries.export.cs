using System.Security.Cryptography;
using System.Text;
using CsMesh.Models;

namespace CsMesh.Analysis;

/// <summary>
/// Renders the graph as a diagram a person or a design doc can read, at the project,
/// namespace or symbol-neighbourhood level (ADR 0004).
///
/// The renderer produces the complete text; the caller decides how to bound it. Without
/// <c>--out</c> the lines go through <see cref="Common.BudgetWriter"/> and overflow exits 2;
/// with <c>--out</c> they are the file and stdout carries only a summary. Keeping the render
/// free of the budget is what lets the same bytes be both the truncated answer and the
/// complete artifact.
///
/// Determinism is a contract: the same tree indexed twice must render byte-identically. Every
/// collection that reaches the output is sorted by identity text, never by <see cref="Node.Id"/>
/// or a hash-set enumeration order.
/// </summary>
public static partial class Queries
{
    /// <summary>What to draw, decoupled from the options parser so it is testable directly.</summary>
    public sealed record ExportRequest(
        string Format,
        string Level,
        Node? Start,
        int Depth,
        string Direction,
        bool IncludeTests,
        bool IncludeTypeUse);

    /// <summary>
    /// The complete rendering plus the numbers a summary needs. Counts of withheld items are raw
    /// graph counts, matching how the ADR measures them; <see cref="Nodes"/>/<see cref="Edges"/> are
    /// the collapsed, filtered ones the diagram actually contains.
    /// </summary>
    public sealed record ExportResult(
        IReadOnlyList<string> Lines,
        int Nodes,
        int Edges,
        int TestNodesWithheld,
        int TestEdgesWithheld,
        int TypeUseEdgesWithheld,
        int NodesBeyondDepth,
        int EdgesBeyondDepth);

    private readonly record struct Bucket(string Identity, string Label);

    private readonly record struct DrawnEdge(string FromIdentity, string ToIdentity, EdgeKind Kind, EdgeRole? Role, double Score);

    /// <summary>The raw counts of everything the diagram's filters removed.</summary>
    private readonly record struct Withheld(int TestNodes, int TestEdges, int TypeUseEdges);

    /// <summary>A level's filtered graph plus what a depth limit left out of it.</summary>
    private readonly record struct LevelResult(
        List<Bucket> Nodes, List<DrawnEdge> Edges, Withheld Withheld, int BeyondNodes, int BeyondEdges);

    public static ExportResult RenderExport(Graph g, ExportRequest req)
    {
        LevelResult level = req.Level switch
        {
            "project" => Collapse(g, req, ProjectOf),
            "namespace" => Collapse(g, req, NamespaceResolver(g)),
            "neighbourhood" => Neighbourhood(g, req),
            _ => throw new ArgumentOutOfRangeException(nameof(req), req.Level)
        };

        var ids = AssignIds(level.Nodes.Select(n => n.Identity).ToList(), Sha256Hex, PrefixFor(req.Level));

        var lines = req.Format == "dot"
            ? RenderDot(level.Nodes, ids, level.Edges)
            : RenderMermaid(level.Nodes, ids, level.Edges);

        return new ExportResult(lines, level.Nodes.Count, level.Edges.Count,
            level.Withheld.TestNodes, level.Withheld.TestEdges, level.Withheld.TypeUseEdges,
            level.BeyondNodes, level.BeyondEdges);
    }

    private static string PrefixFor(string level) => level switch
    {
        "namespace" => "ns",
        "neighbourhood" => "s",
        _ => "p"
    };

    // ------------------------------------------------------------------ levels

    /// <summary>
    /// Collapses the graph to project or namespace buckets. An edge is kept only when both endpoints
    /// are included, and distinct (from, to, kind) triples become one drawn edge; a self-edge counts
    /// once, as the ADR measures it.
    /// </summary>
    private static LevelResult Collapse(Graph g, ExportRequest req, Func<Node, string?> identityOf)
    {
        var includedIds = new HashSet<int>();
        var testNodes = 0;

        foreach (var n in g.Nodes)
        {
            if (!req.IncludeTests && IsTest(n)) { testNodes++; continue; }
            includedIds.Add(n.Id);
        }

        var testEdges = 0;
        var typeUseEdges = 0;
        var collapsed = new Dictionary<(string From, string To, EdgeKind Kind), (EdgeRole? Role, double Score)>();

        foreach (var e in g.Edges)
        {
            if (!includedIds.Contains(e.From) || !includedIds.Contains(e.To)) { testEdges++; continue; }
            if (e.Kind == EdgeKind.TypeUse && !req.IncludeTypeUse) { typeUseEdges++; continue; }

            // A node with no namespace of its own (a tuple-typed synthetic node, say) is not placed
            // in a bucket at any level; an edge that lands on one has nowhere to point here.
            if (identityOf(g.ById(e.From)!) is not { } fromIdentity) continue;
            if (identityOf(g.ById(e.To)!) is not { } toIdentity) continue;

            var key = (fromIdentity, toIdentity, e.Kind);
            collapsed[key] = collapsed.TryGetValue(key, out var existing)
                ? (Or(existing.Role, e.Role), Math.Min(existing.Score, e.Score))
                : (e.Role, e.Score);
        }

        var nodes = includedIds
            .Select(id => g.ById(id)!)
            .Select(identityOf)
            .Where(x => x != null)
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(x => new Bucket(x, x.Length > 0 ? x : EmptyLabel(req.Level)))
            .ToList();

        var edges = collapsed
            .OrderBy(x => x.Key.From, StringComparer.Ordinal)
            .ThenBy(x => x.Key.To, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Kind)
            .Select(x => new DrawnEdge(x.Key.From, x.Key.To, x.Key.Kind, x.Value.Role, x.Value.Score))
            .ToList();

        return new LevelResult(nodes, edges, new Withheld(testNodes, testEdges, typeUseEdges), 0, 0);
    }

    /// <summary>
    /// The undirected (or one-way) ring around one symbol, its induced subgraph at
    /// <see cref="ExportRequest.Depth"/> and the counts a deeper walk would have added.
    ///
    /// BFS records every node's distance, so the depth limit is applied after the walk rather than
    /// during it: that is what lets the summary say how much lies beyond the depth, which the ADR's
    /// <c>--out</c> summary reports. The walk itself is bounded by the visited set, so a cycle
    /// terminates.
    /// </summary>
    private static LevelResult Neighbourhood(Graph g, ExportRequest req)
    {
        var start = req.Start ?? throw new ArgumentException("neighbourhood needs a start node", nameof(req));

        var includedIds = new HashSet<int>();
        var testNodes = 0;

        foreach (var n in g.Nodes)
        {
            if (!req.IncludeTests && IsTest(n)) { testNodes++; continue; }
            includedIds.Add(n.Id);
        }

        var testEdges = 0;
        var typeUseEdges = 0;
        var traversed = new Dictionary<(int From, int To, EdgeKind Kind), (EdgeRole? Role, double Score)>();

        foreach (var e in g.Edges)
        {
            if (!includedIds.Contains(e.From) || !includedIds.Contains(e.To)) { testEdges++; continue; }
            if (e.Kind == EdgeKind.TypeUse && !req.IncludeTypeUse) { typeUseEdges++; continue; }
        }

        var distance = new Dictionary<int, int>();
        if (includedIds.Contains(start.Id))
        {
            distance[start.Id] = 0;
            var frontier = new List<int> { start.Id };
            var next = new List<int>();

            while (frontier.Count > 0)
            {
                next.Clear();
                foreach (var id in frontier)
                {
                    if (req.Direction is "out" or "both")
                        foreach (var e in g.Out(id))
                            Visit(id, e, e.To);
                    if (req.Direction is "in" or "both")
                        foreach (var e in g.In(id))
                            Visit(id, e, e.From);
                }

                (frontier, next) = (next, frontier);
            }

            void Visit(int current, Edge e, int neighbourId)
            {
                if (!includedIds.Contains(neighbourId)) return;
                if (e.Kind == EdgeKind.TypeUse && !req.IncludeTypeUse) return;

                var key = (e.From, e.To, e.Kind);
                traversed[key] = traversed.TryGetValue(key, out var existing)
                    ? (Or(existing.Role, e.Role), Math.Min(existing.Score, e.Score))
                    : (e.Role, e.Score);

                if (distance.TryAdd(neighbourId, distance[current] + 1)) next.Add(neighbourId);
            }
        }

        var within = distance.Where(x => x.Value <= req.Depth).Select(x => x.Key).ToHashSet();
        var beyond = distance.Where(x => x.Value > req.Depth).Select(x => x.Key).ToList();

        var nodes = within
            .Select(id => g.ById(id)!)
            .Select(n => LabelFor(g, n))
            .OrderBy(n => n.Identity, StringComparer.Ordinal)
            .ToList();

        var drawn = new List<DrawnEdge>();
        var beyondEdges = 0;
        foreach (var (key, value) in traversed)
        {
            var from = g.ById(key.From);
            var to = g.ById(key.To);
            if (from == null || to == null) continue;

            if (within.Contains(key.From) && within.Contains(key.To))
            {
                drawn.Add(new DrawnEdge(from.Key, to.Key, key.Kind, value.Role, value.Score));
            }
            else
            {
                beyondEdges++;
            }
        }

        drawn = drawn
            .OrderBy(x => x.FromIdentity, StringComparer.Ordinal)
            .ThenBy(x => x.ToIdentity, StringComparer.Ordinal)
            .ThenBy(x => x.Kind)
            .ToList();

        return new LevelResult(nodes, drawn, new Withheld(testNodes, testEdges, typeUseEdges), beyond.Count, beyondEdges);
    }

    /// <summary>
    /// A symbol's label: its short name, except where the name is overloaded, in which case the
    /// selector the exit-3 answer prints so a diagram and a query speak the same name.
    /// </summary>
    private static Bucket LabelFor(Graph g, Node n)
    {
        var overloaded = g.Nodes.Count(other => other.Name == n.Name) > 1;
        return new Bucket(n.Key, overloaded ? SymbolSelector.SelectorFor(n) : n.Short);
    }

    private static string ProjectOf(Node n) => n.Project;

    /// <summary>
    /// The label for the bucket that holds nodes with no project or no namespace. It is a named
    /// bucket, not an empty string: Mermaid cannot parse a node whose label is <c>""</c>, and an
    /// empty label says nothing about what the bucket means.
    /// </summary>
    private static string EmptyLabel(string level) => level == "project" ? "(no project)" : "(global)";

    /// <summary>
    /// The namespace bucket a node belongs to, derived from names alone rather than from a graph
    /// edge.
    ///
    /// <see cref="Node.Name"/> is fully qualified, so a type's bucket is everything before its last
    /// segment and a member's bucket is everything before its declaring type. A nested type keeps
    /// its containing type as part of the bucket (<c>Ns.Outer.Inner</c> -&gt; <c>Ns.Outer</c>), the
    /// same collapse the ADR measured: one node per namespace and one per nested-type container.
    ///
    /// The previous implementation walked the TypeUse ownership edge instead. That edge exists only
    /// for a declared member of a named type, so interface members and synthetic nodes had none and
    /// were dropped or bucketed globally, and the namespace set came out smaller than the ADR's.
    /// </summary>
    internal static Func<Node, string?> NamespaceResolver(Graph g)
    {
        var typeNames = g.Nodes
            .Where(n => n.Kind is "type" or "interface" or "enum" or "struct" or "delegate")
            .Select(n => n.Name)
            .ToHashSet(StringComparer.Ordinal);

        string? DeclaringTypeOf(string name)
        {
            string? declaring = null;
            foreach (var candidate in typeNames)
            {
                if (candidate.Length < name.Length
                    && name.StartsWith(candidate + ".", StringComparison.Ordinal)
                    && (declaring is null || candidate.Length > declaring.Length))
                {
                    declaring = candidate;
                }
            }

            return declaring;
        }

        return n =>
        {
            // A declared type groups under its own containing segment: a top-level type under its
            // namespace, a nested type under its containing type. A member groups under its
            // declaring type's name. This is the collapse the ADR measured -- one bucket per
            // namespace and one per nested-type container.
            if (n.Kind is "type" or "interface" or "enum" or "struct" or "delegate") return StripLastSegment(n.Name);

            var declaring = DeclaringTypeOf(n.Name);
            if (declaring is not null) return StripLastSegment(declaring);

            // A synthetic node with no declaring type in the graph has no namespace; it belongs to
            // none rather than to a namespace invented from its display name.
            return "";
        };
    }

    private static string StripLastSegment(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[..dot];
    }

    private static EdgeRole? Or(EdgeRole? a, EdgeRole? b) =>
        (a, b) switch
        {
            (null, null) => null,
            (null, { } x) => x,
            ({ } x, null) => x,
            ({ } x, { } y) => x | y
        };

    // ------------------------------------------------------------------ formats

    private static List<string> RenderMermaid(List<Bucket> nodes, Dictionary<string, string> ids, List<DrawnEdge> edges)
    {
        var lines = new List<string> { "flowchart LR" };

        foreach (var node in nodes)
        {
            lines.Add($"  {ids[node.Identity]}[\"{EscapeMermaid(node.Label)}\"]");
        }

        foreach (var edge in edges)
        {
            var arrow = IsDashed(edge) ? "-.->" : "-->";
            var label = EdgeLabel(edge);
            lines.Add($"  {ids[edge.FromIdentity]} {arrow}{label} {ids[edge.ToIdentity]}");
        }

        return lines;
    }

    private static List<string> RenderDot(List<Bucket> nodes, Dictionary<string, string> ids, List<DrawnEdge> edges)
    {
        var lines = new List<string> { "digraph G {" };

        foreach (var node in nodes)
        {
            lines.Add($"  \"{EscapeDot(ids[node.Identity])}\" [label=\"{EscapeDot(node.Label)}\"];");
        }

        foreach (var edge in edges)
        {
            var attributes = new List<string>();
            if (IsDashed(edge)) attributes.Add("style=dashed");
            var label = RawEdgeLabel(edge);
            if (label.Length > 0) attributes.Add($"label=\"{label}\"");
            var suffix = attributes.Count == 0 ? "" : " [" + string.Join(", ", attributes) + "]";
            lines.Add($"  \"{EscapeDot(ids[edge.FromIdentity])}\" -> \"{EscapeDot(ids[edge.ToIdentity])}\"{suffix};");
        }

        lines.Add("}");
        return lines;
    }

    private static bool IsDashed(DrawnEdge edge) =>
        (edge.Role is { } role && (role & EdgeRole.Write) != 0) || edge.Score < Edge.TrustThreshold;

    /// <summary>
    /// A Call edge is the ordinary arrow and stays unlabelled; every other kind carries its short
    /// name so parallel edges of different kinds are distinguishable (ADR 0004). A low-confidence
    /// edge appends its score.
    /// </summary>
    private static string RawEdgeLabel(DrawnEdge edge)
    {
        var parts = new List<string>();
        var kind = KindLabel(edge.Kind);
        if (kind.Length > 0) parts.Add(kind);
        if (edge.Score < Edge.TrustThreshold) parts.Add($"?{edge.Score:0.00}");
        return string.Join(" ", parts);
    }

    private static string EdgeLabel(DrawnEdge edge)
    {
        var raw = RawEdgeLabel(edge);
        return raw.Length == 0 ? "" : $"|{raw}|";
    }

    private static string KindLabel(EdgeKind kind) => kind switch
    {
        EdgeKind.Interface => "iface",
        EdgeKind.Override => "override",
        EdgeKind.Mediatr => "mediatr",
        EdgeKind.DiBinding => "di",
        EdgeKind.Construct => "construct",
        EdgeKind.TypeUse => "typeuse",
        EdgeKind.Route => "route",
        _ => ""
    };

    /// <summary>Mermaid label text: quoted by the caller, so only a quote needs its entity.</summary>
    internal static string EscapeMermaid(string text) => text.Replace("\"", "#quot;", StringComparison.Ordinal);

    /// <summary>DOT ids and labels are quoted, so a quote and a backslash have to be escaped.</summary>
    internal static string EscapeDot(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    // ------------------------------------------------------------------ ids

    /// <summary>
    /// Assigns a diagram id to every identity: the level prefix plus a hex prefix of SHA-256 of the
    /// identity text (ADR 0004). A hash of the identity, not a rank in a sorted list, so adding one
    /// symbol does not renumber every diagram line after it.
    ///
    /// On a collision within one render the colliding ids extend to 12, then 16, ... hex digits,
    /// while an id that collides with nothing keeps its 8-digit form. Hashes are deterministic, so
    /// the result is too.
    /// </summary>
    internal static Dictionary<string, string> AssignIds(
        IReadOnlyList<string> identities, Func<string, string> hash, string prefix)
    {
        var full = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var identity in identities) full[identity] = hash(identity);

        var length = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var identity in identities) length[identity] = Math.Min(8, full[identity].Length);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var group in identities.GroupBy(id => full[id][..length[id]], StringComparer.Ordinal))
            {
                var members = group.ToList();
                if (members.Count == 1) continue;

                foreach (var id in members)
                {
                    if (length[id] >= full[id].Length) continue;
                    length[id] = Math.Min(length[id] + 4, full[id].Length);
                    changed = true;
                }
            }
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var identity in identities) result[identity] = prefix + full[identity][..length[identity]];
        return result;
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
