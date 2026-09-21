using CsMesh.Analysis;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Override and interface member edges are read from the compiler, not matched by member name.
///
/// Name matching linked a derived constructor to its base constructor (constructors cannot
/// override), linked static methods that merely share a name, linked a member hidden with 'new',
/// and ignored parameter types so two overloads of one interface member both pointed at whichever
/// implementation was declared first. The compiler's own relationship --
/// OverriddenMethod/OverriddenProperty/OverriddenEvent and
/// FindImplementationForInterfaceMember -- says exactly which member is which.
/// </summary>
public sealed class OverrideAndInterfaceEdgeTests : IDisposable
{
    private readonly string _root;

    public OverrideAndInterfaceEdgeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-edges-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string ProjectReference(string relative) =>
        $"<ItemGroup><ProjectReference Include=\"{relative}\" /></ItemGroup>";

    private static Graph Indexed(string root)
    {
        var graph = Indexer.Build(root);
        graph.Freeze();
        return graph;
    }

    private static bool HasMemberEdge(Graph g, EdgeKind kind, string fromName, string toName) =>
        g.Edges.Any(e => e.Kind == kind
                         && g.ById(e.From)?.Name == fromName
                         && g.ById(e.To)?.Name == toName);

    private static bool HasExplicitInterfaceEdge(Graph g, string interfaceMemberName, string implementingKeyFragment) =>
        g.Edges.Any(e => e.Kind == EdgeKind.Interface
                         && g.ById(e.From)?.Name == interfaceMemberName
                         && (g.ById(e.To)?.Key.Contains(implementingKeyFragment, StringComparison.Ordinal) ?? false));

    private static bool HasConstructorEdge(Graph g, EdgeKind kind) =>
        g.Edges.Any(e => e.Kind == kind
                         && ((g.ById(e.From)?.Name.EndsWith("..ctor", StringComparison.Ordinal) ?? false)
                             || (g.ById(e.To)?.Name.EndsWith("..ctor", StringComparison.Ordinal) ?? false)));

    [Fact]
    public void Constructors_get_no_override_edge_but_the_real_method_override_does()
    {
        Write("P/P.csproj", Lib());
        Write("P/Types.cs", """
            namespace Demo
            {
                public class Base { public Base() { } public virtual void M() { } }
                public class Derived : Base { public Derived() { } public override void M() { } }
            }
            """);

        var graph = Indexed(_root);

        Assert.False(HasConstructorEdge(graph, EdgeKind.Override));
        Assert.True(HasMemberEdge(graph, EdgeKind.Override, "Demo.Base.M", "Demo.Derived.M"));
    }

    [Fact]
    public void A_static_method_that_shares_a_name_is_not_an_override()
    {
        Write("P/P.csproj", Lib());
        Write("P/Types.cs", """
            namespace Demo
            {
                public class Base { public static void M() { } }
                public class Derived : Base { public new static void M() { } }
            }
            """);

        var graph = Indexed(_root);

        Assert.False(HasMemberEdge(graph, EdgeKind.Override, "Demo.Base.M", "Demo.Derived.M"));
    }

    [Fact]
    public void A_new_member_that_hides_a_virtual_member_is_not_an_override()
    {
        Write("P/P.csproj", Lib());
        Write("P/Types.cs", """
            namespace Demo
            {
                public class Base { public virtual void M() { } }
                public class Derived : Base { public new void M() { } }
            }
            """);

        var graph = Indexed(_root);

        Assert.False(HasMemberEdge(graph, EdgeKind.Override, "Demo.Base.M", "Demo.Derived.M"));
    }

    [Fact]
    public void An_override_across_projects_links_by_symbol_and_not_by_constructor()
    {
        Write("A/A.csproj", Lib());
        Write("A/Base.cs", """
            namespace Demo
            {
                public class Base
                {
                    public Base() { }
                    public virtual int Calc() => 0;
                }
            }
            """);

        Write("B/B.csproj", Lib(ProjectReference("../A/A.csproj")));
        Write("B/Derived.cs", """
            namespace Demo
            {
                public class Derived : Base
                {
                    public Derived() { }
                    public override int Calc() => 1;
                }
            }
            """);

        var graph = Indexed(_root);

        Assert.True(HasMemberEdge(graph, EdgeKind.Override, "Demo.Base.Calc", "Demo.Derived.Calc"));
        Assert.False(HasConstructorEdge(graph, EdgeKind.Override));
    }

