using CsMesh.Analysis;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// InternalsVisibleTo is an assembly attribute, so a friend reads the internal types of the project
/// that names it. The single whole-solution compilation made internals visible to everything and the
/// attribute never mattered; one compilation per project needs it synthesized from the csproj, or a
/// test or sibling assembly that reads an internal through the friend relationship stops binding.
/// </summary>
public sealed class InternalsVisibleToTests : IDisposable
{
    private readonly string _root;

    public InternalsVisibleToTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-ivt-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static bool HasEdge(Graph graph, string fromName, EdgeKind kind, string toName) =>
        graph.Edges.Any(e => graph.ById(e.From)?.Name == fromName && e.Kind == kind && graph.ById(e.To)?.Name == toName);

    /// <summary>
    /// The use of an internal type from the friend is CS0122 unless IVT grants it. The call edge
    /// alone is not enough: Roslyn still returns the method symbol through an accessibility error,
    /// so the edge exists whether or not the internals are actually visible. CS0051 is deliberately
    /// not asserted -- it compares declared accessibility and does not consider IVT at all.
    /// </summary>
    private static void AssertInternalIsAccessible(Graph graph)
    {
        var ids = string.Join(",", graph.Diagnostics.Select(d => $"{d.Id}x{d.Count}"));
        Assert.True(!graph.Diagnostics.Any(d => d.Id is "CS0122"), ids);
    }

    [Fact]
    public void An_internal_type_is_visible_through_the_csproj_item()
    {
        Write("T/T.csproj", Lib("<ItemGroup><InternalsVisibleTo Include=\"Friend\" /></ItemGroup>"));
        Write("T/Secret.cs", "namespace T { internal class Secret { public int Do() => 1; } }");
        Write("Friend/Friend.csproj", Lib(ProjectReference("../T/T.csproj")));
        Write("Friend/Use.cs", "namespace Friend { internal class Use { internal int Go(T.Secret s) => s.Do(); } }");

        var graph = Indexer.Build(_root);

        AssertInternalIsAccessible(graph);
        Assert.True(HasEdge(graph, "Friend.Use.Go", EdgeKind.Call, "T.Secret.Do"));
    }

    [Fact]
    public void An_internal_type_is_visible_through_the_assembly_attribute_form()
    {
        Write("T/T.csproj", Lib(
            "<ItemGroup><AssemblyAttribute Include=\"System.Runtime.CompilerServices.InternalsVisibleTo\">" +
            "<_Parameter1>Friend</_Parameter1></AssemblyAttribute></ItemGroup>"));
        Write("T/Secret.cs", "namespace T { internal class Secret { public int Do() => 1; } }");
        Write("Friend/Friend.csproj", Lib(ProjectReference("../T/T.csproj")));
        Write("Friend/Use.cs", "namespace Friend { internal class Use { internal int Go(T.Secret s) => s.Do(); } }");

        var graph = Indexer.Build(_root);

        AssertInternalIsAccessible(graph);
        Assert.True(HasEdge(graph, "Friend.Use.Go", EdgeKind.Call, "T.Secret.Do"));
    }

    /// <summary>
    /// Two projects share the assembly name Thing, so the referenced one's compilation is
    /// disambiguated. Its IVT attribute still names the friend, and the friend still reads the
    /// internal through the CompilationReference.
    /// </summary>
    [Fact]
    public void An_internal_type_is_visible_when_the_target_name_collides()
    {
        Write("Thing/Thing.csproj", Lib("<ItemGroup><InternalsVisibleTo Include=\"Friend\" /></ItemGroup>"));
        Write("Thing/Secret.cs", "namespace T1 { internal class Secret { public int Do() => 1; } }");
        Write("Thing2/Thing.csproj", Lib());
        Write("Thing2/Other.cs", "namespace T2 { public class Other { } }");
        Write("Friend/Friend.csproj", Lib(ProjectReference("../Thing/Thing.csproj")));
        Write("Friend/Use.cs", "namespace Friend { internal class Use { internal int Go(T1.Secret s) => s.Do(); } }");

        var graph = Indexer.Build(_root);

        Assert.DoesNotContain(graph.Diagnostics, d => d.Id == "CS8203");
        AssertInternalIsAccessible(graph);
        Assert.True(HasEdge(graph, "Friend.Use.Go", EdgeKind.Call, "T1.Secret.Do"));
    }

    /// <summary>
    /// The translation a disambiguated friend needs: the csproj writes the real assembly name, the
    /// attribute has to name the compilation the friend actually got.
    /// </summary>
    [Fact]
    public void A_disambiguated_friend_is_named_by_its_compilation_name()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Friend"] = "Friend_src_a_ab12cd"
        };

        Assert.Equal("Friend_src_a_ab12cd", InternalsVisibleTo.Resolve("Friend", map));
        Assert.Equal("Friend_src_a_ab12cd, PublicKey=abc", InternalsVisibleTo.Resolve("Friend, PublicKey=abc", map));
        Assert.Equal("External", InternalsVisibleTo.Resolve("External", map));
    }

    [Fact]
    public void An_item_naming_a_property_is_left_out_and_counted()
    {
        var csproj = Path.Combine(_root, "P.csproj");
        File.WriteAllText(csproj,
            "<Project><ItemGroup><InternalsVisibleTo Include=\"$(FriendName)\" /></ItemGroup></Project>");

        var (friends, unevaluable) = InternalsVisibleTo.Read(csproj);

        Assert.Empty(friends);
        Assert.Equal(1, unevaluable);
    }
}
