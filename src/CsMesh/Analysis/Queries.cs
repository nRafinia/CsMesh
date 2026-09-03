using CsMesh.Common;
using CsMesh.Models;
using EdgeKind = CsMesh.Models.EdgeKind;
using Graph = CsMesh.Models.Graph;

namespace CsMesh.Analysis;

/// <summary>
/// Graph query algorithms for execution tracing, implementation lookups, blast radius analysis, and entrypoint discovery.
/// </summary>
public static class Queries
{
    private static string Marker(EdgeKind k, string? note) => k switch
    {
        EdgeKind.Mediatr => note != null ? $"  [mediatr {note}]" : "  [mediatr]",
        EdgeKind.Interface => note == "di-bound" ? "  [impl, di-bound]" : "  [impl]",
        EdgeKind.Construct => "  [new]",
        EdgeKind.DiBinding => $"  [di:{note}]",
        EdgeKind.Override => "  [override]",
        _ => ""
    };

    private static string Loc(Models.Node n) => n.File.Length > 0 ? $"  {n.File}:{n.Line}" : "";

    private static string TagSuffix(Models.Node n)
    {
        var interesting = n.Tags.Where(t =>
            t.StartsWith("http:") || t.StartsWith("route:") || t == "handler" ||
            t == "dbcontext" || t == "consumer" || t == "obsolete").ToList();
        return interesting.Count == 0 ? "" : "  {" + string.Join(" ", interesting) + "}";
    }

    /// <summary>
    /// Performs a forward call-graph walk tracing outbound invocations and indirection.
    /// </summary>
    public static int Trace(Graph g, Models.Node start, int depth, BudgetWriter w, HashSet<string> dirty)
    {
        var seen = new HashSet<int>();
        var truncatedAt = new List<string>();

        w.Force($"{start.Short}{TagSuffix(start)}{Loc(start)}{StaleTag(start, dirty)}");

        bool Walk(Models.Node node, int level, string prefix)
        {
            if (level >= depth) return true;
            if (!seen.Add(node.Id)) return true;

            var edges = g.Out(node.Id)
                .Where(e => e.Kind is EdgeKind.Call or EdgeKind.Mediatr or EdgeKind.Interface or EdgeKind.Construct)
                .Where(e => e.Note != "prop")
                .ToList();

            edges = edges.OrderByDescending(e => e.Note == "di-bound")
                         .ThenBy(e => e.Kind == EdgeKind.Construct ? 1 : 0)
                         .ToList();

            foreach (var e in edges)
            {
                var to = g.ById(e.To);
                if (to == null) continue;
                if (to.Kind == "type" && e.Kind == EdgeKind.Construct && level > 1) continue;

                var line = $"{prefix}-> {to.Short}{Marker(e.Kind, e.Note)}{TagSuffix(to)}{Loc(to)}{StaleTag(to, dirty)}";
                if (!w.Add(line))
                {
                    truncatedAt.Add(node.Short);
                    return false;
                }

                if (!Walk(to, level + 1, prefix + "  ")) return false;
            }
            return true;
        }

        var complete = Walk(start, 0, "  ");

        if (!complete)
        {
            w.Force("");
            w.Force($"OVER BUDGET at {string.Join(", ", truncatedAt.Distinct().Take(3))}.");
            w.Force("Narrow it: --depth 3, or trace one of the callees above directly.");
            return Exit.OverBudget;
        }

        if (w.Lines.Count == 1)
        {
            w.Force("  (no outgoing calls resolved in source)");
        }

        return Exit.Ok;
    }

    /// <summary>
    /// Finds all implementations of an interface or base type, ranking DI-bound registrations first.
    /// </summary>
    public static int Impl(Graph g, Models.Node iface, BudgetWriter w, HashSet<string> dirty)
    {
        var impls = g.Out(iface.Id).Where(e => e.Kind is EdgeKind.Interface or EdgeKind.DiBinding).ToList();
        if (impls.Count == 0)
        {
            w.Force($"{iface.Short}: no implementations found in source.");
            return Exit.NotFound;
        }

        w.Force($"{iface.Short}{Loc(iface)}  -- {impls.Select(e => e.To).Distinct().Count()} implementation(s)");
        foreach (var e in impls.OrderByDescending(x => x.Note == "di-bound").DistinctBy(x => x.To))
        {
            var to = g.ById(e.To);
            if (to == null) continue;
            var di = to.Tags.FirstOrDefault(t => t.StartsWith("di:"));
            var mark = e.Note == "di-bound" || di != null ? $"  [{di ?? "di-bound"}]" : "";
            if (!w.Add($"  {to.Short}{mark}{Loc(to)}{StaleTag(to, dirty)}")) return Exit.OverBudget;
        }

        return Exit.Ok;
    }

    /// <summary>
    /// Performs a reverse traversal to discover all direct and indirect callers affected by a member change,
    /// surfacing reachable entrypoints (routes, message handlers, consumers, background services).
    /// </summary>
    public static int BlastRadius(Graph g, Models.Node target, int depth, BudgetWriter w, HashSet<string> dirty)
    {
        var seen = new HashSet<int>();
        var frontier = new List<(Models.Node Node, int Level)> { (target, 0) };
        var reached = new List<(Models.Node Node, int Level)>();
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

        w.Force($"{target.Short}{Loc(target)}{StaleTag(target, dirty)}");
        w.Force($"reached by {reached.Count} member(s), {entrypoints.Count} entrypoint(s)");

        if (entrypoints.Count > 0)
        {
            w.Force("entrypoints:");
            foreach (var ep in entrypoints.Take(30))
                if (!w.Add($"  {ep.Short}{TagSuffix(ep)}{Loc(ep)}")) return Overflow(w, reached.Count);
        }

        var direct = reached.Where(r => r.Level == 1).Select(r => r.Node).ToList();
        if (direct.Count > 0)
        {
            w.Force("direct callers:");
            foreach (var d in direct)
                if (!w.Add($"  {d.Short}{Loc(d)}{StaleTag(d, dirty)}")) return Overflow(w, reached.Count);
        }

        var indirect = reached.Where(r => r.Level > 1).Select(r => r.Node.Short.Split('.')[0]).Distinct().ToList();
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
        n.Tags.Any(t => t.StartsWith("http:") || t == "handler" || t == "consumer" || t == "hosted" || t == "action");

    public static int Entrypoints(Graph g, string? filter, BudgetWriter w)
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
            if (!w.Add($"  {ep.Short}{TagSuffix(ep)}{Loc(ep)}"))
            {
                w.Force($"OVER BUDGET: {eps.Count} total. Filter with a substring argument.");
                return Exit.OverBudget;
            }
        }

        return Exit.Ok;
    }

    private static string StaleTag(Node n, HashSet<string> dirty) =>
        n.File.Length > 0 && dirty.Contains(n.File) ? "  [STALE]" : "";
}
