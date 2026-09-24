using CsMesh.Analysis;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// --unranked is the escape hatch from a ranking the caller cannot verify: reach is derived from
/// what the indexer bound, so a re-index can reorder a list that was read as stable. It lists every
/// match in Node.Key order and drops both products of ranking -- the reach column and the
/// next-command hint. Revert any of that and these fail.
/// </summary>
public sealed class WhereUnrankedTests : IDisposable
{
    private readonly string _root;

    public WhereUnrankedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-unranked-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        File.WriteAllText(Path.Combine(_root, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        // ZetaService is the one a reach ranking lifts to the top; AlphaService is the one Node.Key
        // order puts first. The two orders disagree, which is what makes the assertions meaningful.
        File.WriteAllText(Path.Combine(_root, "Code.cs"), """
            namespace Demo;

            public class AlphaService { public void Handle() { } }

            public class ZetaService { public void Run() { } }

            public class Caller { public void Go() { new ZetaService().Run(); } }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void Unranked_lists_every_match_in_key_order_without_reach_or_a_next_hint()
    {
        var graph = Indexer.Build(_root);
        graph.Freeze();

        var w = new BudgetWriter(4000);
        var exit = Queries.Where(graph, ["service"], null, w, [], unranked: true);

        Assert.Equal(Exit.Ok, exit);

        var text = string.Join("\n", w.Lines);
        Assert.Contains("unranked", text, StringComparison.Ordinal);
        Assert.DoesNotContain("reach-weighted", text, StringComparison.Ordinal);
        Assert.DoesNotContain("entrypoint(s)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("next:", text, StringComparison.Ordinal);

        // Every match, in ordinal Node.Key order -- computed from the graph so the assertion cannot
        // drift with the Key format.
        var expected = graph.Nodes
            .Where(n => n.Kind != "enum-member" && n.Short.Contains("service", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Key, StringComparer.Ordinal)
            .Select(n => n.Short)
            .ToList();

        Assert.Equal(expected, MatchSymbols(text));
    }

    /// <summary>
    /// Unranked is still a bounded answer: overflowing the budget is exit 2 with the completion
    /// marker, exactly as the ranked path, rather than an unbounded dump.
    /// </summary>
    [Fact]
    public void Unranked_overflow_is_exit_two_with_a_completion_marker()
    {
        var graph = Indexer.Build(_root);
        graph.Freeze();

        var w = new BudgetWriter(20);
        var exit = Queries.Where(graph, ["service"], null, w, [], unranked: true);

        Assert.Equal(Exit.OverBudget, exit);
        Assert.True(w.Overflowed);
        Assert.Contains(w.Lines, line => line.StartsWith("INCOMPLETE", StringComparison.Ordinal));
    }

    private static List<string> MatchSymbols(string text) => text
        .Replace("\r\n", "\n")
        .Split('\n')
        .Where(line => line.StartsWith("  ", StringComparison.Ordinal) && line.Contains("  [", StringComparison.Ordinal))
        .Select(line =>
        {
            var body = line[2..];
            var cut = body.IndexOf("  ", StringComparison.Ordinal);
            return cut < 0 ? body : body[..cut];
        })
        .ToList();
}
