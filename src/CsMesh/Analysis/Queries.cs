using CsMesh.Common;
using CsMesh.Models;

namespace CsMesh.Analysis;

/// <summary>
/// Graph query algorithms for execution tracing, implementation lookups, blast radius analysis, and entrypoint discovery.
/// </summary>
public static partial class Queries
{
    /// <summary>
    /// Appends the confidence of an edge when it is not certain, so an agent can tell a compiler
    /// fact from a name match without asking for JSON.
    /// </summary>
    private static string Marker(Edge e)
    {
        var text = Marker(e.Kind, e.Note);
        if (e.Score >= 1.0) return text;

        var suffix = $"?{e.Score:0.00}{(e.Source != null ? " " + e.Source : "")}";
        return text.Length == 0 ? $"  [{suffix}]" : text[..^1] + " " + suffix + "]";
    }

    private static string Marker(EdgeKind k, string? note) => k switch
    {
        EdgeKind.Mediatr => note != null ? $"  [mediatr {note}]" : "  [mediatr]",
        EdgeKind.Interface => note == "di-bound" ? "  [impl, di-bound]" : "  [impl]",
        EdgeKind.Override => note == "di-bound" ? "  [override, di-bound]" : "  [override]",
        EdgeKind.Route => note != null ? $"  [route {note}]" : "  [route]",
        EdgeKind.Construct => "  [new]",
        EdgeKind.DiBinding => $"  [di:{note}]",
        _ => ""
    };

    private static string Relation(EdgeKind k) => k switch
    {
        EdgeKind.Mediatr => "mediatr",
        EdgeKind.Interface => "impl",
        EdgeKind.Override => "override",
        EdgeKind.Route => "route",
        EdgeKind.Construct => "new",
        EdgeKind.DiBinding => "di",
        _ => "call"
    };

    private static string Loc(Node n) => n.File.Length > 0 ? $"  {n.File}:{n.Line}" : "";

    private static string TagSuffix(Node n)
    {
        var interesting = n.Tags.Where(t =>
            t.StartsWith("http:") || t.StartsWith("route:") || t == "handler" ||
            t == "dbcontext" || t == "consumer" || t == "obsolete").ToList();
        return interesting.Count == 0 ? "" : "  {" + string.Join(" ", interesting) + "}";
    }

    private static bool IsStale(Node n, HashSet<string> dirty) => n.File.Length > 0 && dirty.Contains(n.File);

    private static string StaleTag(Node n, HashSet<string> dirty) => IsStale(n, dirty) ? "  [STALE]" : "";

    private static QueryRow Row(Node n, int depth, string relation, string? note, HashSet<string> dirty) => new()
    {
        Depth = depth,
        Symbol = n.Short,
        Kind = n.Kind,
        Relation = relation,
        Note = note,
        File = n.File.Length > 0 ? n.File : null,
        Line = n.Line,
        Stale = IsStale(n, dirty),
        Tags = n.Tags.Count > 0 ? n.Tags : null
    };

    private static QueryRow Row(Node n, int depth, Edge e, HashSet<string> dirty)
    {
        var row = Row(n, depth, Relation(e.Kind), e.Note, dirty);
        row.Confidence = e.Confidence;
        row.Source = e.Source;
        return row;
    }

    /// <summary>Edges that represent "what runs next" rather than structure or ownership.</summary>
    private static bool IsFlow(Edge e) =>
        e.Kind is EdgeKind.Call or EdgeKind.Mediatr or EdgeKind.Interface
                or EdgeKind.Override or EdgeKind.Construct or EdgeKind.Route
        && e.Note != "prop";

