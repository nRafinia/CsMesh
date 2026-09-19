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
/// Deliberately one screen. A map that does not fit on one is a directory listing. So it selects
/// rather than emit-until-full: each section is capped, and the closing footer names what the caps
/// withheld and the query that returns the full list where one exists. That cap is the summary's
/// design, so a capped map still exits 0; the incomplete marker and exit 2 are reserved for the
/// budget actually running out, which is a genuinely incomplete answer.
/// </summary>
public static partial class Queries
{
    private const int ProjectRows = 8;
    private const int TestProjectRows = 5;
    private const int EntrypointRows = 5;
    private const int BusiestRows = 5;
    private const int MostReadRows = 3;

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

        // Capped: the section has more rows than its cap selects -- the summary's design.
        // Truncated: the budget ran out before even the cap was reached -- an incomplete answer.
        var capped = new List<(string Section, int Shown, int Total, string? Command)>();
        var truncated = new List<(string Section, int Shown, int Total, string? Command)>();

        Projects(g, nodes, w, capped, truncated);
        Entrypoints(nodes, w, dirty, capped, truncated);
        Busiest(g, nodes, w, dirty, capped, truncated);

        if (truncated.Count > 0)
        {
            var detail = Detail(truncated);
            var marker = $"INCOMPLETE: map ran out of budget; {detail}";
            w.AddMarker(marker.Length <= 150 ? marker : marker[..147] + "...");
            return Exit.OverBudget;
        }

        if (capped.Count > 0)
        {
            var footer = $"# map is a summary; withheld {Detail(capped)}";
            w.AddNote(footer.Length <= 170 ? footer : footer[..167] + "...");
        }

        return Exit.Ok;
    }

    private static string Detail(List<(string Section, int Shown, int Total, string? Command)> rows) =>
        string.Join(", ", rows.Select(x =>
            x.Command != null ? $"{x.Section} {x.Shown}/{x.Total} (see: {x.Command})" : $"{x.Section} {x.Shown}/{x.Total}"));

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
    private static void Projects(Graph g, List<Node> nodes, BudgetWriter w,
        List<(string, int, int, string?)> capped, List<(string, int, int, string?)> truncated)
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

        Section("PROJECTS", "PROJECTS  deepest first; -> is a declared ProjectReference", production, ProjectRows);
        Section("TEST PROJECTS", "TEST PROJECTS", tests, TestProjectRows);

        void Section(string label, string title, List<string> projects, int cap)
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
                truncated.Add((label, 0, ordered.Count, null));
                return;
            }

            w.Add("");
            w.Add(title);

            var shown = 0;
            var limit = Math.Min(ordered.Count, cap);
            while (shown < limit && w.Add(Row(ordered[shown]))) shown++;

            if (shown < limit) truncated.Add((label, shown, ordered.Count, null));
            else if (ordered.Count > cap) capped.Add((label, shown, ordered.Count, null));
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
    /// The full per-file list is a separate query, so anything the cap withholds names it.
    /// </summary>
    private static void Entrypoints(List<Node> nodes, BudgetWriter w, HashSet<string> dirty,
        List<(string, int, int, string?)> capped, List<(string, int, int, string?)> truncated)
    {
        var entrypoints = nodes.Where(IsEntrypoint).Where(n => n.File.Length > 0).ToList();
        if (entrypoints.Count == 0) return;

        var files = entrypoints
            .GroupBy(n => n.File, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ToList();

        var title = $"ENTRYPOINTS  {entrypoints.Count} in {entrypoints.Select(n => n.File).Distinct().Count()} file(s)";

        // Price the heading together with its first row. Printing a title and then running out
        // reads as "nothing found" when the truth is "the budget ended here".
        if (!Fits(w, title, FileRow(files[0], dirty)))
        {
            truncated.Add(("ENTRYPOINTS", 0, files.Count, "csmesh entrypoints"));
            return;
        }

        w.Add("");
        w.Add(title);

        var shown = 0;
        var limit = Math.Min(files.Count, EntrypointRows);
        while (shown < limit && w.Add(FileRow(files[shown], dirty))) shown++;

        if (shown < limit) truncated.Add(("ENTRYPOINTS", shown, files.Count, "csmesh entrypoints"));
        else if (files.Count > EntrypointRows) capped.Add(("ENTRYPOINTS", shown, files.Count, "csmesh entrypoints"));
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
    private static void Busiest(Graph g, List<Node> nodes, BudgetWriter w, HashSet<string> dirty,
        List<(string, int, int, string?)> capped, List<(string, int, int, string?)> truncated)
    {
        var live = nodes.Where(n => !IsTest(n))
            .Select(n => (Node: n, Callers: g.In(n.Id).Count(e => e.Kind != EdgeKind.TypeUse)))
            .Where(x => x.Callers > 1)
            .ToList();

        Rank("BUSIEST", "BUSIEST  most direct callers; changing behaviour here costs the most",
             live.Where(x => x.Node.Kind == "method"), BusiestRows);

        Rank("MOST READ", "MOST READ  data everything touches; changing shape here costs the most",
             live.Where(x => x.Node.Kind is "property" or "field" or "enum-member"), MostReadRows);

        void Rank(string label, string title, IEnumerable<(Node Node, int Callers)> source, int cap)
        {
            var ranked = source.OrderByDescending(x => x.Callers).ToList();
            if (ranked.Count == 0) return;

            if (!Fits(w, title, Line(ranked[0])))
            {
                truncated.Add((label, 0, ranked.Count, null));
                return;
            }

            w.Add("");
            w.Add(title);

            var shown = 0;
            var limit = Math.Min(ranked.Count, cap);
            while (shown < limit &&
                   w.Add(Line(ranked[shown]), Row(ranked[shown].Node, 1, "busiest", ranked[shown].Callers.ToString(), dirty)))
            {
                shown++;
            }

            if (shown < limit) truncated.Add((label, shown, ranked.Count, null));
            else if (ranked.Count > cap) capped.Add((label, shown, ranked.Count, null));

            string Line((Node Node, int Callers) entry)
            {
                var project = entry.Node.Project.Length > 0 ? $"  [{entry.Node.Project}]" : "";
                return $"  {entry.Callers,3}  {entry.Node.Short}{project}{Loc(entry.Node)}{StaleTag(entry.Node, dirty)}";
            }
        }
    }
}
