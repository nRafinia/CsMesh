using CsMesh.Common;
using CsMesh.Models;

namespace CsMesh.Analysis;

/// <summary>
/// Why a query returned nothing.
///
/// Every other command answers a question. This one answers the failure of a question, which is
/// the harder problem: a structural tool that finds nothing is indistinguishable from a codebase
/// that contains nothing, and an agent reading exit 1 has no way to tell a typo from a boundary
/// from a missing assembly reference. Each of those calls for a different next action, and
/// guessing between them costs the round trips the budget was meant to save.
///
/// Everything reported here is already in the graph -- unresolved sites, external types, scan
/// registrations, edge counts. Nothing is inferred beyond what the indexer recorded.
/// </summary>
public static partial class Queries
{
    public static int Silence(Graph g, Node from, Node? to, int depth, BudgetWriter w, HashSet<string> dirty)
    {
        return to == null
            ? ExplainSymbol(g, from, w, dirty)
            : ExplainPath(g, from, to, depth, w, dirty);
    }

    // ------------------------------------------------------------------ a path that is not there

    private static int ExplainPath(Graph g, Node from, Node to, int depth, BudgetWriter w, HashSet<string> dirty)
    {
        var starts = Expand(g, from);
        var goals = Expand(g, to).Select(n => n.Id).ToHashSet();

        var seen = starts.Select(n => n.Id).ToHashSet();
        var frontier = starts.ToList();
        var deadEnds = new List<Node>();
        var visited = 0;

        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var next = new List<Node>();
            foreach (var node in frontier)
            {
                visited++;
                var outgoing = g.Out(node.Id).Where(IsFlow).ToList();
                var expanded = false;

                foreach (var e in outgoing)
                {
                    if (goals.Contains(e.To))
                    {
                        // A path exists after all; the caller should not be here.
                        w.Force($"a path does exist from {from.Short} to {to.Short}.");
                        w.Force($"Run: csmesh path {from.Short} {to.Short}");
                        return Exit.Ok;
                    }

                    if (!seen.Add(e.To)) continue;
                    if (g.ById(e.To) is not { } target) continue;

                    next.Add(target);
                    expanded = true;
                }

                // A node the walk could not leave. Either it genuinely calls nothing, or the edge
                // it should have had was never built.
                if (!expanded && outgoing.Count == 0) deadEnds.Add(node);
            }

            frontier = next;
        }

        w.Force($"no path: {from.Short} -> {to.Short} within depth {depth}");
        w.Force($"walked {visited} member(s), stopped at {deadEnds.Count} dead end(s)");

        var explained = deadEnds
            .Select(n => (Node: n, Reasons: ReasonsFor(g, n)))
            .OrderByDescending(x => x.Reasons.Count)
            .Take(5)
            .ToList();

