using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
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
        Write("App/Use.cs",
            "namespace App { public class Use { public void Run(Lib.Thing t) => t.Go(); } " +
            "public class Use2 { public void Run() { } } }");

        var graph = Indexer.Build(_root);
        graph.Freeze();
        GraphStore.Save(graph);
        return graph;
    }

    private static (int Exit, string Output) Capture(Func<int> run)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            return (run(), buffer.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
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
    public void Namespace_level_groups_a_member_under_its_owning_types_namespace()
    {
        var graph = BuildAppReferencingLib();

        var result = Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "namespace", null, 1, "both", false, false));

        Assert.Equal(
            "flowchart LR\n" +
            "  ns0d04bfeb[\"App\"]\n" +
            "  ns8b737b68[\"Lib\"]\n" +
            "  ns0d04bfeb --> ns8b737b68",
            string.Join("\n", result.Lines));
    }

    [Fact]
    public void A_node_with_no_namespace_lands_in_the_global_bucket_not_a_guessed_one()
    {
        var graph = new Graph
        {
            Root = "/tmp",
            Nodes =
            [
                new Node { Id = 0, Name = "Real.Ns.T", Short = "T", Kind = "type", Project = "P", Key = "k0" },
                new Node { Id = 1, Name = "Probe.Ns.X", Short = "x", Kind = "field", Project = "P", Key = "k1" }
            ],
            Edges = [new Edge { From = 1, To = 0, Kind = EdgeKind.TypeUse }]
        };
        graph.Freeze();

        var result = Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "namespace", null, 1, "both", false, true));
        var text = string.Join("\n", result.Lines);

        // The field has no owning type, so its display-name prefix is not a namespace. It lands in
        // the global bucket; "Probe.Ns" is a member of the type path, not a namespace.
        Assert.Equal(2, result.Nodes);
        Assert.Contains("[\"Real.Ns\"]", text);
        Assert.Contains("[\"(global)\"]", text);
        Assert.DoesNotContain("Probe.Ns", text);
        Assert.DoesNotContain("[\"\"]", text);
    }

    [Fact]
    public void The_empty_bucket_is_named_in_both_formats()
    {
        var graph = new Graph
        {
            Root = "/tmp",
            Nodes =
            [
                new Node { Id = 0, Name = "A.T", Short = "T", Kind = "type", Project = "", Key = "k0" },
                // A real type in the global namespace, not a synthetic one.
                new Node { Id = 1, Name = "GlobalThing", Short = "GlobalThing", Kind = "type", Project = "", Key = "k1" }
            ]
        };
        graph.Freeze();

        var projectMermaid = string.Join("\n", Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "project", null, 1, "both", false, false)).Lines);
        var projectDot = string.Join("\n", Queries.RenderExport(
            graph, new Queries.ExportRequest("dot", "project", null, 1, "both", false, false)).Lines);
        var namespaceMermaid = string.Join("\n", Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "namespace", null, 1, "both", false, false)).Lines);
        var namespaceDot = string.Join("\n", Queries.RenderExport(
            graph, new Queries.ExportRequest("dot", "namespace", null, 1, "both", false, false)).Lines);

        Assert.Contains("[\"(no project)\"]", projectMermaid);
        Assert.Contains("label=\"(no project)\"", projectDot);
        Assert.Contains("[\"(global)\"]", namespaceMermaid);
        Assert.Contains("label=\"(global)\"", namespaceDot);
        Assert.DoesNotContain("[\"\"]", projectMermaid + namespaceMermaid);
    }

    [Fact]
    public void Project_level_dot_renders_the_golden_text()
    {
        var graph = BuildAppReferencingLib();

        var result = Queries.RenderExport(
            graph, new Queries.ExportRequest("dot", "project", null, 1, "both", false, false));

        Assert.Equal(
            "digraph G {\n" +
            "  \"p0d04bfeb\" [label=\"App\"];\n" +
            "  \"p8b737b68\" [label=\"Lib\"];\n" +
            "  \"p0d04bfeb\" -> \"p8b737b68\";\n" +
            "}",
            string.Join("\n", result.Lines));
    }

    [Fact]
    public void Dot_escapes_quotes_and_backslashes_in_ids_and_labels()
    {
        var graph = new Graph
        {
            Root = "/tmp",
            Nodes = [new Node { Id = 0, Name = "T", Short = "T", Kind = "type", Project = "a\"b\\c", Key = "k|T|type" }]
        };
        graph.Freeze();

        var result = Queries.RenderExport(
            graph, new Queries.ExportRequest("dot", "project", null, 1, "both", false, false));
        var text = string.Join("\n", result.Lines);

        Assert.Contains("label=\"a\\\"b\\\\c\"", text);
    }

    [Fact]
    public void Mermaid_renders_a_quote_as_the_quot_entity()
    {
        Assert.Equal("a#quot;b", Queries.EscapeMermaid("a\"b"));
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

    [Fact]
    public void A_namespace_bucket_comes_from_the_name_not_the_ownership_edge()
    {
        var graph = new Graph
        {
            Root = "/tmp",
            Nodes =
            [
                new Node { Id = 0, Name = "Real.Ns.Dog", Short = "Dog", Kind = "type", Project = "P", Key = "k0" },
                new Node { Id = 1, Name = "Other.Ns.IAnimal", Short = "IAnimal", Kind = "interface", Project = "P", Key = "k1" },
                new Node { Id = 2, Name = "Real.Ns.Dog.Bark", Short = "Dog.Bark", Kind = "method", Project = "P", Key = "k2" }
            ],
            // A TypeUse ownership edge from the wrong type must not relocate the member.
            Edges = [new Edge { From = 1, To = 2, Kind = EdgeKind.TypeUse, Note = "member" }]
        };
        graph.Freeze();

        var of = Queries.NamespaceResolver(graph);

        Assert.Equal("Real.Ns", of(graph.Nodes[0]));
        Assert.Equal("Other.Ns", of(graph.Nodes[1]));
        Assert.Equal("Real.Ns", of(graph.Nodes[2]));
    }

    [Fact]
    public void A_same_bucket_edge_is_withheld_as_internal_at_bucket_levels_only()
    {
        var graph = new Graph
        {
            Root = "/tmp",
            Nodes =
            [
                new Node { Id = 0, Name = "A.T", Short = "T", Kind = "type", Project = "P", Key = "k0" },
                new Node { Id = 1, Name = "A.U", Short = "U", Kind = "type", Project = "P", Key = "k1" },
                new Node { Id = 2, Name = "B.V", Short = "V", Kind = "type", Project = "Q", Key = "k2" }
            ],
            Edges =
            [
                new Edge { From = 0, To = 1, Kind = EdgeKind.Call },
                new Edge { From = 0, To = 2, Kind = EdgeKind.Call }
            ]
        };
        graph.Freeze();

        var project = Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "project", null, 1, "both", false, false));
        var ns = Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "namespace", null, 1, "both", false, false));
        var neighbourhood = Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "neighbourhood", graph.Nodes[0], 1, "both", false, false));

        Assert.Equal(1, project.Edges);
        Assert.Equal(1, project.InternalEdgesWithheld);
        Assert.Equal(1, ns.Edges);
        Assert.Equal(1, ns.InternalEdgesWithheld);
        Assert.Equal(2, neighbourhood.Edges);
        Assert.Equal(0, neighbourhood.InternalEdgesWithheld);
    }

    // ------------------------------------------------------------------ edge kinds

    [Fact]
    public void Non_call_edge_kinds_are_labelled()
    {
        var graph = new Graph
        {
            Root = "/tmp",
            Nodes =
            [
                new Node { Id = 0, Name = "P.A", Short = "A", Kind = "type", Project = "P", Key = "p|A|type" },
                new Node { Id = 1, Name = "Q.B", Short = "B", Kind = "type", Project = "Q", Key = "q|B|type" }
            ],
            Edges = Enum.GetValues<EdgeKind>()
                .Select(k => new Edge { From = 0, To = 1, Kind = k })
                .ToList()
        };
        graph.Freeze();

        var mermaid = string.Join("\n", Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "project", null, 1, "both", false, true)).Lines);
        var dot = string.Join("\n", Queries.RenderExport(
            graph, new Queries.ExportRequest("dot", "project", null, 1, "both", false, true)).Lines);

        foreach (var label in new[] { "iface", "override", "mediatr", "di", "construct", "typeuse", "route" })
        {
            Assert.Contains($"|{label}|", mermaid);
            Assert.Contains($"label=\"{label}\"", dot);
        }

        // A Call edge is the ordinary arrow and must not gain a kind label.
        Assert.Contains("-->", mermaid);
        Assert.DoesNotContain("|call|", mermaid);
    }

    // ------------------------------------------------------------------ neighbourhood

    private static Graph Chain()
    {
        var graph = new Graph
        {
            Root = "/tmp",
            Nodes =
            [
                new Node { Id = 0, Name = "N.A", Short = "A", Kind = "type", Project = "P", Key = "n|A|type" },
                new Node { Id = 1, Name = "N.B", Short = "B", Kind = "method", Project = "P", Key = "n|B()|method" },
                new Node { Id = 2, Name = "N.C", Short = "C", Kind = "method", Project = "P", Key = "n|C()|method" },
                new Node { Id = 3, Name = "N.D", Short = "D", Kind = "method", Project = "P", Key = "n|D()|method" }
            ],
            Edges =
            [
                new Edge { From = 1, To = 2, Kind = EdgeKind.Call },
                new Edge { From = 2, To = 3, Kind = EdgeKind.Call }
            ]
        };
        graph.Freeze();
        return graph;
    }

    [Fact]
    public void Neighbourhood_depth_limits_the_ring_and_reports_what_lies_beyond_it()
    {
        var graph = Chain();
        var start = graph.Nodes.First(n => n.Name == "N.B");

        var one = Queries.RenderExport(graph,
            new Queries.ExportRequest("mermaid", "neighbourhood", start, 1, "both", false, false));
        var two = Queries.RenderExport(graph,
            new Queries.ExportRequest("mermaid", "neighbourhood", start, 2, "both", false, false));

        Assert.Equal(2, one.Nodes);
        Assert.Equal(1, one.Edges);
        Assert.Equal(1, one.NodesBeyondDepth);
        Assert.Equal(1, one.EdgesBeyondDepth);

        Assert.Equal(3, two.Nodes);
        Assert.Equal(2, two.Edges);
        Assert.Equal(0, two.NodesBeyondDepth);
    }

    [Fact]
    public void Neighbourhood_direction_in_excludes_the_outgoing_side()
    {
        var graph = Chain();
        var start = graph.Nodes.First(n => n.Name == "N.B");

        var incoming = Queries.RenderExport(graph,
            new Queries.ExportRequest("mermaid", "neighbourhood", start, 3, "in", false, false));
        var outgoing = Queries.RenderExport(graph,
            new Queries.ExportRequest("mermaid", "neighbourhood", start, 1, "out", false, false));

        Assert.Equal(1, incoming.Nodes);
        Assert.Equal(0, incoming.Edges);
        Assert.Equal(2, outgoing.Nodes);
        Assert.Equal(1, outgoing.Edges);
    }

    [Fact]
    public void A_selector_resolves_one_overload_for_the_neighbourhood_start()
    {
        BuildAppReferencingLib();

        var (exit, output) = Capture(() => ExportCommand.Execute(_root, new Options(["App.Use2.Run()"])));

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("[\"Use2.Run\"]", output);
    }

    [Fact]
    public void An_unknown_symbol_exits_one()
    {
        BuildAppReferencingLib();

        var (exit, _) = Capture(() => ExportCommand.Execute(_root, new Options(["No.Such.Thing"])));

        Assert.Equal(Exit.NotFound, exit);
    }

    [Fact]
    public void An_ambiguous_symbol_exits_three()
    {
        BuildAppReferencingLib();

        var (exit, _) = Capture(() => ExportCommand.Execute(_root, new Options(["Run"])));

        Assert.Equal(Exit.Ambiguous, exit);
    }

    // ------------------------------------------------------------------ synthetic

    [Fact]
    public void Synthetic_tuple_and_anonymous_nodes_are_withheld()
    {
        Write("Fixture.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            "<ImplicitUsings>enable</ImplicitUsings><Nullable>disable</Nullable></PropertyGroup></Project>");
        Write("Code.cs", """
            public class GlobalThing
            {
                public int Value() => 1;
            }

            namespace Real.Ns
            {
                public class Holder
                {
                    public (int a, int b) Pair = (1, 2);

                    public int Sum() => Pair.a + Pair.b;

                    public object Make() => new { Id = 1, Name = "x" };

                    public string Describe() => Make().ToString();
                }
            }
            """);

        var graph = Indexer.Build(_root);
        graph.Freeze();

        var result = Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "namespace", null, 1, "both", false, false));
        var text = string.Join("\n", result.Lines);

        Assert.True(result.SyntheticNodesWithheld >= 1, "the anonymous type must contribute a node");
        Assert.DoesNotContain("<anonymous", text);
        // A real global-namespace type keeps the global bucket alive.
        Assert.Contains("(global)", text);
    }

    [Fact]
    public void Withholding_synthetic_nodes_empties_the_no_project_bucket()
    {
        var graph = new Graph
        {
            Root = "/tmp",
            Nodes =
            [
                // A synthetic node has no project and no namespace.
                new Node { Id = 0, Name = "(int a, int b).Item1", Short = "Item1", Kind = "field", Project = "", Key = "k0" },
                // A real type in the global namespace, owned by project P.
                new Node { Id = 1, Name = "GlobalThing", Short = "GlobalThing", Kind = "type", Project = "P", Key = "k1" }
            ]
        };
        graph.Freeze();

        var project = string.Join("\n", Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "project", null, 1, "both", false, false)).Lines);
        var ns = Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "namespace", null, 1, "both", false, false));
        var nsText = string.Join("\n", ns.Lines);

        Assert.Equal(1, ns.SyntheticNodesWithheld);
        Assert.DoesNotContain("(no project)", project);
        // The real global-namespace type keeps the global bucket; the synthetic node is gone.
        Assert.Contains("(global)", nsText);
        Assert.DoesNotContain("(int a, int b)", nsText);
    }

    // ------------------------------------------------------------------ external validation

    private IEnumerable<(string Level, string Format, IReadOnlyList<string> Lines)> FixtureRenders()
    {
        var graph = BuildAppReferencingLib();
        var start = graph.Nodes.First(n => n.Name == "App.Use.Run");

        foreach (var level in new[] { "project", "namespace", "neighbourhood" })
        foreach (var format in new[] { "mermaid", "dot" })
        {
            var result = Queries.RenderExport(graph, new Queries.ExportRequest(
                format, level, level == "neighbourhood" ? start : null, 1, "both", false, false));
            yield return (level, format, result.Lines);
        }
    }

    [RequiresGraphviz]
    public void Graphviz_accepts_every_level_and_format()
    {
        foreach (var (level, format, lines) in FixtureRenders().Where(x => x.Format == "dot"))
        {
            var input = Path.Combine(_root, $"{level}.dot");
            var output = Path.Combine(_root, $"{level}.svg");
            File.WriteAllLines(input, lines);

            Assert.True(ExternalTool.TryRun("dot", $"-Tsvg \"{input}\" -o \"{output}\"", out var log),
                $"{level}/dot: {log}");
            Assert.True(File.Exists(output));
        }
    }

    [RequiresMermaidCli]
    public void Mermaid_cli_accepts_every_level()
    {
        foreach (var (level, format, lines) in FixtureRenders().Where(x => x.Format == "mermaid"))
        {
            var input = Path.Combine(_root, $"{level}.mmd");
            var output = Path.Combine(_root, $"{level}.mermaid.svg");
            File.WriteAllLines(input, lines);

            Assert.True(ExternalTool.TryRun("mmdc", $"-i \"{input}\" -o \"{output}\"", out var log),
                $"{level}/mermaid: {log}");
            Assert.True(File.Exists(output));
        }
    }

    // ------------------------------------------------------------------ --out

    [Fact]
    public void Out_writes_the_whole_render_and_prints_only_a_summary()
    {
        BuildAppReferencingLib();

        var (exit, output) = Capture(() => ExportCommand.Execute(_root,
            new Options(["--level", "project", "--out", "diagram.mmd"])));

        Assert.Equal(Exit.Ok, exit);

        var file = Path.Combine(_root, "diagram.mmd");
        Assert.True(File.Exists(file));
        Assert.StartsWith("flowchart LR", File.ReadAllText(file));

        Assert.Contains("project mermaid -> diagram.mmd", output);
        Assert.Contains("nodes: 2, edges: 1", output);
        Assert.Contains("withheld:", output);
        Assert.Contains("TypeUse", output);
    }

    [Fact]
    public void Out_cannot_escape_the_repository_root_through_dot_dot()
    {
        BuildAppReferencingLib();
        var name = "csmesh-escaped-" + Guid.NewGuid().ToString("N")[..8] + ".mmd";
        var outside = Path.Combine(Directory.GetParent(_root)!.FullName, name);

        var (exit, _) = Capture(() => ExportCommand.Execute(_root,
            new Options(["--out", Path.Combine("..", name)])));

        Assert.Equal(Exit.Usage, exit);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void Out_cannot_escape_the_repository_root_to_an_absolute_path()
    {
        BuildAppReferencingLib();
        var name = "csmesh-escaped-" + Guid.NewGuid().ToString("N")[..8] + ".mmd";
        var outside = Path.Combine(Path.GetTempPath(), name);

        var (exit, _) = Capture(() => ExportCommand.Execute(_root,
            new Options(["--out", outside])));

        Assert.Equal(Exit.Usage, exit);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void Out_cannot_write_into_a_sibling_whose_name_starts_with_the_root_name()
    {
        BuildAppReferencingLib();
        var sibling = _root + "2";
        Directory.CreateDirectory(sibling);
        try
        {
            var (exit, _) = Capture(() => ExportCommand.Execute(_root,
                new Options(["--out", Path.Combine(sibling, "x.mmd")])));

            Assert.Equal(Exit.Usage, exit);
            Assert.False(File.Exists(Path.Combine(sibling, "x.mmd")));
        }
        finally
        {
            try { Directory.Delete(sibling, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public void Out_with_a_missing_parent_is_a_usage_error()
    {
        BuildAppReferencingLib();

        var (exit, _) = Capture(() => ExportCommand.Execute(_root,
            new Options(["--out", "no-such-directory/diagram.mmd"])));

        Assert.Equal(Exit.Usage, exit);
    }

    [Fact]
    public void Out_write_failure_is_internal()
    {
        // Windows will not rename over a destination another handle holds without share-delete;
        // Unix rename() does not consult handles, so the failure is a Windows-only path.
        if (!OperatingSystem.IsWindows()) return;

        BuildAppReferencingLib();
        var target = Path.Combine(_root, "held.mmd");

        using (new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var (exit, _) = Capture(() => ExportCommand.Execute(_root, new Options(["--out", "held.mmd"])));
            Assert.Equal(Exit.Internal, exit);
        }
    }

    [Fact]
    public void A_whole_test_project_is_withheld_even_when_a_helper_is_untagged()
    {
        var graph = new Graph
        {
            Root = "/tmp",
            Nodes =
            [
                // A helper in a test project whose name carries no test convention and which has no
                // test attribute: the tag heuristic misses it, so it is not tagged "test".
                new Node { Id = 0, Name = "Tns.Helper", Short = "Helper", Kind = "type", Project = "T", Key = "k0" },
                new Node { Id = 1, Name = "Tns.FooTests", Short = "FooTests", Kind = "type", Project = "T", Tags = ["test"], Key = "k1" },
                new Node { Id = 2, Name = "Tns.FooTests.M", Short = "FooTests.M", Kind = "method", Project = "T", Tags = ["test"], Key = "k2" },
                new Node { Id = 3, Name = "Tns.FooTests.N", Short = "FooTests.N", Kind = "method", Project = "T", Tags = ["test"], Key = "k3" },
                new Node { Id = 4, Name = "Pns.Prod", Short = "Prod", Kind = "type", Project = "P", Key = "k4" }
            ]
        };
        graph.Freeze();

        var result = Queries.RenderExport(
            graph, new Queries.ExportRequest("mermaid", "namespace", null, 1, "both", false, false));
        var text = string.Join("\n", result.Lines);

        // The project is mostly tagged test, so its untagged helper is test code too and no test
        // namespace bucket survives.
        Assert.Equal(1, result.Nodes);
        Assert.Equal(4, result.TestNodesWithheld);
        Assert.Contains("Pns", text);
        Assert.DoesNotContain("Tns", text);
    }

    // ------------------------------------------------------------------ namespace rule

    /// <summary>
    /// The namespace level buckets by the outermost containing type's namespace, never by a type
    /// name: a namespace no symbol declares is not drawn; a nested type and its members land in the
    /// outer namespace; an interface member lands under its interface; and a node whose declaring
    /// type cannot be determined (a tuple-typed synthetic node) has no namespace rather than a
    /// bucket invented from its display name.
    /// </summary>
    [Fact]
    public void The_namespace_level_counts_declared_buckets_not_name_prefixes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Ns.cs"), """
            namespace A.B.C
            {
                public class Outer
                {
                    public class Inner { public void Nested() { } }
                    public void Take((int major, int minor) v) { }
                }

                public interface IThing { void Do(); }
                public class Thing : IThing { public void Do() { } }
            }
            """);

        var graph = Indexer.Build(_root);

        // The indexer produces synthetic nodes for tuple/anonymous signatures whose display name is
        // not a qualified name; one is appended here because the bare fixture does not compile far
        // enough to emit one.
        graph.Nodes.Add(new Node
        {
            Id = graph.Nodes.Count,
            Name = "(System.Type Type, string Property)",
            Short = "Property",
            Kind = "field",
            Project = "",
            Key = "synthetic|tuple|field"
        });
        graph.InvalidateLookups();
        graph.Freeze();

        var of = Queries.NamespaceResolver(graph);

        Node Find(string name) => graph.Nodes.First(n => n.Name == name);

        // A declared namespace is a bucket.
        Assert.Equal("A.B.C", of(Find("A.B.C.Outer")));
        // A nested type lands in the outer namespace; no type name is ever a bucket.
        Assert.Equal("A.B.C", of(Find("A.B.C.Outer.Inner")));
        Assert.Equal("A.B.C", of(Find("A.B.C.Outer.Inner.Nested")));
        Assert.DoesNotContain(graph.Nodes.Select(of), ns => ns == "A.B.C.Outer");
        // An interface member lands under its interface's namespace.
        Assert.Equal("A.B.C", of(Find("A.B.C.IThing.Do")));
        // A synthetic node is not given a namespace invented from its display name.
        Assert.Equal("", of(Find("(System.Type Type, string Property)")));
        // No parent prefix is drawn.
        Assert.DoesNotContain(graph.Nodes.Select(of), ns => ns is "A" or "A.B");
    }

    // ------------------------------------------------------------------ budget and exits

    [Fact]
    public void Overflow_exits_two_and_names_both_remedies()
    {
        var lines = Enumerable.Range(0, 80).Select(i => $"  p{i} --> q{i}").ToList();
        var result = new Queries.ExportResult(lines, 80, 80, 0, 0, 0, 0, 0, 0, 0, 0);

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
