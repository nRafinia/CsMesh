using CsMesh.Analysis;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The compile-item parser and its glob matcher, exercised as units. Ownership reads this model, so a
/// matcher that crosses a directory boundary on <c>*</c> or misses <c>**</c> would silently move a
/// file between projects once the split depends on it.
/// </summary>
public sealed class CompileItemsTests : IDisposable
{
    private readonly string _root;

    public CompileItemsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-items-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ------------------------------------------------------------------ glob matcher

    [Theory]
    [InlineData("**/*.cs", "a.cs", true)]
    [InlineData("**/*.cs", "sub/a.cs", true)]
    [InlineData("**/*.cs", "sub/deep/a.cs", true)]
    [InlineData("**/*.cs", "a.txt", false)]
    [InlineData("sub/*.cs", "sub/a.cs", true)]
    [InlineData("sub/*.cs", "sub/deep/a.cs", false)]
    [InlineData("a?.cs", "ab.cs", true)]
    [InlineData("a?.cs", "a.cs", false)]
    [InlineData(@"Swap\Jobs\*.cs", "Swap/Jobs/Thing.cs", true)]
    [InlineData(@"Swap\Jobs\Gone.cs", "Swap/Jobs/Gone.cs", true)]
    [InlineData(@"Swap\Jobs\Gone.cs", "Swap/Gone.cs", false)]
    [InlineData("**", "anything/at/all.cs", true)]
    public void Glob_matches_the_msbuild_shape(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, CompileItems.Matches(pattern, path));
    }

    // ------------------------------------------------------------------ parser

    [Fact]
    public void EnableDefaultCompileItems_false_is_read()
    {
        var csproj = Write("P/P.csproj", "<Project><PropertyGroup>" +
            "<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup></Project>");

        var model = CompileItems.Parse(csproj);

        Assert.False(model.EnableDefaultCompileItems);
    }

    [Fact]
    public void Default_items_are_on_when_the_property_is_absent()
    {
        var csproj = Write("P/P.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var model = CompileItems.Parse(csproj);

        Assert.True(model.EnableDefaultCompileItems);
    }

    [Fact]
    public void A_remove_glob_is_collected_relative_to_the_project()
    {
        var csproj = Write("P/P.csproj", "<Project><ItemGroup>" +
            @"<Compile Remove=""Swap\Jobs\Expired.cs"" /></ItemGroup></Project>");
        var removed = Path.Combine(_root, "P", "Swap", "Jobs", "Expired.cs");
        var kept = Path.Combine(_root, "P", "Swap", "Kept.cs");

        var model = CompileItems.Parse(csproj);

        Assert.Single(model.Removes);
        Assert.True(model.RemovesFile(Path.Combine(_root, "P"), removed));
        Assert.False(model.RemovesFile(Path.Combine(_root, "P"), kept));
    }

    [Fact]
    public void A_linked_include_outside_the_project_is_resolved()
    {
        var csproj = Write("P/P.csproj", "<Project><ItemGroup>" +
            @"<Compile Include=""..\Shared\Thing.cs"" Link=""Thing.cs"" /></ItemGroup></Project>");
        var linked = Path.Combine(_root, "Shared", "Thing.cs");

        var model = CompileItems.Parse(csproj);

        Assert.Single(model.Includes);
        Assert.True(model.IncludesFile(Path.Combine(_root, "P"), linked));
    }

    [Fact]
    public void This_file_directory_is_resolved_against_the_containing_file()
    {
        var csproj = Write("P/P.csproj", "<Project><ItemGroup>" +
            @"<Compile Include=""$(MSBuildThisFileDirectory)Generated\Thing.cs"" /></ItemGroup></Project>");
        var generated = Path.Combine(_root, "P", "Generated", "Thing.cs");

        var model = CompileItems.Parse(csproj);

        Assert.Empty(model.Unevaluable);
        Assert.True(model.IncludesFile(Path.Combine(_root, "P"), generated));
    }

    [Fact]
    public void A_projitems_import_contributes_its_includes()
    {
        Write("Shared/Shared.projitems", "<Project><ItemGroup>" +
            @"<Compile Include=""$(MSBuildThisFileDirectory)Code.cs"" /></ItemGroup></Project>");
        var csproj = Write("P/P.csproj", "<Project><Import Project=\"../Shared/Shared.projitems\" /></Project>");
        var shared = Path.Combine(_root, "Shared", "Code.cs");

        var model = CompileItems.Parse(csproj);

        Assert.True(model.IncludesFile(Path.Combine(_root, "P"), shared));
    }

    [Fact]
    public void An_item_with_an_unknown_property_is_counted_and_left_out()
    {
        var csproj = Write("P/P.csproj", "<Project><ItemGroup>" +
            @"<Compile Include=""$(GeneratedDir)\Thing.cs"" /></ItemGroup></Project>");

        var model = CompileItems.Parse(csproj);

        Assert.Equal(1, model.UnevaluableCount);
        Assert.Contains("$(GeneratedDir)", model.Unevaluable[0]);
        Assert.Empty(model.Includes);
    }

    [Fact]
    public void A_missing_project_is_an_empty_default_model()
    {
        var model = CompileItems.Parse(Path.Combine(_root, "does-not-exist.csproj"));

        Assert.True(model.EnableDefaultCompileItems);
        Assert.Empty(model.Removes);
        Assert.Empty(model.Includes);
        Assert.Equal(0, model.UnevaluableCount);
    }
}
