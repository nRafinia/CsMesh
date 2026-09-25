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

    public static ExportResult RenderExport(Graph g, ExportRequest req)
    {
        if (req.Format == "dot") return RenderDotLevel(g, req);

        // Mermaid is the default; the caller has already validated the value.
        var (nodes, edges, counts) = req.Level switch
        {
            "project" => Collapse(g, req),
            _ => throw new ArgumentOutOfRangeException(nameof(req), req.Level)
        };

        var ids = AssignIds(nodes.Select(n => n.Identity).ToList(), Sha256Hex, "p");

        var lines = new List<string> { "flowchart LR" };
        foreach (var node in nodes)
        {
            lines.Add($"  {ids[node.Identity]}[\"{EscapeMermaid(node.Label)}\"]");
        }

        foreach (var edge in edges)
        {
            lines.Add(MermaidEdge(ids[edge.FromIdentity], ids[edge.ToIdentity], edge));
        }

        return new ExportResult(lines, nodes.Count, edges.Count,
            counts.TestNodes, counts.TestEdges, counts.TypeUseEdges, 0, 0);
    }

    private static (List<Bucket> Nodes, List<DrawnEdge> Edges, (int TestNodes, int TestEdges, int TypeUseEdges)) Collapse(
        Graph g, ExportRequest req)
    {
        var included = new List<Node>();
        var testNodes = 0;

        foreach (var n in g.Nodes)
        {
            if (!req.IncludeTests && IsTest(n)) { testNodes++; continue; }
            included.Add(n);
        }

        var includedIds = included.Select(n => n.Id).ToHashSet();

        var testEdges = 0;
        var typeUseEdges = 0;
        var collapsed = new Dictionary<(string From, string To, EdgeKind Kind), (EdgeRole? Role, double Score)>();

        foreach (var e in g.Edges)
        {
            if (!includedIds.Contains(e.From) || !includedIds.Contains(e.To)) { testEdges++; continue; }
            if (e.Kind == EdgeKind.TypeUse && !req.IncludeTypeUse) { typeUseEdges++; continue; }

            var from = g.ById(e.From)!;
            var to = g.ById(e.To)!;
            var key = (ProjectOf(from), ProjectOf(to), e.Kind);

            if (collapsed.TryGetValue(key, out var existing))
            {
                collapsed[key] = (Or(existing.Role, e.Role), Math.Min(existing.Score, e.Score));
            }
            else
            {
                collapsed[key] = (e.Role, e.Score);
            }
        }

        var labels = included
            .GroupBy(ProjectOf, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Key, StringComparer.Ordinal);

        var nodes = labels.Keys
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(x => new Bucket(x, labels[x]))
            .ToList();

        var edges = collapsed
            .OrderBy(x => x.Key.From, StringComparer.Ordinal)
            .ThenBy(x => x.Key.To, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Kind)
            .Select(x => new DrawnEdge(x.Key.From, x.Key.To, x.Key.Kind, x.Value.Role, x.Value.Score))
            .ToList();

        return (nodes, edges, (testNodes, testEdges, typeUseEdges));
    }

    private static ExportResult RenderDotLevel(Graph g, ExportRequest req) =>
        throw new ArgumentOutOfRangeException(nameof(req), req.Level);

    private static string ProjectOf(Node n) => n.Project;

    private static EdgeRole? Or(EdgeRole? a, EdgeRole? b) =>
        (a, b) switch
        {
            (null, null) => null,
            (null, { } x) => x,
            ({ } x, null) => x,
            ({ } x, { } y) => x | y
        };

    private static string MermaidEdge(string fromId, string toId, DrawnEdge edge)
    {
        var dashed = (edge.Role is { } role && (role & EdgeRole.Write) != 0) || edge.Score < Edge.TrustThreshold;
        var arrow = dashed ? "-.->" : "-->";
        var label = edge.Score < Edge.TrustThreshold ? $"|?{edge.Score:0.00}|" : "";
        return $"  {fromId} {arrow}{label} {toId}";
    }

    /// <summary>Mermaid label text: quoted by the caller, so only a quote needs its entity.</summary>
    internal static string EscapeMermaid(string text) => text.Replace("\"", "#quot;", StringComparison.Ordinal);

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
