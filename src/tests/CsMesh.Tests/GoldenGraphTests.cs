using System.Globalization;
using System.Text;
using CsMesh.Analysis;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Golden graphs for the dispatch shapes. Each fixture under Fixtures/&lt;case&gt;/ is a small, real
/// solution; its checked-in snapshot is what the indexer must produce.
///
/// Built from source alone: the harness never restores or builds the fixture, so the graph depends
/// only on the fixture text, the pinned Roslyn version, and the shared framework. Fixtures therefore
/// declare every handler interface and message locally and reference no NuGet package -- a package
/// type would be unbound and the snapshot would encode a machine-dependent degraded graph.
///
/// Identity is <see cref="Node.Key"/>, never the positional Id, so a diff reads as "this edge lost
/// its confidence" instead of "every id after line 10 shifted". Edge lines carry <see cref="Node.Name"/>
/// for readability; Key stays the anchor in the nodes section, and any name shared by more than one
/// node is shown by key and listed in the header.
///
/// The unchecked fields a regression would quietly change -- Confidence, Source, Site -- are all
/// present, and unresolved sites are pinned in their own section.
///
/// Regenerate every snapshot with:
///   set CSMESH_UPDATE_SNAPSHOTS=1 &amp;&amp; dotnet test --filter FullyQualifiedName~GoldenGraphTests
/// Without that variable a missing snapshot fails; it is never written on CI.
/// </summary>
public sealed class GoldenGraphTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CsMesh.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string FixturesDir() =>
        Path.Combine(RepoRoot(), "src", "tests", "CsMesh.Tests", "Fixtures");

    public static IEnumerable<object[]> Cases()
    {
        var dir = FixturesDir();
        if (!Directory.Exists(dir)) yield break;

        foreach (var caseDir in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (Directory.EnumerateFiles(caseDir, "*.cs", SearchOption.AllDirectories).Any())
            {
                yield return [Path.GetFileName(caseDir)];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_graph_matches_its_checked_in_snapshot(string caseName)
    {
        var caseDir = Path.Combine(FixturesDir(), caseName);
        var snapshotPath = Path.Combine(caseDir, "expected.graph.txt");

        // Build output would be scanned into the reference set and change the graph. The harness
        // builds from source alone; a bin/obj here means someone built the fixture by hand.
        foreach (var artifact in new[] { "bin", "obj" })
        {
            Assert.False(
                Directory.Exists(Path.Combine(caseDir, artifact)),
                $"fixture '{caseName}' contains {artifact}/; remove it -- the graph is built from source alone");
        }

        var graph = Indexer.Build(caseDir);
        graph.Freeze();
        var actual = Render(graph, caseName);

        // Update mode overwrites existing snapshots as well as creating missing ones; a regeneration
        // command that cannot rewrite an existing file is not a regeneration command. Off CI (no
        // variable) a missing snapshot fails and is never written.
        if (Environment.GetEnvironmentVariable("CSMESH_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(snapshotPath, actual);
            return;
        }

        if (!File.Exists(snapshotPath))
        {
            Assert.Fail(
                $"no snapshot for '{caseName}': {snapshotPath}{Environment.NewLine}" +
                "regenerate: set CSMESH_UPDATE_SNAPSHOTS=1 && dotnet test --filter FullyQualifiedName~GoldenGraphTests");
        }

        var expected = File.ReadAllText(snapshotPath);

        Assert.True(
            Normalize(expected) == Normalize(actual),
            $"graph snapshot drifted for '{caseName}':{Environment.NewLine}{Diff(Normalize(expected), Normalize(actual))}");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');

    private static string Render(Graph g, string caseName)
    {
        var byId = g.Nodes.ToDictionary(n => n.Id);
        var namesById = g.Nodes.ToDictionary(n => n.Id, n => n.Key.Length > 0 ? n.Key : "!" + n.Name);

        // A name shared by two nodes cannot address an edge, so those nodes are shown by key.
        var nameCounts = g.Nodes
            .GroupBy(n => n.Name, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var ambiguous = nameCounts.Where(kv => kv.Value > 1).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();

        string Ref(int id)
        {
            if (!byId.TryGetValue(id, out var node)) return "?id:" + id;
            return nameCounts[node.Name] > 1 ? namesById[id] : node.Name;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# csmesh graph snapshot: {caseName}");
        sb.AppendLine("# format 2");
        sb.AppendLine("# regenerate: CSMESH_UPDATE_SNAPSHOTS=1 dotnet test --filter FullyQualifiedName~GoldenGraphTests");
        if (ambiguous.Count > 0)
        {
            sb.AppendLine($"# names shared by more than one node (shown by key): {string.Join(", ", ambiguous)}");
        }

        sb.AppendLine();
        sb.AppendLine("## nodes");
        sb.AppendLine("# key | kind | name | file");
        foreach (var n in g.Nodes
                     .OrderBy(n => n.Key.Length > 0 ? n.Key : "!" + n.Name, StringComparer.Ordinal)
                     .ThenBy(n => n.Kind, StringComparer.Ordinal)
                     .ThenBy(n => n.Name, StringComparer.Ordinal))
        {
            var key = n.Key.Length > 0 ? n.Key : "!" + n.Name;
            sb.AppendLine($"{key} | {n.Kind} | {n.Name} | {n.File.Replace('\\', '/')}");
        }

        sb.AppendLine();
        sb.AppendLine("## edges");
        sb.AppendLine("# from | kind | to | confidence | source | site | note");
        var edges = g.Edges
            .Select(e => new
            {
                From = Ref(e.From),
                Kind = e.Kind.ToString(),
                To = Ref(e.To),
                Confidence = (e.Confidence ?? 1.0).ToString("0.00", CultureInfo.InvariantCulture),
                Source = e.Source ?? "-",
                Site = (e.Site ?? "-").Replace('\\', '/'),
                Note = e.Note ?? "-"
            })
            .OrderBy(e => e.From, StringComparer.Ordinal)
            .ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.To, StringComparer.Ordinal)
            .ThenBy(e => e.Source, StringComparer.Ordinal)
            .ThenBy(e => e.Site, StringComparer.Ordinal)
            .ThenBy(e => e.Note, StringComparer.Ordinal);

        foreach (var e in edges)
        {
            sb.AppendLine($"{e.From} | {e.Kind} | {e.To} | {e.Confidence} | {e.Source} | {e.Site} | {e.Note}");
        }

        sb.AppendLine();
        sb.AppendLine("## unresolved");
        sb.AppendLine("# kind/reason | site | expression");
        var unresolved = g.Unresolved
            .Select(u => new
            {
                Reason = $"{u.Kind}/{u.Reason}",
                Site = u.File.Length > 0 ? $"{u.File.Replace('\\', '/')}:{u.Line}" : "-",
                Expression = u.Expression
            })
            .OrderBy(u => u.Reason, StringComparer.Ordinal)
            .ThenBy(u => u.Site, StringComparer.Ordinal)
            .ThenBy(u => u.Expression, StringComparer.Ordinal);

        foreach (var u in unresolved)
        {
            sb.AppendLine($"{u.Reason} | {u.Site} | {u.Expression}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Positional over the total order, deliberately. A set difference cannot see a duplicate: if
    /// the indexer starts emitting one edge twice -- the exact regression the newest-config rule
    /// exists to prevent -- the line sets stay equal and a set comparison stays green. Comparing the
    /// sorted sequence catches both a changed line and a repeated one.
    /// </summary>
    internal static string Diff(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var sb = new StringBuilder();

        var max = Math.Max(e.Length, a.Length);
        var shown = 0;
        for (var i = 0; i < max && shown < 40; i++)
        {
            var el = i < e.Length ? e[i] : "<end of snapshot>";
            var al = i < a.Length ? a[i] : "<end of graph>";
            if (string.Equals(el, al, StringComparison.Ordinal)) continue;

            sb.AppendLine($"line {i + 1}:");
            sb.AppendLine($"  snapshot: {el}");
            sb.AppendLine($"  graph:    {al}");
            shown++;
        }

        if (e.Length != a.Length) sb.AppendLine($"line count: snapshot {e.Length}, graph {a.Length}");
        return sb.ToString();
    }

    // The comparer is a property of the guard, so it is tested directly: two line arrays, no
    // corrupted snapshot on disk.

    [Fact]
    public void The_comparer_reports_a_duplicated_line_as_a_mismatch()
    {
        var snapshot = "## edges\na | Call | b\nc | Call | d";
        var graph = "## edges\na | Call | b\nc | Call | d\nc | Call | d";

        var diff = Diff(snapshot, graph);

        Assert.NotEmpty(diff);
        Assert.Contains("line count: snapshot 3, graph 4", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void The_comparer_is_silent_on_identical_input()
    {
        Assert.Empty(Diff("a\nb\nc", "a\nb\nc"));
    }

    [Fact]
    public void The_comparer_reports_a_changed_field()
    {
        var snapshot = "Demo.Go | Mediatr | Demo.Handle | 1.00 | semantic-request | Bus.cs:22 | -";
        var graph = "Demo.Go | Mediatr | Demo.Handle | 0.70 | short-name-match | Bus.cs:22 | -";

        var diff = Diff(snapshot, graph);

        Assert.Contains("0.70", diff, StringComparison.Ordinal);
        Assert.Contains("1.00", diff, StringComparison.Ordinal);
    }
}