    /// <summary>
    /// Performs a forward call-graph walk tracing outbound invocations and indirection.
    /// </summary>
    public static int Trace(Graph g, Node start, int depth, BudgetWriter w, HashSet<string> dirty)
    {
        // A node reached first at depth 5 must still be expanded when a shorter path reaches it,
        // otherwise whole branches of the tree silently disappear. onPath guards against cycles.
        var expandedAt = new Dictionary<int, int>();
        var onPath = new HashSet<int>();
        var truncatedAt = new List<string>();
        var emitted = 0;

        w.Force($"{start.Short}{TagSuffix(start)}{Loc(start)}{StaleTag(start, dirty)}",
                Row(start, 0, "root", null, dirty));

        bool Walk(Node node, int level, string prefix)
        {
            if (level >= depth) return true;
            if (expandedAt.TryGetValue(node.Id, out var previous) && previous <= level) return true;
            if (!onPath.Add(node.Id)) return true;
            expandedAt[node.Id] = level;

            try
            {
                var edges = g.Out(node.Id)
                    .Where(IsFlow)
                    .OrderByDescending(e => e.Note == "di-bound")
                    .ThenByDescending(e => e.Score)
                    .ThenBy(e => e.Kind == EdgeKind.Construct ? 1 : 0)
                    .ToList();

                foreach (var e in edges)
                {
                    var to = g.ById(e.To);
                    if (to == null) continue;
                    if (to.Kind == "type" && e.Kind == EdgeKind.Construct && level > 1) continue;

                    var line = $"{prefix}-> {to.Short}{Marker(e)}{TagSuffix(to)}{Loc(to)}{StaleTag(to, dirty)}";
                    if (!w.Add(line, Row(to, level + 1, e, dirty)))
                    {
                        truncatedAt.Add(node.Short);
                        return false;
                    }

                    emitted++;
                    if (!Walk(to, level + 1, prefix + "  ")) return false;
                }

                return true;
            }
            finally
            {
                onPath.Remove(node.Id);
            }
        }

        var complete = Walk(start, 0, "  ");

        if (!complete)
        {
            w.Force("");
            w.Force($"OVER BUDGET at {string.Join(", ", truncatedAt.Distinct().Take(3))}.");
            w.Force("Narrow it: --depth 3, or trace one of the callees above directly.");
            return Exit.OverBudget;
        }

        if (emitted == 0) w.Force("  (no outgoing calls resolved in source)");

        return Exit.Ok;
    }

    /// <summary>
    /// Finds all implementations of an interface or base type, ranking DI-bound registrations first.
    /// </summary>
    public static int Impl(Graph g, Node target, BudgetWriter w, HashSet<string> dirty)
    {
        var impls = g.Out(target.Id)
            .Where(e => e.Kind is EdgeKind.Interface or EdgeKind.Override or EdgeKind.DiBinding)
            .DistinctBy(e => e.To)
            .ToList();

        if (impls.Count == 0)
        {
            w.Force($"{target.Short}: no implementations found in source.");
            return Exit.NotFound;
        }

        w.Force($"{target.Short}{Loc(target)}  -- {impls.Count} implementation(s)",
                Row(target, 0, "root", null, dirty));

        foreach (var e in impls.OrderByDescending(x => x.Note == "di-bound").ThenByDescending(x => x.Score))
        {
            var to = g.ById(e.To);
            if (to == null) continue;

            var di = to.Tags.FirstOrDefault(t => t.StartsWith("di:"));
            var keyed = to.Tags.FirstOrDefault(t => t.StartsWith("keyed:"));
            var bound = e.Note == "di-bound" || di != null;

            var parts = new List<string>();
            if (bound) parts.Add(di ?? "di-bound");
            if (keyed != null) parts.Add(keyed);
            if (e.Score < 1.0) parts.Add($"?{e.Score:0.00}{(e.Source != null ? " " + e.Source : "")}");
            var mark = parts.Count == 0 ? "" : "  [" + string.Join(", ", parts) + "]";

            var row = Row(to, 1, e, dirty);
            row.Relation = e.Kind == EdgeKind.Override ? "override" : "impl";
            row.Note = e.Note ?? di;

            if (!w.Add($"  {to.Short}{mark}{Loc(to)}{StaleTag(to, dirty)}", row))
            {
                w.Force("");
                w.Force($"OVER BUDGET: {impls.Count} implementations. Raise --budget or query a narrower type.");
                return Exit.OverBudget;
            }
        }

        return Exit.Ok;
    }

