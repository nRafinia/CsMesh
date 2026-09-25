using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// ADR 0004 in code: hash ids, deterministic order, withheld-count reporting and the budget/exit
/// contract of the project level. The renderer is exercised directly so the golden text does not
/// depend on console capture; the budget path goes through the command's writer.
/// </summary>
[Collection("console-capture")]
public sealed class ExportTests : IDisposable
{
    private readonly string _root;

    public ExportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Lib() =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
        "<ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>";

    private Graph BuildAppReferencingLib()
    {
        Write("Lib/Lib.csproj", Lib());
        Write("Lib/Thing.cs", "namespace Lib { public class Thing { public void Go() { } } }");
        Write("App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            "<ImplicitUsings>enable</ImplicitUsings></PropertyGroup>" +
            "<ItemGroup><ProjectReference Include=\"../Lib/Lib.csproj\" /></ItemGroup></Project>");
        Write("App/Use.cs", "namespace App { public class Use { public void Run(Lib.Thing t) => t.Go(); } }");

        var graph = Indexer.Build(_root);
        graph.Freeze();
        return graph;
    }

    // ------------------------------------------------------------------ golden

    [Fact]
    public void Project_level_mermaid_renders_the_golden_text()
    {
        var graph = BuildAppReferencingLib();

        var result = Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "project", null, 1, "both", false, false));

        var text = string.Join("\n", result.Lines);

        // Ids are the first 8 hex of SHA-256 of the project path: SHA-256("App")=0d04bfeb,
        // SHA-256("Lib")=8b737b68. Nodes are ordered by that identity text.
        Assert.Equal(
            "flowchart LR\n" +
            "  p0d04bfeb[\"App\"]\n" +
            "  p8b737b68[\"Lib\"]\n" +
            "  p0d04bfeb --> p8b737b68",
            text);

        Assert.Equal(2, result.Nodes);
        Assert.Equal(1, result.Edges);
    }

    [Fact]
    public void Rendering_the_same_graph_twice_is_byte_identical()
    {
        var graph = BuildAppReferencingLib();

        var first = Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "project", null, 1, "both", false, false));
        var second = Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "project", null, 1, "both", false, false));

        Assert.Equal(string.Join("\n", first.Lines), string.Join("\n", second.Lines));
    }

    // ------------------------------------------------------------------ ids

    /// <summary>
    /// A collision must extend only the colliding ids, deterministically. Forced here with a hash
    /// stub rather than by finding a real SHA-256 collision, which is the point: the extension logic
    /// is what is under test.
    /// </summary>
    [Fact]
    public void A_colliding_id_extends_to_twelve_digits_and_leaves_the_others_alone()
    {
        static string Fake(string s) => s switch
        {
            "a" => "aabbccdd" + "a" + new string('0', 55),
            "b" => "aabbccdd" + "b" + new string('0', 55),
            "c" => "cccccccc" + new string('0', 56),
            _ => new string('0', 64)
        };

        var ids = Queries.AssignIds(["a", "b", "c"], Fake, "p");

        Assert.Equal("paabbccdda000", ids["a"]);
        Assert.Equal("paabbccddb000", ids["b"]);
        Assert.Equal("pcccccccc", ids["c"]);
    }

    // ------------------------------------------------------------------ withholding

    [Fact]
    public void Test_code_and_typeuse_are_withheld_with_their_counts()
    {
        var graph = new Graph
        {
            Root = "/tmp",
            Nodes =
            [
                new Node { Id = 0, Name = "A.T", Short = "T", Kind = "type", Project = "A", Key = "a|T|type" },
                new Node { Id = 1, Name = "A.T.M", Short = "T.M", Kind = "method", Project = "A", Key = "a|T.M()|method" },
                new Node { Id = 2, Name = "B.U", Short = "U", Kind = "type", Project = "B", Key = "b|U|type" },
                new Node { Id = 3, Name = "B.TT", Short = "TT", Kind = "type", Project = "B", Tags = ["test"], Key = "b|TT|type" }
            ],
            Edges =
            [
                new Edge { From = 1, To = 0, Kind = EdgeKind.TypeUse },
                new Edge { From = 1, To = 2, Kind = EdgeKind.Call },
                new Edge { From = 3, To = 2, Kind = EdgeKind.Call },
                new Edge { From = 1, To = 3, Kind = EdgeKind.Call }
            ]
        };
        graph.Freeze();

        var result = Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "project", null, 1, "both", false, false));

        Assert.Equal(2, result.Nodes);
        Assert.Equal(1, result.Edges);
        Assert.Equal(1, result.TestNodesWithheld);
        Assert.Equal(2, result.TestEdgesWithheld);
        Assert.Equal(1, result.TypeUseEdgesWithheld);
    }

    // ------------------------------------------------------------------ budget and exits

    [Fact]
    public void Overflow_exits_two_and_names_both_remedies()
    {
        var lines = Enumerable.Range(0, 80).Select(i => $"  p{i} --> q{i}").ToList();
        var result = new Queries.ExportResult(lines, 80, 80, 0, 0, 0, 0, 0);

        var exit = ExportCommand.Write(result, budget: 200, level: "project", out var writer);
        var text = string.Join("\n", writer.Lines);

        Assert.Equal(Exit.OverBudget, exit);
        Assert.Contains("INCOMPLETE", text);
        Assert.Contains("--out", text);
        Assert.Contains("--level", text);
    }

    [Fact]
    public void The_default_budget_is_the_one_the_docs_promise()
    {
        Assert.Equal(1500, ExportCommand.DefaultBudget);
        Assert.Contains("default: 1500", HelpCommand.ExportHelp, StringComparison.Ordinal);
    }

    [Fact]
    public void No_index_exits_four()
    {
        var exit = ExportCommand.Execute(_root, new Options([]));

        Assert.Equal(Exit.NoIndex, exit);
    }

    [Theory]
    [InlineData("--format", "svg")]
    [InlineData("--level", "bogus")]
    [InlineData("--direction", "sideways")]
    public void A_bad_option_exits_sixty_four(string flag, string value)
    {
        var exit = ExportCommand.Execute(_root, new Options([flag, value]));

        Assert.Equal(Exit.Usage, exit);
    }
}
