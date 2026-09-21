using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Ownership must match what a real build compiles, because the split partitions files by owning
/// project. A csproj can remove a file from its default glob, turn the glob off and list what it
/// wants, or pull a file in from outside its directory through a link or a shared project. Each of
/// those is a place the nearest-csproj rule alone answers wrongly.
///
/// Console is captured for the doctor line, so this joins the console-capture collection.
/// </summary>
[Collection("console-capture")]
public sealed class CompileOwnershipTests : IDisposable
{
    private readonly string _root;

    public CompileOwnershipTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-own-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string ExeProject(string extra = "") =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType>" +
        "<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>" +
        "</PropertyGroup>" + extra + "</Project>";

    private static string LibraryProject(string extra = "") =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
        "<ImplicitUsings>enable</ImplicitUsings></PropertyGroup>" + extra + "</Project>";

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
    public void A_removed_file_is_not_owned_by_its_nearest_project()
    {
        Write("P.csproj", LibraryProject("<ItemGroup><Compile Remove=\"Dropped.cs\" /></ItemGroup>"));
        Write("Kept.cs", "namespace P; public class Kept { }");
        Write("Dropped.cs", "namespace P; public class Dropped { }");

        var scope = ProjectScope.Discover(_root);
        Assert.Empty(scope.Owners(Path.Combine(_root, "Dropped.cs")));
        Assert.NotEmpty(scope.Owners(Path.Combine(_root, "Kept.cs")));

        var graph = Indexer.Build(_root);

        Assert.Contains(graph.Nodes, n => n.Name == "P.Kept");
        Assert.DoesNotContain(graph.Nodes, n => n.Name == "P.Dropped");
    }

    [Fact]
    public void Default_items_off_owns_only_what_is_explicitly_included()
    {
        Write("P.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>" +
            "<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>" +
            "<ItemGroup><Compile Include=\"Kept.cs\" /></ItemGroup></Project>");
        Write("Kept.cs", "namespace P; public class Kept { }");
        Write("Dropped.cs", "namespace P; public class Dropped { }");

        var scope = ProjectScope.Discover(_root);
        Assert.Empty(scope.Owners(Path.Combine(_root, "Dropped.cs")));
        Assert.NotEmpty(scope.Owners(Path.Combine(_root, "Kept.cs")));

        var graph = Indexer.Build(_root);

        Assert.Contains(graph.Nodes, n => n.Name == "P.Kept");
        Assert.DoesNotContain(graph.Nodes, n => n.Name == "P.Dropped");
    }

    [Fact]
    public void A_file_linked_from_outside_the_directory_is_owned()
    {
        Write("P/P.csproj", LibraryProject("<ItemGroup><Compile Include=\"..\\Shared\\Shared.cs\" /></ItemGroup>"));
        Write("Shared/Shared.cs", "namespace Shared; public class SharedThing { }");

        var scope = ProjectScope.Discover(_root);
        var owners = scope.Owners(Path.Combine(_root, "Shared", "Shared.cs"));
        Assert.Contains(Path.Combine(_root, "P"), owners, StringComparer.OrdinalIgnoreCase);

        var graph = Indexer.Build(_root);

        Assert.Contains(graph.Nodes, n => n.Name == "Shared.SharedThing");
    }

    [Fact]
    public void A_shared_projitems_include_is_owned()
    {
        Write("Shared/Shared.projitems", "<Project><ItemGroup>" +
            "<Compile Include=\"$(MSBuildThisFileDirectory)Code.cs\" /></ItemGroup></Project>");
        Write("Shared/Code.cs", "namespace Shared; public class SharedCode { }");
        Write("P/P.csproj", LibraryProject("<Import Project=\"../Shared/Shared.projitems\" />"));

        var scope = ProjectScope.Discover(_root);
        var owners = scope.Owners(Path.Combine(_root, "Shared", "Code.cs"));
        Assert.Contains(Path.Combine(_root, "P"), owners, StringComparer.OrdinalIgnoreCase);

        var graph = Indexer.Build(_root);

        Assert.Contains(graph.Nodes, n => n.Name == "Shared.SharedCode");
    }

    /// <summary>
    /// A linked file can belong to several projects at once; that is what compiling shared source
    /// into each assembly means. The build produces two assemblies, so the graph produces two
    /// nodes: one per owner, with distinct assembly-qualified keys and distinct project stamps.
    /// Binding it once from the first owner, as the pre-E split did, merged away the second
    /// assembly and made the two indistinguishable in exit 3.
    /// </summary>
    [Fact]
    public void A_file_included_by_two_projects_has_both_owners_and_one_node_per_owner()
    {
        Write("P1/P1.csproj", LibraryProject("<ItemGroup><Compile Include=\"..\\Shared\\S.cs\" /></ItemGroup>"));
        Write("P2/P2.csproj", LibraryProject("<ItemGroup><Compile Include=\"..\\Shared\\S.cs\" /></ItemGroup>"));
        Write("Shared/S.cs", "namespace Shared; public class S { }");

        var scope = ProjectScope.Everything(_root);
        var owners = scope.Owners(Path.Combine(_root, "Shared", "S.cs"));

        Assert.Equal(2, owners.Count);

        var graph = Indexer.Build(_root);
        var nodes = graph.Nodes.Where(n => n.Name == "Shared.S").ToList();

        Assert.Equal(2, nodes.Count);
        Assert.Equal(2, nodes.Select(n => n.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, nodes.Select(n => n.Project).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_loose_file_is_excluded_when_the_repository_has_a_project()
    {
        Write("src/App/App.csproj", ExeProject());
        Write("src/App/Program.cs", "System.Console.WriteLine(1);");
        Write("Scratch.cs", "public class Loose { }");

        var scope = ProjectScope.Discover(_root);
        Assert.Empty(scope.Owners(Path.Combine(_root, "Scratch.cs")));

        var graph = Indexer.Build(_root);

        Assert.DoesNotContain(graph.Nodes, n => n.Name == "Loose");
        Assert.Equal(1, graph.ExcludedLooseFiles);
    }

    [Fact]
    public void A_loose_file_is_kept_when_the_repository_has_no_project()
    {
        Write("Scratch.cs", "public class Loose { }");

        var graph = Indexer.Build(_root);

        Assert.Contains(graph.Nodes, n => n.Name == "Loose");
        Assert.Equal(0, graph.ExcludedLooseFiles);
    }

    [Fact]
    public void Doctor_counts_an_unevaluable_compile_item()
    {
        Write("src/App/App.csproj", ExeProject(
            "<ItemGroup><Compile Include=\"$(GeneratedDir)\\X.cs\" /></ItemGroup>"));
        Write("src/App/Program.cs", "System.Console.WriteLine(1);");

        var graph = Indexer.Build(_root);
        Assert.Equal(1, graph.UnevaluableCompileItems);

        GraphStore.Save(graph);
        var output = Capture(() => DoctorCommand.Execute(_root, new Options([])));

        Assert.Contains("unevaluable", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 compile item", output, StringComparison.OrdinalIgnoreCase);
    }
}