        if (explained.Any(x => x.Reasons.Count > 0))
        {
            if (!w.Add("")) return Exit.NotFound;
            if (!w.Add("WHERE IT BROKE")) return Exit.NotFound;

            // The same remedy repeated under five dead ends is one fact, not five. Say it once.
            var said = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (node, reasons) in explained.Where(x => x.Reasons.Count > 0))
            {
                if (!w.Add($"  {node.Short}{Loc(node)}{StaleTag(node, dirty)}",
                           Row(node, 1, "dead-end", null, dirty)))
                {
                    break;
                }

                foreach (var reason in reasons.Take(4))
                {
                    if (reason.StartsWith("  ", StringComparison.Ordinal) && !said.Add(reason)) continue;
                    if (!w.Add($"    {reason}")) break;
                }
            }
        }

        // The question may simply be the wrong way round. Checking costs one traversal and saves
        // the caller from concluding the two symbols are unrelated.
        var backwards = ShortestHops(g, to, from, depth);
        if (backwards > 0)
        {
            w.Force("");
            w.Force($"REVERSE  {to.Short} reaches {from.Short} in {backwards} hop(s).");
            w.Force($"         Run: csmesh path {to.Short} {from.Short}");
        }

        w.Force("");
        w.Force(deadEnds.Count == 0
            ? "The two symbols are in unconnected parts of the graph."
            : "The path may exist at runtime. csmesh could not see past the points above.");

        return Exit.NotFound;
    }

    /// <summary>Hop count of the shortest route, or 0 when there is none.</summary>
    private static int ShortestHops(Graph g, Node from, Node to, int depth)
    {
        var goals = Expand(g, to).Select(n => n.Id).ToHashSet();
        var seen = Expand(g, from).Select(n => n.Id).ToHashSet();
        var frontier = Expand(g, from).Select(n => n.Id).ToList();

        for (var level = 1; level <= depth && frontier.Count > 0; level++)
        {
            var next = new List<int>();
            foreach (var id in frontier)
            {
                foreach (var e in g.Out(id).Where(IsFlow))
                {
                    if (goals.Contains(e.To)) return level;
                    if (seen.Add(e.To)) next.Add(e.To);
                }
            }

            frontier = next;
        }

        return 0;
    }

    // ------------------------------------------------------------------ a symbol with no edges

    private static int ExplainSymbol(Graph g, Node node, BudgetWriter w, HashSet<string> dirty)
    {
        var outgoing = g.Out(node.Id).Where(IsFlow).ToList();
        var incoming = g.In(node.Id).Where(e => e.Kind != EdgeKind.TypeUse).ToList();

        w.Force($"{node.Short}{Loc(node)}{StaleTag(node, dirty)}", Row(node, 0, "root", null, dirty));
        w.Force($"{outgoing.Count} outgoing, {incoming.Count} incoming edge(s)");

        if (outgoing.Count > 0 && incoming.Count > 0)
        {
            w.Force("");
            w.Force("This symbol is connected in both directions; nothing is being hidden from you.");
            w.Force($"Run: csmesh context {node.Short}");
            return Exit.Ok;
        }

        var reasons = ReasonsFor(g, node);

        if (outgoing.Count == 0)
        {
            if (!w.Add("")) return Exit.Ok;
            if (!w.Add("NOTHING LEAVES THIS SYMBOL")) return Exit.Ok;

            if (reasons.Count == 0)
            {
                w.Add(node.Kind is "interface" or "type"
                    ? "  it declares members but the graph has no calls out of them"
                    : "  it calls nothing that is declared in this repository");
            }

            foreach (var reason in reasons.Take(6))
            {
                if (!w.Add("  " + reason)) break;
            }
        }

        if (incoming.Count == 0)
        {
            if (!w.Add("")) return Exit.Ok;
            if (!w.Add("NOTHING REACHES THIS SYMBOL")) return Exit.Ok;

            w.Add("  no call, construction, dispatch or binding in source names it");

            // The name may appear in code that never bound -- a source-generated member, a type
            // from an unresolved assembly. That is an index gap, not an unused symbol, and the
            // two call for opposite conclusions.
            var mentions = Mentions(g, node);
            if (mentions.Count > 0)
            {
                w.Add($"  but {mentions.Count} unresolved site(s) mention this name:");
                foreach (var mention in mentions.Take(4))
                {
                    if (!w.Add($"    {mention.File}:{mention.Line}  {mention.Expression}")) break;
                }

                w.Add("  the reference exists; it did not bind. Run 'dotnet build', then 'csmesh index'.");
            }

            // Reflection, serialization and container scanning all call code that nothing in the
            // source refers to. Saying "unused" here would be the wrong conclusion.
            if (g.ScanRegistrations.Count > 0)
            {
                w.Add($"  {g.ScanRegistrations.Count} assembly-scan registration(s) exist; a scan can");
                w.Add("  resolve a type nothing names in source");
            }

            if (IsEntrypoint(node))
            {
                w.Add("  it is an entrypoint: the framework calls it, not your code");
            }
        }

        w.Force("");
        w.Force("Absent from the graph is not the same as absent from the codebase.");

        return Exit.Ok;
    }

    // ------------------------------------------------------------------ shared diagnosis

    /// <summary>
    /// What the indexer recorded about this symbol's own source range. Node spans and unresolved
    /// sites both carry line numbers, so a failure can be attributed to the member it happened in
    /// rather than reported as a repository-wide statistic.
    /// </summary>
    private static List<string> ReasonsFor(Graph g, Node node)
    {
        var reasons = new List<string>();
        if (node.File.Length == 0) return reasons;

        var inside = g.Unresolved
            .Where(u => string.Equals(u.File, node.File, StringComparison.OrdinalIgnoreCase))
            .Where(u => node.Covers(u.Line))
            .ToList();

        foreach (var group in inside.GroupBy(u => $"{u.Kind}/{u.Reason}").OrderByDescending(x => x.Count()))
        {
            var sample = group.First();
            reasons.Add($"{group.Count()} {group.Key} at {sample.File}:{sample.Line} ({sample.Expression})");
            reasons.Add("  " + Remedy(sample.Reason));
        }

        var external = g.ExternalTypes
            .Where(x => x.Sites.Any(s => InsideSpan(s, node)))
            .Take(3)
            .ToList();

        foreach (var type in external)
        {
            reasons.Add($"uses {type.Name} from {type.Assembly}, which is not indexed");
        }

        // An abstraction with no implementation in source cannot be walked through, and that is a
        // fact about the repository rather than a failure of the index.
        if (node.Kind is "interface" &&
            g.Out(node.Id).All(e => e.Kind is not (EdgeKind.Interface or EdgeKind.Override)))
        {
            reasons.Add($"no type in this repository implements {node.Short}");
            reasons.Add("  the implementation is in a referenced assembly or generated at build time");
        }

        return reasons;
    }

    /// <summary>
    /// Unresolved sites whose expression text names this symbol. Text matching is a weak signal
    /// everywhere else in this tool; here it is the right one, because the question is precisely
    /// whether something the compiler could not bind was talking about this symbol.
    /// </summary>
    private static List<UnresolvedSite> Mentions(Graph g, Node node)
    {
        var simple = node.Short.Contains('.') ? node.Short[(node.Short.LastIndexOf('.') + 1)..] : node.Short;
        if (simple.Length < 3) return [];

        return g.Unresolved
            .Where(u => u.Expression.Contains(simple, StringComparison.Ordinal))
            .Where(u => !string.Equals(u.File, node.File, StringComparison.OrdinalIgnoreCase) || !node.Covers(u.Line))
            .Take(20)
            .ToList();
    }

    private static bool InsideSpan(string site, Node node)
    {
        var colon = site.LastIndexOf(':');
        if (colon <= 0) return false;
        if (!string.Equals(site[..colon], node.File, StringComparison.OrdinalIgnoreCase)) return false;

        return int.TryParse(site[(colon + 1)..], out var line) && node.Covers(line);
    }

    private static string Remedy(string reason) => reason switch
    {
        "no-candidate-symbol" or "unbound-type" =>
            "run 'dotnet build' so bin/ assemblies exist, then 'csmesh index'",
        "ambiguous-overload" =>
            "the edge points at the first candidate and may be wrong; check the overload by hand",
        "ambiguous-type-name" =>
            "two types share this name across namespaces, so no binding was recorded at all",
        "ambiguous-request-name" =>
            "two request types share this name; the dispatch was skipped rather than misrouted",
        "no-handler" =>
            "no handler for this request exists here; it may live in another solution",
        "assembly-scan-unfiltered" =>
            "a scan with no AssignableTo filter binds everything; csmesh will not guess the pairs",
        _ => "open the file to confirm"
    };
}
