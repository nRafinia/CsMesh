using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// `map` skipped sections with Fits()/Remaining and still returned Exit.Ok, so a map missing its
/// entrypoints reported a complete answer. Exit 0 is the one signal an agent branches on without
/// reading the payload; an incomplete answer must not carry it.
/// </summary>
public sealed class MapOverflowTests : IDisposable
{
    private readonly string _root;

    public MapOverflowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-map-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        File.WriteAllText(Path.Combine(_root, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        File.WriteAllText(Path.Combine(_root, "Code.cs"), """
            namespace Demo;

            public class A { public void Go() { } }
            public class B { public void Run() { new A().Go(); } }
            public class C { public void Run2() { new A().Go(); } }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    private Graph Build()
    {
        var graph = Indexer.Build(_root);
        graph.Freeze();
        return graph;
    }

    [Fact]
    public void A_map_that_drops_a_section_exits_two_and_names_it()
    {
        var w = new BudgetWriter(70, BudgetWriter.CompletionMarkerReserve);

        var exit = Queries.Map(Build(), null, w, []);

        Assert.Equal(Exit.OverBudget, exit);
        Assert.Contains(w.Lines, line =>
            line.Contains("INCOMPLETE", StringComparison.Ordinal) &&
            line.Contains("PROJECTS", StringComparison.Ordinal));
    }

    [Fact]
    public void A_map_that_fits_exits_zero_with_no_incomplete_marker()
    {
        var w = new BudgetWriter(4000);

        var exit = Queries.Map(Build(), null, w, []);

        Assert.Equal(Exit.Ok, exit);
        Assert.DoesNotContain(w.Lines, line => line.Contains("INCOMPLETE", StringComparison.Ordinal));
    }
}
