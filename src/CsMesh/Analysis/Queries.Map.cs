using CsMesh.Common;
using CsMesh.Models;

namespace CsMesh.Analysis;

/// <summary>
/// Where the weight is.
///
/// Every other command answers a question about a symbol you already know. This one is for the
/// moment before that: an agent dropped into an unfamiliar repository with no idea which of four
/// hundred types matter. `ls` and `tree` answer that question with folder names, which is the
/// wrong axis -- src/Application/Services tells you nothing about whether anything in it is load
/// bearing. The graph knows: what depends on what, where the entrypoints cluster, and which
/// handful of members everything else runs through.
///
/// Deliberately one screen. A map that does not fit on one is a directory listing.
/// </summary>
public static partial class Queries
{
    public static int Map(Graph g, string? under, BudgetWriter w, HashSet<string> dirty)
    {
        var nodes = g.Nodes.Where(n => Under(n, under)).ToList();

        if (nodes.Count == 0)
        {
            w.Force(under == null ? "the index is empty; run: csmesh index" : $"nothing indexed under '{under}'.");
            return Exit.NotFound;
        }

        var files = nodes.Where(n => n.File.Length > 0).Select(n => n.File)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        // Under a filter the global edge count would be a lie about the subtree being shown.
        var scope = nodes.Select(n => n.Id).ToHashSet();
        var edges = under == null ? g.Edges.Count : g.Edges.Count(e => scope.Contains(e.From) && scope.Contains(e.To));

        w.Force($"{files} file(s), {nodes.Count} symbol(s), {edges} edge(s)"
                + (under != null ? $"  under {under}" : ""));

        Projects(g, nodes, w);
        Entrypoints(nodes, w, dirty);
        Busiest(g, nodes, w, dirty);

        return Exit.Ok;
    }

    private static bool Under(Node n, string? prefix) =>
        prefix == null || n.File.Replace('\\', '/').StartsWith(prefix.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Assemblies and what they lean on. Direction is the whole point: the project nothing depends
    /// on is the leaf you can change freely, and the one everything depends on is the one you
    /// cannot.
    /// </summary>
    private static void Projects(Graph g, List<Node> nodes, BudgetWriter w)
    {
        var byProject = nodes
            .Where(n => n.Project.Length > 0)
            .GroupBy(n => n.Project, StringComparer.Ordinal)
            .ToList();

        if (byProject.Count == 0) return;

        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var owner = nodes.Where(n => n.Project.Length > 0).ToDictionary(n => n.Id, n => n.Project);

        foreach (var e in g.Edges)
        {
            if (e.Kind == EdgeKind.TypeUse) continue;
            if (!owner.TryGetValue(e.From, out var a) || !owner.TryGetValue(e.To, out var b)) continue;
            if (a == b) continue;

            if (!dependencies.TryGetValue(a, out var set))
                dependencies[a] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(b);
        }

        w.Force("");
        w.Force("PROJECTS");

        var width = byProject.Max(p => p.Key.Length);

        foreach (var project in byProject.OrderByDescending(p => p.Count()))
        {
            var types = project.Count(n => n.Kind is "type" or "interface" or "enum");
            var entrypoints = project.Count(IsEntrypoint);
            var leansOn = dependencies.GetValueOrDefault(project.Key, []);

            var arrow = leansOn.Count == 0
                ? "depends on nothing here"
                : "-> " + string.Join(", ", leansOn.Order(StringComparer.Ordinal).Take(4));

            var line = $"  {project.Key.PadRight(width)}  {types,4} type(s)  {entrypoints,3} entrypoint(s)  {arrow}";
            if (!w.Add(line)) return;
        }
    }

    /// <summary>
    /// Entrypoints by file rather than one by one. The file holding nine routes is where the
    /// surface area of the application is, and that is a better first read than nine separate rows.
    /// </summary>
    private static void Entrypoints(List<Node> nodes, BudgetWriter w, HashSet<string> dirty)
    {
        var entrypoints = nodes.Where(IsEntrypoint).Where(n => n.File.Length > 0).ToList();
        if (entrypoints.Count == 0) return;

        w.Force("");
        w.Force($"ENTRYPOINTS  {entrypoints.Count} in {entrypoints.Select(n => n.File).Distinct().Count()} file(s)");

        foreach (var file in entrypoints
                     .GroupBy(n => n.File, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(x => x.Count())
                     .Take(8))
        {
            var stale = dirty.Contains(file.Key) ? "  [STALE]" : "";
            var kinds = file.SelectMany(n => n.Tags)
                .Where(t => t.StartsWith("http:") || t is "consumer" or "hosted" or "handler")
                .Select(t => t.StartsWith("http:") ? "http" : t)
                .Distinct()
                .Order(StringComparer.Ordinal);

            if (!w.Add($"  {file.Count(),3}  {file.Key}  ({string.Join(", ", kinds)}){stale}")) return;
        }
    }

    /// <summary>
    /// Direct callers, not transitive reach: reach on every node is quadratic and the ranking
    /// barely moves, because what everything runs through tends to be called from many places
    /// directly.
    ///
    /// Methods and data are counted separately on purpose. A property with twenty readers and a
    /// method with twenty callers are both hotspots, but the first is a hotspot for a change of
    /// shape and the second for a change of behaviour, and mixing them buries the methods under a
    /// list of getters nobody is worried about.
    /// </summary>
    private static void Busiest(Graph g, List<Node> nodes, BudgetWriter w, HashSet<string> dirty)
    {
        var live = nodes.Where(n => !IsTest(n))
            .Select(n => (Node: n, Callers: g.In(n.Id).Count(e => e.Kind != EdgeKind.TypeUse)))
            .Where(x => x.Callers > 1)
            .ToList();

        Rank("BUSIEST  most direct callers; changing behaviour here costs the most",
             live.Where(x => x.Node.Kind == "method"), 8);

        Rank("MOST READ  data everything touches; changing shape here costs the most",
             live.Where(x => x.Node.Kind is "property" or "field" or "enum-member"), 5);

        void Rank(string title, IEnumerable<(Node Node, int Callers)> source, int take)
        {
            var ranked = source.OrderByDescending(x => x.Callers).Take(take).ToList();
            if (ranked.Count == 0) return;

            if (!w.Add("")) return;
            if (!w.Add(title)) return;

            foreach (var (node, callers) in ranked)
            {
                var project = node.Project.Length > 0 ? $"  [{node.Project}]" : "";
                if (!w.Add($"  {callers,3}  {node.Short}{project}{Loc(node)}{StaleTag(node, dirty)}",
                           Row(node, 1, "busiest", callers.ToString(), dirty)))
                {
                    return;
                }
            }
        }
    }
}