    /// <summary>
    /// Performs a reverse traversal to discover all direct and indirect callers affected by a member change,
    /// surfacing reachable entrypoints (routes, message handlers, consumers, background services).
    /// </summary>
    public static int BlastRadius(Graph g, Node target, int depth, BudgetWriter w, HashSet<string> dirty)
    {
        var seen = new HashSet<int>();
        var frontier = new List<(Node Node, int Level)> { (target, 0) };
        var reached = new List<(Node Node, int Level)>();
        var entrypoints = new List<Node>();

        if (target.Kind is "type" or "interface")
        {
            foreach (var e in g.Out(target.Id).Where(x => x.Kind == EdgeKind.TypeUse))
            {
                var m = g.ById(e.To);
                if (m != null && seen.Add(m.Id)) frontier.Add((m, 0));
            }
        }

        while (frontier.Count > 0)
        {
            var next = new List<(Node, int)>();
            foreach (var (node, level) in frontier)
            {
                if (level >= depth) continue;
                foreach (var e in g.In(node.Id))
                {
                    if (e.Kind == EdgeKind.TypeUse) continue;
                    var from = g.ById(e.From);
                    if (from == null || !seen.Add(from.Id)) continue;
                    reached.Add((from, level + 1));
                    if (IsEntrypoint(from)) entrypoints.Add(from);
                    next.Add((from, level + 1));
                }
            }
            frontier = next;
        }

        w.Force($"{target.Short}{Loc(target)}{StaleTag(target, dirty)}", Row(target, 0, "root", null, dirty));
        w.Force($"reached by {reached.Count} member(s), {entrypoints.Count} entrypoint(s)");

        if (entrypoints.Count > 0)
        {
            w.Force("entrypoints:");
            foreach (var ep in entrypoints.Take(30))
            {
                if (!w.Add($"  {ep.Short}{TagSuffix(ep)}{Loc(ep)}{StaleTag(ep, dirty)}",
                           Row(ep, 1, "entrypoint", null, dirty)))
                {
                    return Overflow(w, reached.Count);
                }
            }
        }

        var direct = reached.Where(r => r.Level == 1).Select(r => r.Node).ToList();
        if (direct.Count > 0)
        {
            w.Force("direct callers:");
            foreach (var d in direct)
            {
                if (!w.Add($"  {d.Short}{Loc(d)}{StaleTag(d, dirty)}", Row(d, 1, "direct-caller", null, dirty)))
                {
                    return Overflow(w, reached.Count);
                }
            }
        }

        var indirect = reached.Where(r => r.Level > 1)
            .Select(r => r.Node.Short.Split('.')[0])
            .Distinct()
            .ToList();

        if (indirect.Count > 0)
        {
            var line = "indirect (types): " + string.Join(", ", indirect.Take(25));
            if (!w.Add(line)) return Overflow(w, reached.Count);
        }

        return Exit.Ok;
    }

    private static int Overflow(BudgetWriter w, int total)
    {
        w.Force("");
        w.Force($"OVER BUDGET: {total} reachable members. Narrow it: --depth 1, or raise --budget.");
        return Exit.OverBudget;
    }

    private static bool IsEntrypoint(Node n) =>
        n.Tags.Any(t => t.StartsWith("http:") || t is "handler" or "consumer" or "hosted" or "action");

    public static int Entrypoints(Graph g, string? filter, BudgetWriter w, HashSet<string> dirty)
    {
        var eps = g.Nodes.Where(IsEntrypoint)
            .Where(n => filter == null || n.Short.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        n.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (eps.Count == 0)
        {
            w.Force("no entrypoints matched.");
            return Exit.NotFound;
        }

        w.Force($"{eps.Count} entrypoint(s)");
        foreach (var ep in eps)
        {
            if (!w.Add($"  {ep.Short}{TagSuffix(ep)}{Loc(ep)}{StaleTag(ep, dirty)}",
                       Row(ep, 0, "entrypoint", null, dirty)))
            {
                w.Force($"OVER BUDGET: {eps.Count} total. Filter with a substring argument.");
                return Exit.OverBudget;
            }
        }

        return Exit.Ok;
    }
}
