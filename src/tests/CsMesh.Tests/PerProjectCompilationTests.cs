using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The whole-solution compilation let one project's global using, name or assembly boundary reach
/// every other project. These pin the per-project split: a project sees only what it references and
/// only its own usings, and the split's compilations are ordered, referenced and reported.
///
/// Console is captured for the cycle report, so this joins the console-capture collection.
/// </summary>
[Collection("console-capture")]
public sealed class PerProjectCompilationTests : IDisposable
{
    private readonly string _root;

    public PerProjectCompilationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-split-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static bool HasEdge(Graph graph, string fromName, EdgeKind kind, string toName) =>
        graph.Edges.Any(e => graph.ById(e.From)?.Name == fromName && e.Kind == kind && graph.ById(e.To)?.Name == toName);

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

    [Fact]
    public void A_global_using_in_one_project_does_not_collide_with_another_projects()
    {
        Write("A/A.csproj", Lib());
        Write("A/GlobalUsings.cs", "global using A.Ns;\n");
        Write("A/Thing.cs", "namespace A.Ns { public class Marker { } }");
        Write("B/B.csproj", Lib());
        Write("B/GlobalUsings.cs", "global using B.Ns;\n");
        Write("B/Thing.cs", "namespace B.Ns { public class Marker { } }");
        Write("B/Use.cs", "namespace B { public class Use { public void Go() { var m = new Marker(); } } }");

        var graph = Indexer.Build(_root);
        graph.Freeze();

        Assert.DoesNotContain(graph.Diagnostics, d => d.Id == "CS0104");
        Assert.True(HasEdge(graph, "B.Use.Go", EdgeKind.Construct, "B.Ns.Marker"),
            "Marker must resolve to the namespace imported by B, not A's");
    }

    [Fact]
    public void Two_projects_with_top_level_statements_are_not_a_single_entry_point_conflict()
    {
        Write("A/A.csproj", Exe());
        Write("A/Program.cs", "System.Console.WriteLine(1);");
        Write("B/B.csproj", Exe());
        Write("B/Program.cs", "System.Console.WriteLine(2);");

        var graph = Indexer.Build(_root);

        Assert.DoesNotContain(graph.Diagnostics,
            d => d.Id is "CS9338" or "CS8802" or "CS0017");
    }

    [Fact]
    public void A_call_from_one_project_into_another_binds_through_the_reference()
    {
        Write("A/A.csproj", Lib());
        Write("A/Service.cs", "namespace A { public class Service { public int Do() => 1; } }");
        Write("B/B.csproj", Lib(ProjectReference("../A/A.csproj")));
        Write("B/Caller.cs", "namespace B { public class Caller { public int Go(A.Service s) => s.Do(); } }");

        var graph = Indexer.Build(_root);

        Assert.True(HasEdge(graph, "B.Caller.Go", EdgeKind.Call, "A.Service.Do"));
    }

