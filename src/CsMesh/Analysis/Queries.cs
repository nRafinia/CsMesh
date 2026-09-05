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
        Project = n.Project.Length > 0 ? n.Project : null,
        Tags = n.Tags.Count > 0 ? n.Tags : null
    };

    private static QueryRow Row(Node n, int depth, Edge e, HashSet<string> dirty)
    {
        var row = Row(n, depth, Relation(e.Kind), e.Note, dirty);
        row.Confidence = e.Confidence;
        row.Source = e.Source;
        row.Site = e.Site;
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
    public static int Trace(Graph g, Node start, int depth, BudgetWriter w, HashSet<string> dirty,
                            string? rerun = null)
    {
        // A node reached first at depth 5 must still be expanded when a shorter path reaches it,
        // otherwise whole branches of the tree silently disappear. onPath guards against cycles.
        var expandedAt = new Dictionary<int, int>();
        var onPath = new HashSet<int>();
        var truncatedAt = new List<string>();
        var emitted = 0;

        // Cost per level, so an overflow can name a depth that fits instead of telling the caller
        // to guess. An agent that has to try --depth 3, then --depth 2, has spent the round trips
        // the budget was meant to save.
        var costByLevel = new Dictionary<int, int>();

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
                    // A DiBinding is the container's own answer to "what runs". Matching only on
                    // the marker missed it, because a DiBinding edge notes its lifetime instead.
                    .OrderByDescending(e => e.Kind == EdgeKind.DiBinding || e.Note == "di-bound")
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

                    costByLevel[level + 1] = costByLevel.GetValueOrDefault(level + 1) + BudgetWriter.Estimate(line);
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

            var fits = DepthThatFits(costByLevel, w.Budget);
            if (fits > 0 && rerun != null)
            {
                w.Force($"depth {fits} fits. Re-run: {rerun} --depth {fits}");
            }
            else if (fits > 0)
            {
                w.Force($"depth {fits} fits within this budget.");
            }
            else
            {
                w.Force("Even depth 1 does not fit. Raise --budget, or trace a narrower symbol.");
            }

            return Exit.OverBudget;
        }

        if (emitted == 0) w.Force("  (no outgoing calls resolved in source)");

        return Exit.Ok;
    }

    /// <summary>
    /// The deepest level whose cumulative cost still fits the budget, leaving room for the header
    /// and the closing lines. Zero when nothing fits.
    /// </summary>
    private static int DepthThatFits(Dictionary<int, int> costByLevel, int budget)
    {
        var allowance = (int)(budget * 0.85);
        var running = 0;
        var best = 0;

        foreach (var level in costByLevel.Keys.Order())
        {
            running += costByLevel[level];
            if (running > allowance) break;
            best = level;
        }

        return best;
    }

    /// <summary>
    /// Finds all implementations of an interface or base type, ranking DI-bound registrations first.
    /// </summary>
    public static int Impl(Graph g, Node target, BudgetWriter w, HashSet<string> dirty)
    {
        // One implementation can arrive on two edges: the DiBinding written where it was
        // registered, and the Interface edge written where it was declared. Deduplicating on
        // arrival order kept whichever came first, which was the DiBinding -- and the DiBinding
        // carries the lifetime in its note, not the "di-bound" marker the sort below looks for.
        // The edge holding the ranking signal was the one being thrown away, so a registered class
        // only led the list when its score happened to tie with everything else and insertion order
        // broke the tie in its favour. Anything registered at less than full confidence -- a name
        // match, an assembly scan, a factory -- sorted below classes nobody registered at all.
        var impls = g.Out(target.Id)
            .Where(e => e.Kind is EdgeKind.Interface or EdgeKind.Override or EdgeKind.DiBinding)
            .GroupBy(e => e.To)
            .Select(x => x.OrderByDescending(e => e.Kind == EdgeKind.DiBinding).First())
            .ToList();

        if (impls.Count == 0)
        {
            w.Force($"{target.Short}: no implementations found in source.");
            return Exit.NotFound;
        }

        w.Force($"{target.Short}{Loc(target)}  -- {impls.Count} implementation(s)",
                Row(target, 0, "root", null, dirty));

        foreach (var e in impls
                     .OrderByDescending(x => x.Kind == EdgeKind.DiBinding || x.Note == "di-bound")
                     .ThenBy(x => g.ById(x.To) is { } n && IsTest(n) ? 1 : 0)
                     .ThenByDescending(x => x.Score))
        {
            var to = g.ById(e.To);
            if (to == null) continue;

            var di = to.Tags.FirstOrDefault(t => t.StartsWith("di:"));
            var keyed = to.Tags.FirstOrDefault(t => t.StartsWith("keyed:"));
            var bound = e.Note == "di-bound" || di != null;

            var parts = new List<string>();
            if (bound) parts.Add(di ?? "di-bound");
            if (keyed != null) parts.Add(keyed);
            if (IsTest(to)) parts.Add("test");
            if (e.Score < 1.0) parts.Add($"?{e.Score:0.00}{(e.Source != null ? " " + e.Source : "")}");
            var mark = parts.Count == 0 ? "" : "  [" + string.Join(", ", parts) + "]";

            var row = Row(to, 1, e, dirty);
            row.Relation = e.Kind == EdgeKind.Override ? "override" : "impl";
            row.Note = e.Note ?? di;

            // Two locations, and both are wanted: where the class lives, and where it was wired.
            // Without the second, "now change the binding" starts with a grep.
            var wiring = WiringSite(g, target.Id, to.Id);
            row.Site = wiring;

            // Registering a type in the file that declares it is common; printing the path twice
            // on one line is noise, so only the line number survives.
            var site = wiring == null ? ""
                : wiring.StartsWith(to.File + ":", StringComparison.OrdinalIgnoreCase)
                    ? $"  @ line {wiring[(to.File.Length + 1)..]}"
                    : $"  @ {wiring}";

            if (!w.Add($"  {to.Short}{mark}{Loc(to)}{site}{StaleTag(to, dirty)}", row))
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
        // Confidence is carried along the path, not read off the last edge. A caller reached only
        // through a 0.70 dispatch is a 0.70 caller no matter how certain the remaining hops were,
        // and blast-radius is the worst place to overstate certainty: the person running it is
        // deciding what not to break.
        var seen = new HashSet<int>();
        var frontier = new List<Reach> { new(target, 0, 1.0, null) };
        var reached = new List<Reach>();
        var entrypoints = new List<Reach>();

        if (target.Kind is "type" or "interface" or "enum")
        {
            foreach (var e in g.Out(target.Id).Where(x => x.Kind == EdgeKind.TypeUse))
            {
                var m = g.ById(e.To);
                if (m != null && seen.Add(m.Id)) frontier.Add(new Reach(m, 0, 1.0, null));
            }
        }

        while (frontier.Count > 0)
        {
            var next = new List<Reach>();
            foreach (var reach in frontier)
            {
                if (reach.Level >= depth) continue;
                foreach (var e in g.In(reach.Node.Id))
                {
                    if (e.Kind == EdgeKind.TypeUse) continue;
                    var from = g.ById(e.From);
                    if (from == null || !seen.Add(from.Id)) continue;

                    var score = Math.Min(reach.Score, e.Score);
                    var source = e.Score < reach.Score ? e.Source : reach.Source;
                    var hop = new Reach(from, reach.Level + 1, score, source);

                    reached.Add(hop);
                    if (IsEntrypoint(from)) entrypoints.Add(hop);
                    next.Add(hop);
                }
            }
            frontier = next;
        }

        var tests = reached.Where(r => IsTest(r.Node)).ToList();
        var weakest = reached.Select(r => r.Score).DefaultIfEmpty(1.0).Min();

        w.Force($"{target.Short}{Loc(target)}{StaleTag(target, dirty)}", Row(target, 0, "root", null, dirty));
        w.Force($"reached by {reached.Count} member(s), {entrypoints.Count} entrypoint(s), {tests.Count} test(s)");

        // How far the change spreads across assemblies says more about its size than forty type
        // names do, and costs one line.
        var projects = reached
            .Where(r => r.Node.Project.Length > 0)
            .GroupBy(r => r.Node.Project, StringComparer.Ordinal)
            .OrderByDescending(x => x.Count())
            .ToList();

        if (projects.Count > 1)
        {
            w.Force("across " + projects.Count + " project(s): " +
                    string.Join("  ", projects.Take(6).Select(p => $"{p.Key} ({p.Count()})")));
        }

        if (entrypoints.Count > 0)
        {
            w.Force("entrypoints:");
            foreach (var ep in entrypoints.Take(30))
            {
                if (!Write(ep, "entrypoint", TagSuffix(ep.Node))) return Overflow(w, reached.Count);
            }
        }

        // Production callers first. A test calling a member is a real caller, but it is not the
        // thing that breaks in production, and mixing the two buries the answer.
        var direct = reached.Where(r => r.Level == 1).ToList();
        var directProduction = direct.Where(r => !IsTest(r.Node)).ToList();
        var directTests = direct.Where(r => IsTest(r.Node)).ToList();

        if (directProduction.Count > 0)
        {
            w.Force("direct callers:");
            foreach (var d in directProduction)
            {
                if (!Write(d, "direct-caller", "")) return Overflow(w, reached.Count);
            }
        }

        if (directTests.Count > 0)
        {
            w.Force("direct callers (tests):");
            foreach (var d in directTests)
            {
                if (!Write(d, "direct-caller-test", "")) return Overflow(w, reached.Count);
            }
        }

        // Group by the owning type id, not by a string: two types with the same simple name in
        // different namespaces are two types.
        var indirect = reached.Where(r => r.Level > 1)
            .Select(r => r.Node.Short.Contains('.') ? r.Node.Short[..r.Node.Short.LastIndexOf('.')] : r.Node.Short)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (indirect.Count > 0)
        {
            var line = "indirect (types): " + string.Join(", ", indirect.Take(25));
            if (!w.Add(line)) return Overflow(w, reached.Count);
        }

        if (weakest < Edge.TrustThreshold)
        {
            w.Force($"weakest path into this set: {weakest:0.00}. Rows marked ?score are inferred, not read off a symbol.");
        }

        return Exit.Ok;

        bool Write(Reach r, string relation, string tags)
        {
            var mark = r.Score < 1.0
                ? $"  [?{r.Score:0.00}{(r.Source != null ? " " + r.Source : "")}]"
                : "";

            var row = Row(r.Node, r.Level, relation, null, dirty);
            if (r.Score < 1.0)
            {
                row.Confidence = r.Score;
                row.Source = r.Source;
            }

            return w.Add($"  {r.Node.Short}{mark}{tags}{Loc(r.Node)}{StaleTag(r.Node, dirty)}", row);
        }
    }

    /// <summary>One node reached during a reverse walk, with the weakest edge on the way to it.</summary>
    private readonly record struct Reach(Node Node, int Level, double Score, string? Source);

    internal static bool IsTest(Node n) => n.Tags.Contains("test");

    /// <summary>Where the container was told about this pair, if anywhere.</summary>
    private static string? WiringSite(Graph g, int serviceId, int implementationId) =>
        g.Out(serviceId)
            .FirstOrDefault(e => e.Kind == EdgeKind.DiBinding && e.To == implementationId && e.Site != null)?
            .Site;

    private static int Overflow(BudgetWriter w, int total)
    {
        w.Force("");
        w.Force($"OVER BUDGET: {total} reachable members. Narrow it: --depth 1, or raise --budget.");
        return Exit.OverBudget;
    }

    private static bool IsEntrypoint(Node n) =>
        n.Tags.Any(t => t.StartsWith("http:") || t is "handler" or "consumer" or "hosted" or "action");

    public static int Entrypoints(Graph g, string? filter, string? under, BudgetWriter w, HashSet<string> dirty)
    {
        var eps = g.Nodes.Where(IsEntrypoint)
            .Where(n => Under(n, under))
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