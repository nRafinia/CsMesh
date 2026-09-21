using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// One compilation per project means two assemblies can declare the same fully-qualified name.
/// Commit E puts the declaring assembly into Node.Key and RequestKey, so the two stay separate
/// nodes, separate dispatch entries and separate implementor tables. Without it they merged, and a
/// trace or an 'impl' silently answered from the wrong project.
///
/// Console.Out is process-global, so this joins the console-capture collection.
/// </summary>
[Collection("console-capture")]
public sealed class AssemblyQualifiedKeysTests : IDisposable
{
    private readonly string _root;

    public AssemblyQualifiedKeysTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-keys-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string Lib(string extra = "") =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
        "<ImplicitUsings>enable</ImplicitUsings></PropertyGroup>" + extra + "</Project>";

    private static string Exe(string extra = "") =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType>" +
        "<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>" +
        "</PropertyGroup>" + extra + "</Project>";

    private static string ProjectReference(string relative) =>
        $"<ItemGroup><ProjectReference Include=\"{relative}\" /></ItemGroup>";

    private static string Capture(Func<int> run)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            run();
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }

    private static string AssemblyOf(string key)
    {
        var bar = key.IndexOf('|');
        return bar < 0 ? "" : key[..bar];
    }

    private void WriteTwoPrograms()
    {
        Write("A/A.csproj", Exe());
        Write("A/Program.cs", "System.Console.WriteLine(1);");
        Write("B/B.csproj", Exe());
        Write("B/Program.cs", "System.Console.WriteLine(2);");
    }

    [Fact]
    public void Two_top_level_programs_are_two_nodes_and_exit_three_lists_both()
    {
        WriteTwoPrograms();

        var graph = Indexer.Build(_root);
        var programs = graph.Nodes
            .Where(n => n.Kind == "method" && n.Short.Contains("top-level", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, programs.Count);
        Assert.Equal(2, programs.Select(n => n.Key).Distinct(StringComparer.Ordinal).Count());

        // Even a synthesized node carries its compilation's assembly, and its Project is that
        // compilation's project rather than the file's nearest one.
        Assert.All(programs, p => Assert.Equal(p.Project, AssemblyOf(p.Key)));

        GraphStore.Save(graph);

        var exit = 0;
        var text = Capture(() => exit = QueryCommand.Execute(_root, new Options(["Program"]), "trace"));

        Assert.Equal(Exit.Ambiguous, exit);
        Assert.Contains("ambiguous", text, StringComparison.OrdinalIgnoreCase);

        // Both candidates and the project each one lives in, so the caller can paste --project.
        var separator = Path.DirectorySeparatorChar;
        Assert.True(text.Contains($"A  A{separator}Program.cs", StringComparison.Ordinal), text);
        Assert.True(text.Contains($"B  B{separator}Program.cs", StringComparison.Ordinal), text);
        Assert.True(text.Contains("--project", StringComparison.Ordinal), text);
    }

    [Fact]
    public void The_project_filter_picks_one_of_two_same_named_candidates()
    {
        WriteTwoPrograms();
        GraphStore.Save(Indexer.Build(_root));

        var exit = 0;
        var text = Capture(() =>
            exit = QueryCommand.Execute(_root, new Options(["Program", "--project", "A"]), "trace"));

        Assert.Equal(Exit.Ok, exit);
        Assert.DoesNotContain("ambiguous", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_same_fqn_in_two_projects_gets_separate_implementors()
    {
        const string source =
            "namespace Demo { public interface IThing { void Do(); } " +
            "public sealed class Thing : IThing { public void Do() { } } }";

        Write("A/A.csproj", Lib());
        Write("A/Thing.cs", source);
        Write("B/B.csproj", Lib());
        Write("B/Thing.cs", source);

        var graph = Indexer.Build(_root);
        graph.Freeze();

        var interfaces = graph.Nodes
            .Where(n => n.Name == "Demo.IThing" && n.Kind == "interface")
            .ToList();

        Assert.Equal(2, interfaces.Count);
        Assert.Equal(2, interfaces.Select(n => AssemblyOf(n.Key)).Distinct(StringComparer.Ordinal).Count());

        foreach (var iface in interfaces)
        {
            var implementations = graph.Out(iface.Id)
                .Where(e => e.Kind == EdgeKind.Interface)
                .Select(e => graph.ById(e.To)!)
                .ToList();

            // Keyed by short name, both interfaces shared one bucket and this list held both
            // implementors: an interface edge that pointed into the other assembly.
            var implementation = Assert.Single(implementations);
            Assert.Equal(AssemblyOf(iface.Key), AssemblyOf(implementation.Key));
            Assert.Equal(iface.Project, implementation.Project);
        }
    }

    [Fact]
    public void A_cross_project_handler_resolves_and_the_request_key_carries_its_assembly()
    {
        Write("Abstractions/Abstractions.csproj", Lib());
        Write("Abstractions/Abs.cs", """
            namespace Abs
            {
                public interface IMediator { void Send<T>(T command); }
                public interface ICommandHandler<T> { }
                public sealed record CreateOrder(int Id);
            }
            """);

        Write("Handler/Handler.csproj", Lib(ProjectReference("../Abstractions/Abstractions.csproj")));
        Write("Handler/H.cs", """
            namespace Handler
            {
                public sealed class CreateOrderHandler : Abs.ICommandHandler<Abs.CreateOrder>
                {
                    public void Handle(Abs.CreateOrder command) { }
                }
            }
            """);

        Write("Api/Api.csproj", Lib(ProjectReference("../Abstractions/Abstractions.csproj")
            + ProjectReference("../Handler/Handler.csproj")));
        Write("Api/A.cs", """
            namespace Api
            {
                public class Caller
                {
                    private readonly Abs.IMediator _mediator = null!;
                    public void Go() => _mediator.Send(new Abs.CreateOrder(1));
                }
            }
            """);

        var graph = Indexer.Build(_root);
        graph.Freeze();

        // The request lives in Abstractions, so its key must name that assembly whether it is seen
        // from its own compilation or through the handler's CompilationReference.
        Assert.Contains(graph.HandlersByRequest.Keys,
            k => k.StartsWith("Abstractions|", StringComparison.Ordinal) && k.Contains("CreateOrder"));

        Assert.Contains(graph.Edges, e =>
            e.Kind == EdgeKind.Mediatr &&
            graph.ById(e.From)?.Short == "Caller.Go" &&
            graph.ById(e.To)?.Short == "CreateOrderHandler.Handle");
    }

    [Fact]
    public void A_v12_graph_is_rejected_and_a_full_index_replaces_it()
    {
        Write("A/A.csproj", Lib());
        Write("A/T.cs", "namespace A { public sealed class T { } }");

        var stale = Indexer.Build(_root);
        stale.FormatVersion = 12;
        GraphStore.Save(stale);

        Assert.Null(GraphStore.Load(_root, out var problem));
        Assert.Contains("v12", problem!, StringComparison.Ordinal);

        Capture(() => IndexCommand.Execute(_root, new Options(["--full"])));

        var reloaded = GraphStore.Load(_root, out _);
        Assert.NotNull(reloaded);
        Assert.Equal(Graph.CurrentFormatVersion, reloaded!.FormatVersion);
    }
}
