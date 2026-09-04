using System.Diagnostics;
using System.Text.RegularExpressions;
using CsMesh.Common;
using CsMesh.Models;

namespace CsMesh.Analysis;

/// <summary>
/// Turns a git diff into the set of symbols it touched.
///
/// blast-radius answers "what breaks if I change this symbol". Nobody starts there. They start
/// from a change they already made and ask what it hit -- which is the same question over a
/// change-set instead of a node.
/// </summary>
public static partial class Queries
{
    private static readonly Regex HunkHeader =
        new(@"^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,(?<count>\d+))? @@", RegexOptions.Compiled);

    public static int Diff(Graph g, string root, string range, int depth, BudgetWriter w, HashSet<string> dirty)
    {
        if (!TryReadDiff(root, range, out var raw, out var failure))
        {
            w.Force(failure);
            return Exit.Usage;
        }

        var touched = ChangedLines(raw);
        if (touched.Count == 0)
        {
            w.Force($"no C# changes in '{range}'.");
            return Exit.NotFound;
        }

        var changed = SymbolsCovering(g, touched);
        if (changed.Count == 0)
        {
            w.Force($"{touched.Count} file(s) changed, but no indexed symbol covers the changed lines.");
            w.Force("The edits may be outside a declaration, or the index may predate them: csmesh index");
            return Exit.NotFound;
        }

        // Union of the reverse reach of every changed symbol, deduplicated. Symbols that are
        // themselves in the change-set are dropped from the affected set -- you already know.
        var changedIds = changed.Select(n => n.Id).ToHashSet();
        var affected = new Dictionary<int, (Node Node, double Score, string? Source)>();

        foreach (var node in changed)
        {
            foreach (var reach in ReverseReachScored(g, node, depth))
            {
                if (changedIds.Contains(reach.Node.Id)) continue;

                if (affected.TryGetValue(reach.Node.Id, out var existing) && existing.Score >= reach.Score) continue;
                affected[reach.Node.Id] = (reach.Node, reach.Score, reach.Source);
            }
        }

        var entrypoints = affected.Values.Where(x => IsEntrypoint(x.Node)).ToList();
        var tests = affected.Values.Where(x => IsTest(x.Node)).ToList();
        var production = affected.Values.Where(x => !IsEntrypoint(x.Node) && !IsTest(x.Node)).ToList();

        w.Force($"{range}: {changed.Count} changed symbol(s) across {touched.Count} file(s)");

        if (!Section("CHANGED", changed.Select(n => (n, 1.0, (string?)null)), "changed")) return Truncated(w);
        if (!Section("ENTRYPOINTS", entrypoints.Select(x => (x.Node, x.Score, x.Source)), "entrypoint")) return Truncated(w);
        if (!Section("AFFECTED", production.Select(x => (x.Node, x.Score, x.Source)), "affected")) return Truncated(w);
        if (!Section("TESTS", tests.Select(x => (x.Node, x.Score, x.Source)), "test")) return Truncated(w);

        var weakest = affected.Values.Select(x => x.Score).DefaultIfEmpty(1.0).Min();
        w.Force("");
        w.Force($"IMPACT  {affected.Count} affected member(s), {entrypoints.Count} entrypoint(s), {tests.Count} test(s)");

        if (weakest < Edge.TrustThreshold)
        {
            w.Force($"        weakest path {weakest:0.00}; rows marked ?score are inferred, not read off a symbol.");
        }

        if (affected.Count == 0)
        {
            w.Force("        nothing outside the change-set reaches these symbols.");
        }

        return Exit.Ok;

        bool Section(string title, IEnumerable<(Node Node, double Score, string? Source)> items, string relation)
        {
            var list = items.OrderBy(x => x.Node.Short, StringComparer.Ordinal).Take(25).ToList();
            if (list.Count == 0) return true;

            if (!w.Add("")) return false;
            if (!w.Add(title)) return false;

            foreach (var (node, score, source) in list)
            {
                var mark = score < 1.0 ? $"  [?{score:0.00}{(source != null ? " " + source : "")}]" : "";
                var row = Row(node, 1, relation, null, dirty);
                if (score < 1.0)
                {
                    row.Confidence = score;
                    row.Source = source;
                }

                if (!w.Add($"  {node.Short}{mark}{TagSuffix(node)}{Loc(node)}{StaleTag(node, dirty)}", row))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static int Truncated(BudgetWriter w)
    {
        w.Force("");
        w.Force("OVER BUDGET: raise --budget, or narrow the range with --depth 1.");
        return Exit.OverBudget;
    }

    /// <summary>
    /// Runs git rather than reading .git by hand. Diff parsing against packed objects is not
    /// something to reimplement for a convenience command.
    /// </summary>
    private static bool TryReadDiff(string root, string range, out string output, out string failure)
    {
        output = "";
        failure = "";

        var arguments = $"--no-pager diff --unified=0 --no-color --no-ext-diff {range} -- \"*.cs\"";

        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (process == null)
            {
                failure = "could not start git.";
                return false;
            }

            output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0) return true;

            failure = $"git failed ({process.ExitCode}): {error.Trim()}";
            return false;
        }
        catch (Exception ex)
        {
            failure = $"could not run git in {root}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// File -> the 1-based lines the diff added or replaced. Deletions leave no line to attribute,
    /// so a pure deletion hunk contributes the line it collapsed onto.
    /// </summary>
    private static Dictionary<string, List<int>> ChangedLines(string diff)
    {
        var result = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        string? file = null;

        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = line[4..].Trim();
                file = path == "/dev/null" ? null
                    : path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;
                continue;
            }

            if (file == null || !line.StartsWith("@@", StringComparison.Ordinal)) continue;

            var match = HunkHeader.Match(line);
            if (!match.Success) continue;

            var start = int.Parse(match.Groups["start"].Value);
            var count = match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value) : 1;

            if (!result.TryGetValue(file, out var lines)) result[file] = lines = [];
            for (var i = 0; i < Math.Max(count, 1); i++) lines.Add(start + i);
        }

        return result;
    }