    [Fact]
    public void A_property_override_links_by_symbol()
    {
        Write("P/P.csproj", Lib());
        Write("P/Types.cs", """
            namespace Demo
            {
                public class Base { public virtual int Value => 0; }
                public class Derived : Base { public override int Value => 1; }
            }
            """);

        var graph = Indexed(_root);

        Assert.True(HasMemberEdge(graph, EdgeKind.Override, "Demo.Base.Value", "Demo.Derived.Value"));
    }

    [Fact]
    public void An_explicit_interface_implementation_gets_an_interface_edge()
    {
        Write("P/P.csproj", Lib());
        Write("P/Types.cs", """
            namespace Demo
            {
                public interface IThing { int Value { get; } void Run(); }
                public class Thing : IThing
                {
                    int IThing.Value => 1;
                    void IThing.Run() { }
                }
            }
            """);

        var graph = Indexed(_root);

        // An explicit implementation's display name carries the interface (Demo.Thing.Demo.IThing.Run),
        // so the test matches the implementing node's key, which stays the member identity.
        Assert.True(HasExplicitInterfaceEdge(graph, "Demo.IThing.Value", "|global::Demo.Thing|Value|Property"));
        Assert.True(HasExplicitInterfaceEdge(graph, "Demo.IThing.Run", "|global::Demo.Thing|Demo.IThing.Run`0()|Method"));
    }

    [Fact]
    public void An_implementation_nested_in_another_project_gets_an_interface_edge()
    {
        Write("A/A.csproj", Lib());
        Write("A/ICurrent.cs", "namespace Demo { public interface ICurrent { string Name(); } }");

        Write("B/B.csproj", Lib(ProjectReference("../A/A.csproj")));
        Write("B/Scope.cs", """
            namespace Demo
            {
                public class Scope
                {
                    private sealed class Impl : ICurrent { public string Name() => "x"; }
                }
            }
            """);

        var graph = Indexed(_root);

        Assert.True(HasMemberEdge(graph, EdgeKind.Interface, "Demo.ICurrent.Name", "Demo.Scope.Impl.Name"));
    }

    /// <summary>
    /// A default interface member is the interface's own member. A class that relies on it must
    /// produce no member edge -- in particular no edge from the member to itself -- while a class
    /// that overrides the default links to the class's own member.
    /// </summary>
    [Fact]
    public void A_default_interface_member_produces_no_edge_to_itself_but_an_override_does()
    {
        Write("P/P.csproj", Lib());
        Write("P/Types.cs", """
            namespace Demo
            {
                public interface IThing { void M() { } }
                public class Defaulted : IThing { }
                public class Overriding : IThing { public void M() { } }
            }
            """);

        var graph = Indexed(_root);

        Assert.DoesNotContain(graph.Edges, e => e.From == e.To && e.Kind == EdgeKind.Interface);

        Assert.True(HasMemberEdge(graph, EdgeKind.Interface, "Demo.IThing.M", "Demo.Overriding.M"));
        Assert.DoesNotContain(graph.Edges, e => e.Kind == EdgeKind.Interface
            && graph.ById(e.From)?.Name == "Demo.IThing.M"
            && (graph.ById(e.To)?.Name.StartsWith("Demo.Defaulted", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public void Overloaded_interface_members_link_to_their_own_implementation()
    {
        Write("P/P.csproj", Lib());
        Write("P/Types.cs", """
            namespace Demo
            {
                public interface ISvc { void Run(); void Run(string x); }
                public class Svc : ISvc { public void Run() { } public void Run(string x) { } }
            }
            """);

        var graph = Indexed(_root);

        var edges = graph.Edges
            .Where(e => e.Kind == EdgeKind.Interface
                        && graph.ById(e.From)?.Name == "Demo.ISvc.Run"
                        && graph.ById(e.To)?.Name == "Demo.Svc.Run")
            .ToList();

        Assert.Equal(2, edges.Count);
        Assert.Contains(edges, e => !graph.ById(e.From)!.Signature.Contains("string", StringComparison.Ordinal)
                                    && !graph.ById(e.To)!.Signature.Contains("string", StringComparison.Ordinal));
        Assert.Contains(edges, e => graph.ById(e.From)!.Signature.Contains("string", StringComparison.Ordinal)
                                    && graph.ById(e.To)!.Signature.Contains("string", StringComparison.Ordinal));
    }
}
