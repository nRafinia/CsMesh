using CsMesh.Analysis;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// where ranks by a composite of lexical strength and reach, so an exact name with nothing calling
/// it can sit below a substring match that routes do reach. That is surprising enough to state in
/// the output, and this pins the sentence. Revert the line and this fails.
/// </summary>
public sealed class WhereBoundaryTests : IDisposable
{
    private readonly string _root;

    public WhereBoundaryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-where-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        File.WriteAllText(Path.Combine(_root, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        File.WriteAllText(Path.Combine(_root, "Code.cs"), """
            namespace Demo;

            public class DiscountRequest { public int Id { get; set; } }

            public class DiscountService { public void Apply() { } }

            public class Caller { public void Run() { new DiscountService().Apply(); } }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void Where_states_that_ranking_is_reach_weighted_not_exact_name_first()
    {
        var graph = Indexer.Build(_root);
        graph.Freeze();

        var w = new BudgetWriter(4000);
        var exit = Queries.Where(graph, ["discount"], null, w, []);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains(w.Lines, line =>
            line.Contains("reach-weighted, not exact-name-first", StringComparison.Ordinal));
    }
}
