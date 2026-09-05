using CsMesh.Analysis;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A repository's own build output must not be a reference for the source it was built from.
///
/// Every bin/ under the root was scanned for references, and every project in the root also builds
/// into one. The compilation then held each of the repository's types twice -- once from source,
/// once from the assembly compiled out of that source. Ordinary calls survived it because the
/// source declaration usually wins. Extension methods did not: their lookup gathers candidates from
/// every assembly in scope, so the compiler found the method in both places, could prefer neither,
/// and returned candidates with no symbol.
///
/// What that produced was 'ambiguous-overload' piled up in whichever file calls the most extension
/// methods, which is always the composition root -- so it read like something odd about that one
/// file rather than a duplicate reference across the whole solution.
/// </summary>
public sealed class ProjectOutputShadowingTests : IDisposable
{
    private readonly List<string> _temp = [];

    /// <summary>
    /// A repository with a library whose extension methods a second project calls, the library
    /// already built into its own bin/.
    /// </summary>
    private string Repository(string solutionProjects)
    {
        var root = Path.Combine(Path.GetTempPath(), "csmesh-shadow-" + Guid.NewGuid().ToString("N")[..8]);
        _temp.Add(root);

        Directory.CreateDirectory(Path.Combine(root, "Data"));
        Directory.CreateDirectory(Path.Combine(root, "Data", "bin", "Debug", "net10.0"));
        Directory.CreateDirectory(Path.Combine(root, "App"));

        File.WriteAllText(Path.Combine(root, "Data", "Data.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        File.WriteAllText(Path.Combine(root, "App", "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        File.WriteAllText(Path.Combine(root, "Data", "Extensions.cs"),
            """
            namespace Data;

            public interface IServices { }

            public static class DataServiceExtensions
            {
                public static IServices AddData(this IServices s) => s;
            }
            """);

        File.WriteAllText(Path.Combine(root, "App", "Composition.cs"),
            """
            using Data;

            public static class Composition
            {
                public static void Wire(IServices services) => services.AddData();
            }
            """);

        File.WriteAllText(Path.Combine(root, "Repo.slnx"), solutionProjects);
        return root;
    }

    private const string BothInScope =
        """
        <Solution>
          <Project Path="App/App.csproj" />
          <Project Path="Data/Data.csproj" />
        </Solution>
        """;

    private const string OnlyApp =
        """
        <Solution>
          <Project Path="App/App.csproj" />
        </Solution>
        """;

    [Fact]
    public void A_project_compiled_from_source_has_its_own_output_excluded()
    {
        var graph = Indexer.Build(Repository(BothInScope));

        // Two projects in scope, so two outputs shadowed -- whether or not either was ever built.
        Assert.Equal(2, graph.ShadowedOutputs);
    }

    [Fact]
    public void An_extension_method_call_binds_when_both_sides_are_in_scope()
    {
        var graph = Indexer.Build(Repository(BothInScope));

        Assert.False(graph.UnresolvedByReason.ContainsKey("call/ambiguous-overload"));
        Assert.Contains(graph.Edges, e =>
            graph.ById(e.From)?.Short == "Composition.Wire" &&
            graph.ById(e.To)?.Short == "DataServiceExtensions.AddData");
    }

    [Fact]
    public void A_project_left_out_of_scope_keeps_its_output_as_a_reference()
    {
        // Data is not compiled here, so its assembly in bin/ is the only way anything calling into
        // it can bind. Shadowing it would trade one silent gap for a larger one.
        var graph = Indexer.Build(Repository(OnlyApp));

        Assert.Equal(1, graph.ShadowedOutputs);
    }

    [Fact]
    public void Indexing_everything_still_excludes_the_outputs()
    {
        // --all was built from an empty project list, so nothing was known about what was being
        // compiled and no output was shadowed. Widening what gets indexed is not a reason to stop
        // knowing which projects those files belong to -- and with every file in scope, every
        // output in the repository is a duplicate of something in the compilation.
        var graph = Indexer.Build(Repository(OnlyApp), includeAllProjects: true);

        Assert.Equal(2, graph.ShadowedOutputs);
    }

    public void Dispose()
    {
        foreach (var dir in _temp)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }
}