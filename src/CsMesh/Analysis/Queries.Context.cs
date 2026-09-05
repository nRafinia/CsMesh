using CsMesh.Common;
using CsMesh.Models;

namespace CsMesh.Analysis;

public static partial class Queries
{
    /// <summary>
    /// Everything structural worth knowing about one symbol, in one call.
    ///
    /// The chained alternative -- trace, then impl, then blast-radius, then entrypoints -- costs
    /// four invocations and makes the agent decide which of them the question needed. Sections are
    /// emitted in decreasing order of usefulness, so a budget that runs out truncates the tail
    /// rather than the answer.
    /// </summary>
    public static int Context(Graph g, Node node, int depth, BudgetWriter w, HashSet<string> dirty)
    {
        var overflowed = false;

        w.Force($"{node.Short}{Loc(node)}{StaleTag(node, dirty)}", Row(node, 0, "root", null, dirty));

        var role = new List<string> { node.Kind };
        role.AddRange(node.Tags.Where(t => !t.StartsWith("http:") && !t.StartsWith("route:")));
        Section("ROLE", [string.Join("  ", role.Distinct())]);

        // What the thing *is*, before what touches it. A caller who asks for context on a type
        // almost always wants its shape first -- which fields, which types, what is nullable --
        // and today they get a file:line and go read it themselves.
        if (node.Kind is "type" or "interface" or "enum" or "enum")
        {
            var members = g.Out(node.Id)
                .Where(e => e.Kind == EdgeKind.TypeUse && e.Note is "member" or "ctor")
                .Select(e => g.ById(e.To))
                .Where(n => n != null)
                .Select(n => n!)
                .OrderBy(n => n.Kind == "method" ? 1 : 0)
                .ThenBy(n => n.Line)
                .Take(20)
                .ToList();

            Section("MEMBERS", members.Select(Describe), members);
        }

        var callers = g.In(node.Id)
            .Where(e => e.Kind != EdgeKind.TypeUse)
            .Select(e => (Edge: e, Node: g.ById(e.From)))
            .Where(x => x.Node != null)
            .DistinctBy(x => x.Node!.Id)
            .Take(12)
            .ToList();

        Emit("CALLED BY", callers.Select(x => (x.Node!, x.Edge, "caller")));

        var callees = g.Out(node.Id)
            .Where(IsFlow)
            // Same reason as in Impl: a DiBinding notes its lifetime, not the marker, so matching
            // on the marker alone ranked the container's own answer by score against everything
            // else and let a tie decide.
            .OrderByDescending(e => e.Kind == EdgeKind.DiBinding || e.Note == "di-bound")
            .ThenByDescending(e => e.Score)
            .Select(e => (Edge: e, Node: g.ById(e.To)))
            .Where(x => x.Node != null)
            .DistinctBy(x => x.Node!.Id)
            .Take(12)
            .ToList();

        Emit("CALLS", callees.Select(x => (x.Node!, x.Edge, Relation(x.Edge.Kind))));

        // What actually runs behind an abstraction, and what abstraction this sits behind.
        var implementations = g.Out(node.Id)
            .Where(e => e.Kind is EdgeKind.Interface or EdgeKind.Override or EdgeKind.DiBinding)
            .Select(e => (Edge: e, Node: g.ById(e.To)))
            .Where(x => x.Node != null)
            .DistinctBy(x => x.Node!.Id)
            .Take(8)
            .ToList();

        Emit("IMPLEMENTATION", implementations.Select(x => (x.Node!, x.Edge, "impl")));

        var reachable = ReverseReach(g, node, depth);
        var entrypoints = reachable.Where(IsEntrypoint).Take(10).ToList();

        if (entrypoints.Count > 0)
        {
            Section("ENTRYPOINTS", entrypoints.Select(ep => $"{ep.Short}{TagSuffix(ep)}"), entrypoints);
        }

        Section("IMPACT",
        [
            $"{callers.Count} direct caller(s), {reachable.Count} reachable member(s), {entrypoints.Count} entrypoint(s)"
        ]);

        var files = new[] { node }
            .Concat(callers.Select(x => x.Node!))
            .Concat(callees.Select(x => x.Node!))
            .Concat(implementations.Select(x => x.Node!))
            .Concat(entrypoints)
            .Where(n => n.File.Length > 0)
            .Select(n => n.File)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        Section("FILES", files);

        if (!overflowed) return Exit.Ok;

        w.Force("");
        w.Force("OVER BUDGET: context truncated. Raise --budget, or use trace/blast-radius for one section.");
        return Exit.OverBudget;

        void Section(string title, IEnumerable<string> lines, List<Node>? rows = null)
        {
            var materialised = lines.Where(l => l.Length > 0).ToList();
            if (materialised.Count == 0 || overflowed) return;

            if (!w.Add("")) { overflowed = true; return; }
            if (!w.Add(title)) { overflowed = true; return; }

            for (var i = 0; i < materialised.Count; i++)
            {
                var row = rows != null && i < rows.Count
                    ? Row(rows[i], 1, title.ToLowerInvariant(), null, dirty)
                    : null;

                if (w.Add("  " + materialised[i], row)) continue;
                overflowed = true;
                return;
            }
        }

        void Emit(string title, IEnumerable<(Node Node, Edge Edge, string Relation)> items)
        {
            var list = items.ToList();
            if (list.Count == 0 || overflowed) return;

            if (!w.Add("")) { overflowed = true; return; }
            if (!w.Add(title)) { overflowed = true; return; }

            foreach (var (n, e, relation) in list)
            {
                var row = Row(n, 1, e, dirty);
                row.Relation = relation;

                if (w.Add($"  {n.Short}{Marker(e)}{TagSuffix(n)}{Loc(n)}{StaleTag(n, dirty)}", row)) continue;
                overflowed = true;
                return;
            }
        }
    }

    /// <summary>
    /// One member on one line: name first, because that is what the reader is scanning for.
    /// </summary>
    private static string Describe(Node member)
    {
        var name = member.Short.Contains('.') ? member.Short[(member.Short.LastIndexOf('.') + 1)..] : member.Short;

        if (member.Signature.Length == 0) return name;
        return member.Kind == "method" ? $"{name}{member.Signature}" : $"{name}  {member.Signature}";
    }

    /// <summary>
    /// Members that would be affected if this symbol changed. Shared with blast-radius in spirit,
    /// but returns the set instead of printing it.
    /// </summary>
    private static List<Node> ReverseReach(Graph g, Node target, int depth)
    {
        var seen = new HashSet<int> { target.Id };
        var reached = new List<Node>();
        var frontier = new List<Node> { target };

        if (target.Kind is "type" or "interface" or "enum")
        {
            foreach (var e in g.Out(target.Id).Where(x => x.Kind == EdgeKind.TypeUse))
            {
                if (g.ById(e.To) is { } m && seen.Add(m.Id)) frontier.Add(m);
            }
        }

        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var next = new List<Node>();
            foreach (var node in frontier)
            {
                foreach (var e in g.In(node.Id))
                {
                    if (e.Kind == EdgeKind.TypeUse) continue;
                    var from = g.ById(e.From);
                    if (from == null || !seen.Add(from.Id)) continue;
                    reached.Add(from);
                    next.Add(from);
                }
            }

            frontier = next;
        }

        return reached;
    }
}