using System.Text;
using CsMesh.Analysis;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A member that matches only through its container is a real hit, but the container is what the
/// task names. Reach lifted the member above its own type -- the type has no callers, its members
/// have all of them -- so the type disappeared below its members. The row is now labelled
/// [container] and sorted below its container; a row that matched its own name keeps the
/// reach-weighted order, because it really is the name the caller typed.
/// </summary>
public sealed class WhereContainerRankTests : IDisposable
{
    private readonly string _root;

    public WhereContainerRankTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-container-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        var source = new StringBuilder();
        source.AppendLine("namespace Demo;");
        source.AppendLine("public static class Widget");
        source.AppendLine("{");
        foreach (var name in new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" })
            source.AppendLine($"    public static int {name}() => 1;");
        source.AppendLine("    public static int WidgetSeed() => 2;");
        source.AppendLine("}");

        // Four controller actions per called member. The controller tag makes each one an
        // entrypoint, and four entrypoints are enough reach to lift a container-only row above the
        // type before the fix -- so the test fails on the old behaviour rather than on a tie.
        for (var c = 1; c <= 4; c++)
        {
            source.AppendLine($"public class C{c}Controller");
            source.AppendLine("{");
            foreach (var name in new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" })
                source.AppendLine($"    public int {name}() => Widget.{name}();");
            source.AppendLine("}");
        }

        File.WriteAllText(Path.Combine(_root, "src", "Types.cs"), source.ToString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void A_container_only_match_is_labelled_and_sorted_below_its_container()
    {
        var graph = Indexer.Build(_root);
        graph.Freeze();

        var w = new BudgetWriter(4000);
        var exit = Queries.Where(graph, ["widget"], null, w, []);

        Assert.Equal(Exit.Ok, exit);

        var matches = w.Rows.Where(r => r.Relation == "match").ToList();
        Assert.NotEmpty(matches);

        // The type leads, however much reach its members collected.
        Assert.Equal("Widget", matches[0].Symbol);
        Assert.Equal("name", matches[0].Source);

        var members = matches
            .Where(r => r.Symbol!.StartsWith("Widget.", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(6, members.Count);
        Assert.Equal(5, members.Count(r => r.Source == "container"));

        // The member whose own leaf contains the term is not demoted: it matched its name.
        Assert.Single(members, r => r.Source == "name");
    }
}