    [Fact]
    public void An_implementor_a_handler_and_a_registration_each_live_in_their_own_project()
    {
        Write("Abstractions/Abstractions.csproj", Lib());
        Write("Abstractions/IThing.cs", "namespace Abs { public interface IThing { void Do(); } }");
        Write("Abstractions/IMediator.cs", "namespace Abs { public interface IMediator { void Send<T>(T command); } }");
        Write("Abstractions/ICommandHandler.cs", "namespace Abs { public interface ICommandHandler<T> { } }");
        Write("Abstractions/IServiceCollection.cs", "namespace Abs { public interface IServiceCollection { } }");
        Write("Abstractions/CreateOrder.cs", "namespace Abs { public sealed record CreateOrder(int Id); }");

        Write("Impl/Impl.csproj", Lib(ProjectReference("../Abstractions/Abstractions.csproj")));
        Write("Impl/Thing.cs", "namespace Impl { public sealed class Thing : Abs.IThing { public void Do() { } } }");

        Write("Handler/Handler.csproj", Lib(ProjectReference("../Abstractions/Abstractions.csproj")));
        Write("Handler/CreateOrderHandler.cs",
            "namespace Handler { public sealed class CreateOrderHandler : Abs.ICommandHandler<Abs.CreateOrder> " +
            "{ public void Handle(Abs.CreateOrder command) { } } }");

        Write("Api/Api.csproj", Lib(ProjectReference("../Abstractions/Abstractions.csproj")
            + ProjectReference("../Impl/Impl.csproj") + ProjectReference("../Handler/Handler.csproj")));
        Write("Api/Api.cs", """
            namespace Api
            {
                using Abs;
                using Impl;

                public static class Registrations
                {
                    public static void Configure(IServiceCollection services) => services.AddScoped<IThing, Thing>();
                    public static void AddScoped<TService, TImplementation>(this IServiceCollection s) { }
                }

                public class Caller
                {
                    private readonly IMediator _mediator = null!;
                    public void Go() => _mediator.Send(new CreateOrder(1));
                }
            }
            """);

        var graph = Indexer.Build(_root);
        graph.Freeze();

        Assert.True(HasEdge(graph, "Abs.IThing", EdgeKind.Interface, "Impl.Thing"));
        Assert.True(HasEdge(graph, "Abs.IThing", EdgeKind.DiBinding, "Impl.Thing"));
        Assert.Contains(graph.Edges, e =>
            e.Kind == EdgeKind.Mediatr &&
            graph.ById(e.From)?.Short == "Caller.Go" &&
            graph.ById(e.To)?.Short == "CreateOrderHandler.Handle");
    }

    [Fact]
    public void A_full_index_and_an_incremental_refresh_agree()
    {
        Write("A/A.csproj", Exe());
        Write("A/Service.cs", "namespace A { public class Service { public int Do() => 1; } }");
        Write("B/B.csproj", Lib(ProjectReference("../A/A.csproj")));
        Write("B/Caller.cs", "namespace B { public class Caller { public int Go(A.Service s) => s.Do(); } }");

        var full = Indexer.Build(_root);

        var service = Path.Combine(_root, "A", "Service.cs");
        File.AppendAllText(service, "\nnamespace A { public partial class Service2 { public int Extra() => 2; } }");
        var dirty = new[] { Path.GetRelativePath(_root, service) };

        var incremental = Indexer.BuildIncremental(full, dirty);
        Assert.NotNull(incremental);

        var fresh = Indexer.Build(_root);
        AssertSameGraph(incremental!, fresh);
    }

    [Fact]
    public void A_reference_cycle_is_broken_and_reported_without_crashing()
    {
        Write("A/A.csproj", Exe(ProjectReference("../B/B.csproj")));
        Write("A/Program.cs", "System.Console.WriteLine(1);");
        Write("B/B.csproj", Exe(ProjectReference("../A/A.csproj")));
        Write("B/Program.cs", "System.Console.WriteLine(2);");

        var graph = Indexer.Build(_root);
        Assert.NotEmpty(graph.ProjectCycles);

        GraphStore.Save(graph);
        var output = Capture(() => DoctorCommand.Execute(_root, new Options([])));
        Assert.Contains("cycle", output, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertSameGraph(Graph a, Graph b)
    {
        a.Freeze();
        b.Freeze();

        Assert.Equal(
            a.Nodes.Select(n => n.Key).OrderBy(x => x, StringComparer.Ordinal),
            b.Nodes.Select(n => n.Key).OrderBy(x => x, StringComparer.Ordinal));

        Assert.Equal(EdgeSignatures(a), EdgeSignatures(b));
    }

    private static List<string> EdgeSignatures(Graph g) => g.Edges
        .Select(e => $"{g.ById(e.From)?.Key}|{e.Kind}|{g.ById(e.To)?.Key}|{e.Note}")
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToList();
}
