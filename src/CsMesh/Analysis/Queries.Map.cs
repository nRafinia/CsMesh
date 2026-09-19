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

        // A dropped section is collected, not swallowed. This command used to skip sections with
        // Fits() and still return Exit.Ok, so a map missing its entrypoints reported "complete
        // answer" -- the one thing an agent branches on without reading the payload.
        var drops = new List<(string Section, int Rows)>();

        Projects(g, nodes, w, drops);
        Entrypoints(nodes, w, dirty, drops);
        Busiest(g, nodes, w, dirty, drops);

        if (drops.Count > 0)
        {
            var detail = string.Join(", ", drops.Select(d => $"{d.Section} ({d.Rows})"));
            w.AddMarker(IncompleteMarker(w, $"dropped {detail}; narrow with --under or raise --budget"));
            return Exit.OverBudget;
        }

        return Exit.Ok;
    }

    /// <summary>
    /// Room for a blank separator, a heading, and at least one row beneath it. The separator is a
    /// token of its own; leaving it out of the sum put the cut exactly between a title and its
    /// first row at one budget in a few hundred.
    /// </summary>
    private static bool Fits(BudgetWriter w, string title, string firstRow) =>
        BudgetWriter.Estimate("") + BudgetWriter.Estimate(title) + BudgetWriter.Estimate(firstRow) <= w.Remaining;

    private static bool Under(Node n, string? prefix) =>
        prefix == null || n.File.Replace('\\', '/').StartsWith(prefix.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Assemblies and what they lean on, ordered so the map reads like a layer diagram: whatever
    /// nothing depends on sits at the bottom, entrypoints at the top.
    ///
    /// Dependencies come from the declared ProjectReference set, not from symbol edges. An
    /// interface edge runs from the abstraction to the implementation -- correct for "what runs",
    /// backwards for "what needs what" -- and reading the map off those edges produced
    /// "Fleetdeck.Core depends on Fleetdeck.Hub", which is the reverse of the architecture.
    /// </summary>
    private static void Projects(Graph g, List<Node> nodes, BudgetWriter w, List<(string Section, int Rows)> drops)
    {
        var byProject = nodes
            .Where(n => n.Project.Length > 0)
            .GroupBy(n => n.Project, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);

        if (byProject.Count == 0) return;

        var declared = g.ProjectReferences;
        var depth = Depths(byProject.Keys, declared);
        var prefix = CommonPrefix(byProject.Keys);

        // Test projects sit at the top of a topological ordering because everything is below them,
        // which pushes the project carrying half the codebase to the bottom of the screen. They
        // are listed apart for the same reason blast-radius separates test callers: a test is a
        // real dependant, but it is not the shape of the application.
        var production = byProject.Keys.Where(k => !IsTestProject(byProject[k])).ToList();
        var tests = byProject.Keys.Where(k => IsTestProject(byProject[k])).ToList();

        var width = Math.Min(byProject.Keys.Max(k => Short(k, prefix).Length), 34);

        Section("PROJECTS", "PROJECTS  deepest first; -> is a declared ProjectReference", production);
        Section("TEST PROJECTS", "TEST PROJECTS", tests);

        void Section(string label, string title, List<string> projects)
        {
            if (projects.Count == 0) return;

            // Cost the heading and its first row together. A title printed alone reads as
            // "nothing found" when the truth is that the budget ran out underneath it.
            var ordered = projects
                .OrderByDescending(k => depth.GetValueOrDefault(k))
                .ThenBy(k => k, StringComparer.Ordinal)
                .ToList();

            if (!Fits(w, title, Row(ordered[0])))
            {
                drops.Add((label, ordered.Count));
                return;
            }

            w.Add("");
            w.Add(title);

            for (var i = 0; i < ordered.Count; i++)
            {
                if (w.Add(Row(ordered[i]))) continue;
                drops.Add((label, ordered.Count - i));
                return;
            }
        }

        string Row(string project)
        {
            var members = byProject[project];
            var types = members.Count(n => n.Kind is "type" or "interface" or "enum");
            var entrypoints = members.Count(IsEntrypoint);

            var references = declared.GetValueOrDefault(project, []);
            var arrow = references.Count == 0
                ? "depends on nothing here"
                : "-> " + string.Join(", ", references.Take(5).Select(r => Short(r, prefix)))
                        + (references.Count > 5 ? $", +{references.Count - 5}" : "");

            var name = Short(project, prefix);
            var padded = name.Length >= width ? name : name.PadRight(width);

            return $"  {padded}  {types,4} type(s)  {entrypoints,3} entrypoint(s)  {arrow}";
        }
    }

    private static bool IsTestProject(List<Node> members) =>
        members.Count(IsTest) * 2 > members.Count;

    /// <summary>
    /// The shared leading segment of every project name, so a solution where each one begins
    /// "Fleetdeck." does not spend half of every line repeating it.
    /// </summary>
    private static string CommonPrefix(IEnumerable<string> names)
    {
        var list = names.ToList();
        if (list.Count < 2) return "";

        var first = list[0].Split('.');
        if (first.Length < 2) return "";

        var candidate = first[0] + ".";
        return list.All(n => n.StartsWith(candidate, StringComparison.Ordinal)) ? candidate : "";
    }

    private static string Short(string project, string prefix) =>
        prefix.Length > 0 && project.StartsWith(prefix, StringComparison.Ordinal)
            ? project[prefix.Length..]
            : project;

    /// <summary>
    /// Longest path from each project down to something that references nothing. Cycles between
    /// projects cannot happen in MSBuild, so a visited set is enough to stay terminating.
    /// </summary>
    private static Dictionary<string, int> Depths(
        IEnumerable<string> projects,
        Dictionary<string, List<string>> references)
    {
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);

        int Of(string project, HashSet<string> onPath)
        {
            if (depth.TryGetValue(project, out var known)) return known;
            if (!onPath.Add(project)) return 0;

            var deepest = 0;
            foreach (var next in references.GetValueOrDefault(project, []))
            {
                deepest = Math.Max(deepest, Of(next, onPath) + 1);
            }

            onPath.Remove(project);
            depth[project] = deepest;
            return deepest;
        }

        foreach (var project in projects) Of(project, new HashSet<string>(StringComparer.Ordinal));
        return depth;
    }

    /// <summary>
    /// Entrypoints by file rather than one by one. The file holding nine routes is where the
    /// surface area of the application is, and that is a better first read than nine separate rows.
    /// </summary>
    private static void Entrypoints(List<Node> nodes, BudgetWriter w, HashSet<string> dirty, List<(string Section, int Rows)> drops)
    {
        var entrypoints = nodes.Where(IsEntrypoint).Where(n => n.File.Length > 0).ToList();
        if (entrypoints.Count == 0) return;

        var files = entrypoints
            .GroupBy(n => n.File, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .Take(8)
            .ToList();

        var title = $"ENTRYPOINTS  {entrypoints.Count} in {entrypoints.Select(n => n.File).Distinct().Count()} file(s)";

        // Price the heading together with its first row. Printing a title and then running out
        // reads as "nothing found" when the truth is "the budget ended here".
        if (!Fits(w, title, FileRow(files[0], dirty)))
        {
            drops.Add(("ENTRYPOINTS", files.Count));
            return;
        }

        w.Add("");
        w.Add(title);

        for (var i = 0; i < files.Count; i++)
        {
            if (w.Add(FileRow(files[i], dirty))) continue;
            drops.Add(("ENTRYPOINTS", files.Count - i));
            return;
        }
    }

    private static string FileRow(IGrouping<string, Node> file, HashSet<string> dirty)
    {
        var stale = dirty.Contains(file.Key) ? "  [STALE]" : "";
        var kinds = file.SelectMany(n => n.Tags)
            .Where(t => t.StartsWith("http:") || t is "consumer" or "hosted" or "handler" or "action")
            .Select(t => t.StartsWith("http:") ? "http" : t)
            .Distinct()
            .Order(StringComparer.Ordinal);

        return $"  {file.Count(),3}  {file.Key}  ({string.Join(", ", kinds)}){stale}";
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
    private static void Busiest(Graph g, List<Node> nodes, BudgetWriter w, HashSet<string> dirty, List<(string Section, int Rows)> drops)
    {
        var live = nodes.Where(n => !IsTest(n))
            .Select(n => (Node: n, Callers: g.In(n.Id).Count(e => e.Kind != EdgeKind.TypeUse)))
            .Where(x => x.Callers > 1)
            .ToList();

        Rank("BUSIEST", "BUSIEST  most direct callers; changing behaviour here costs the most",
             live.Where(x => x.Node.Kind == "method"), 8);

        Rank("MOST READ", "MOST READ  data everything touches; changing shape here costs the most",
             live.Where(x => x.Node.Kind is "property" or "field" or "enum-member"), 5);

        void Rank(string label, string title, IEnumerable<(Node Node, int Callers)> source, int take)
        {
            var ranked = source.OrderByDescending(x => x.Callers).Take(take).ToList();
            if (ranked.Count == 0) return;

            if (!Fits(w, title, Line(ranked[0])))
            {
                drops.Add((label, ranked.Count));
                return;
            }

            w.Add("");
            w.Add(title);

            for (var i = 0; i < ranked.Count; i++)
            {
                if (w.Add(Line(ranked[i]), Row(ranked[i].Node, 1, "busiest", ranked[i].Callers.ToString(), dirty))) continue;
                drops.Add((label, ranked.Count - i));
                return;
            }

            string Line((Node Node, int Callers) entry)
            {
                var project = entry.Node.Project.Length > 0 ? $"  [{entry.Node.Project}]" : "";
                return $"  {entry.Callers,3}  {entry.Node.Short}{project}{Loc(entry.Node)}{StaleTag(entry.Node, dirty)}";
            }
        }
    }
}
