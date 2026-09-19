using System.Globalization;
using System.Text;
using CsMesh.Analysis;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Golden graphs for the dispatch shapes. Each fixture under Fixtures/&lt;case&gt;/ is a small, real
/// solution; its checked-in snapshot is what the indexer must produce. The snapshot is keyed on
/// <see cref="Node.Key"/>, never the positional Id, so a diff reads as "this edge lost its
/// confidence" instead of "every id after line 10 shifted".
///
/// Edge ordering is total and deterministic (from, kind, to, source, site, note), and the fields a
/// regression would quietly change -- Confidence, Source, Site -- are all present.
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

        var graph = Indexer.Build(caseDir);
        graph.Freeze();
        var actual = Render(graph, caseName);

        if (!File.Exists(snapshotPath))
        {
            if (Environment.GetEnvironmentVariable("CSMESH_UPDATE_SNAPSHOTS") == "1")
            {
                File.WriteAllText(snapshotPath, actual);
                return;
            }

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
        var keyById = g.Nodes.ToDictionary(
            n => n.Id,
            n => n.Key.Length > 0 ? n.Key : "!" + n.Name);

        string KeyOf(int id) => keyById.TryGetValue(id, out var key) ? key : "?id:" + id;

        var sb = new StringBuilder();
        sb.AppendLine($"# csmesh graph snapshot: {caseName}");
        sb.AppendLine("# format 1");
        sb.AppendLine("# regenerate: CSMESH_UPDATE_SNAPSHOTS=1 dotnet test --filter FullyQualifiedName~GoldenGraphTests");
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
                From = KeyOf(e.From),
                Kind = e.Kind.ToString(),
                To = KeyOf(e.To),
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

        return sb.ToString();
    }

    /// <summary>
    /// Set difference, not positional: a line that moved is not a change. Reads as the old edge
    /// removed and the changed one added, which is the actual regression.
    /// </summary>
    private static string Diff(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');

        var removed = expectedLines.Except(actualLines, StringComparer.Ordinal).Take(40).ToList();
        var added = actualLines.Except(expectedLines, StringComparer.Ordinal).Take(40).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("removed:");
        foreach (var line in removed) sb.AppendLine("  - " + line);
        sb.AppendLine("added:");
        foreach (var line in added) sb.AppendLine("  + " + line);
        return sb.ToString();
    }
}