    /// <summary>
    /// The innermost declaration containing each changed line. Innermost matters: a change inside
    /// a method should attribute to the method, not to the type that also spans those lines.
    /// </summary>
    private static List<Node> SymbolsCovering(Graph g, Dictionary<string, List<int>> touched)
    {
        var byFile = g.Nodes
            .Where(n => n.File.Length > 0)
            .GroupBy(n => n.File.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);

        var found = new Dictionary<int, Node>();

        foreach (var (file, lines) in touched)
        {
            if (!byFile.TryGetValue(file.Replace('\\', '/'), out var candidates)) continue;

            foreach (var line in lines)
            {
                var covering = candidates.Where(n => n.Covers(line)).ToList();
                if (covering.Count == 0) continue;

                // Narrowest span wins; a method is inside its type.
                var best = covering
                    .OrderBy(n => Math.Max(n.EndLine, n.Line) - n.Line)
                    .ThenBy(n => n.Kind == "type" || n.Kind == "interface" ? 1 : 0)
                    .First();

                found[best.Id] = best;
            }
        }

        return found.Values.OrderBy(n => n.File, StringComparer.Ordinal).ThenBy(n => n.Line).ToList();
    }

    private static List<Reach> ReverseReachScored(Graph g, Node target, int depth)
    {
        var seen = new HashSet<int> { target.Id };
        var reached = new List<Reach>();
        var frontier = new List<Reach> { new(target, 0, 1.0, null) };

        if (target.Kind is "type" or "interface" or "enum")
        {
            foreach (var e in g.Out(target.Id).Where(x => x.Kind == EdgeKind.TypeUse))
            {
                if (g.ById(e.To) is { } m && seen.Add(m.Id)) frontier.Add(new Reach(m, 0, 1.0, null));
            }
        }

        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var next = new List<Reach>();
            foreach (var reach in frontier)
            {
                foreach (var e in g.In(reach.Node.Id))
                {
                    if (e.Kind == EdgeKind.TypeUse) continue;
                    var from = g.ById(e.From);
                    if (from == null || !seen.Add(from.Id)) continue;

                    var hop = new Reach(from, reach.Level + 1, Math.Min(reach.Score, e.Score),
                                        e.Score < reach.Score ? e.Source : reach.Source);
                    reached.Add(hop);
                    next.Add(hop);
                }
            }

            frontier = next;
        }

        return reached;
    }
}
