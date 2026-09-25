using CsMesh.Analysis;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A tree can hold source nothing compiles. Indexing it is not harmless: its types compete in
/// lookups, its registrations are reported as if the container used them, and its unresolved
/// references drag the quality numbers down, with nothing to tell the reader that half the noise
/// comes from code that never runs.
/// </summary>
public sealed class ProjectScopeTests : IDisposable
{
    private readonly string _root;

    public ProjectScopeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-scope-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src", "Live"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "Dead"));

        Write("src/Live/Live.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        Write("src/Dead/Dead.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        Write("src/Live/Code.cs", "namespace Live; public class A { }");
        Write("src/Dead/Code.cs", "namespace Dead; public class B { }");
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Path_(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    // ------------------------------------------------------------------ solution file wins

    [Fact]
    public void A_solution_file_decides_which_projects_are_in()
    {
        Write("App.slnx", "<Solution><Project Path=\"src/Live/Live.csproj\" /></Solution>");

        var scope = ProjectScope.Discover(_root);

        Assert.True(scope.Includes(Path_("src/Live/Code.cs")));
        Assert.False(scope.Includes(Path_("src/Dead/Code.cs")));
        Assert.Contains("Dead", scope.Reason.Length > 0 ? string.Join(",", scope.Excluded) : "");
    }

    /// <summary>
    /// The .sln a Windows tool writes carries backslashes. On Linux a backslash is an ordinary
    /// filename character, so the path only resolves after it is normalized; otherwise the solution
    /// matches nothing and the scope silently falls back to the ProjectReference closure, which drops
    /// a library nothing references.
    /// </summary>
    [Fact]
    public void A_backslash_sln_naming_a_library_keeps_it_in_scope()
    {
        Write("App.sln",
            "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
            "Project(\"{FAE04EC0}\") = \"Dead\", \"src\\Dead\\Dead.csproj\", \"{2222}\"\nEndProject\n");

        var scope = ProjectScope.Discover(_root);

        Assert.True(scope.Includes(Path_("src/Dead/Code.cs")));
        Assert.Contains("App.sln", scope.Decision);
    }

    [Fact]
    public void A_backslash_slnx_naming_a_library_keeps_it_in_scope()
    {
        Write("App.slnx", "<Solution><Project Path=\"src\\Dead\\Dead.csproj\" /></Solution>");

        var scope = ProjectScope.Discover(_root);

        Assert.True(scope.Includes(Path_("src/Dead/Code.cs")));
        Assert.Contains("App.slnx", scope.Decision);
    }

    [Fact]
    public void The_old_sln_format_is_read_too()
    {
        Write("App.sln",
            "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
            "Project(\"{FAE04EC0}\") = \"Live\", \"src\\Live\\Live.csproj\", \"{1111}\"\nEndProject\n");

        var scope = ProjectScope.Discover(_root);

        Assert.True(scope.Includes(Path_("src/Live/Code.cs")));
        Assert.False(scope.Includes(Path_("src/Dead/Code.cs")));
        Assert.Contains("App.sln", scope.Decision);
    }

    [Fact]
    public void A_nested_solution_does_not_vote_on_the_outer_scope()
    {
        // A vendored library or submodule under src/Shared carries its own .slnx listing its own
        // projects. Unioning that with the top-level solution puts back exactly the code the
        // top-level solution left out, and the filter goes quiet with nothing to show for it.
        Write("App.slnx", "<Solution><Project Path=\"src/Live/Live.csproj\" /></Solution>");
        Write("src/Vendor/Vendor.slnx", "<Solution><Project Path=\"../Dead/Dead.csproj\" /></Solution>");

        var scope = ProjectScope.Discover(_root);

        Assert.False(scope.Includes(Path_("src/Dead/Code.cs")));
    }

    [Fact]
    public void The_scope_decision_is_always_stated_even_when_nothing_is_excluded()
    {
        // A filter that fell back to including everything looks identical to one with nothing to
        // exclude. That difference hid a bug for a whole round of debugging.
        Write("App.slnx",
            "<Solution><Project Path=\"src/Live/Live.csproj\" /><Project Path=\"src/Dead/Dead.csproj\" /></Solution>");

        var scope = ProjectScope.Discover(_root);

        Assert.False(scope.ExcludesAnything);
        Assert.NotEmpty(scope.Decision);
        Assert.Contains("App.slnx", scope.Decision);
    }

    [Fact]
    public void A_solution_naming_nothing_that_exists_falls_back_rather_than_excluding_everything()
    {
        Write("App.slnx", "<Solution><Project Path=\"nowhere/Gone.csproj\" /></Solution>");

        var scope = ProjectScope.Discover(_root);

        // Live is an Exe, so the closure keeps it. Nothing may be excluded on the strength of a
        // solution whose paths all failed to resolve.
        Assert.True(scope.Includes(Path_("src/Live/Code.cs")));
        Assert.DoesNotContain("App.slnx", scope.Decision);
    }

    // ------------------------------------------------------------------ reference closure

    [Fact]
    public void Without_a_solution_the_closure_from_executables_decides()
    {
        var scope = ProjectScope.Discover(_root);

        Assert.True(scope.Includes(Path_("src/Live/Code.cs")));
        Assert.False(scope.Includes(Path_("src/Dead/Code.cs")));
    }

    [Fact]
    public void A_referenced_project_is_reachable()
    {
        Write("src/Live/Live.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>"
            + "<ItemGroup><ProjectReference Include=\"..\\Dead\\Dead.csproj\" /></ItemGroup></Project>");

        var scope = ProjectScope.Discover(_root);

        Assert.True(scope.Includes(Path_("src/Dead/Code.cs")));
        Assert.False(scope.ExcludesAnything);
    }

    [Fact]
    public void A_test_project_is_its_own_root()
    {
        // Nothing references a test project. Dropping them would take every test caller out of
        // blast-radius, which is the opposite of what that command is for.
        Write("src/Dead/Dead.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
            + "<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.0.0\" /></ItemGroup></Project>");

        var scope = ProjectScope.Discover(_root);

        Assert.True(scope.Includes(Path_("src/Dead/Code.cs")));
    }

    // ------------------------------------------------------------------ nested projects

    /// <summary>
    /// The nearest csproj owns a file. A live parent must not claim a nested project's sources:
    /// compiling them twice duplicated every type the nested project declared, which is what turned
    /// eight Fixtures/*/Bus.cs files into CS0101 x14 in one compilation.
    /// </summary>
    [Fact]
    public void A_nested_projects_files_are_not_claimed_by_its_live_parent()
    {
        Write("src/P/P.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        Write("src/P/N/N.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        Write("src/P/N/Nested.cs", "namespace N; public class Nested { }");
        Write("App.slnx", "<Solution><Project Path=\"src/P/P.csproj\" /></Solution>");

        var scope = ProjectScope.Discover(_root);

        Assert.False(scope.Includes(Path_("src/P/N/Nested.cs")));
        Assert.Contains("N.csproj", string.Join(",", scope.Excluded));
    }

    [Fact]
    public void A_nested_project_in_scope_owns_its_files_rather_than_its_parent()
    {
        Write("src/P/P.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        Write("src/P/Top.cs", "namespace P; public class Top { }");
        Write("src/P/N/N.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        Write("src/P/N/Nested.cs", "namespace N; public class Nested { }");

        // index --all marks every project live, so N's files come in on N's own account.
        var all = ProjectScope.Everything(_root);
        Assert.True(all.Includes(Path_("src/P/N/Nested.cs")));

        // With only N named, N's files are still included and P's own file is not: ownership
        // follows the nearest csproj, not whichever ancestor happens to contain N.
        Write("App.slnx", "<Solution><Project Path=\"src/P/N/N.csproj\" /></Solution>");
        var scope = ProjectScope.Discover(_root);
        Assert.True(scope.Includes(Path_("src/P/N/Nested.cs")));
        Assert.False(scope.Includes(Path_("src/P/Top.cs")));
    }

    /// <summary>
    /// The fix must not start excluding plain folders: a subdirectory with no csproj of its own
    /// still belongs to the project above it.
    /// </summary>
    [Fact]
    public void A_plain_subfolder_of_a_live_project_is_still_included()
    {
        Write("src/P/P.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        Write("src/P/Sub/Code.cs", "namespace P; public class Sub { }");
        Write("App.slnx", "<Solution><Project Path=\"src/P/P.csproj\" /></Solution>");

        var scope = ProjectScope.Discover(_root);

        Assert.True(scope.Includes(Path_("src/P/Sub/Code.cs")));
    }

    // ------------------------------------------------------------------ safety

    [Fact]
    public void A_loose_file_is_excluded_once_the_repository_has_a_project()
    {
        Write("Scratch.cs", "public class Loose { }");
        Write("App.slnx", "<Solution><Project Path=\"src/Live/Live.csproj\" /></Solution>");

        var scope = ProjectScope.Discover(_root);

        // No csproj above it means no assembly to compile it into. The count is reported by doctor
        // rather than the file being dropped in silence.
        Assert.False(scope.Includes(Path_("Scratch.cs")));
        Assert.True(scope.HasProjects);
    }

    [Fact]
    public void Everything_excludes_nothing()
    {
        Write("App.slnx", "<Solution><Project Path=\"src/Live/Live.csproj\" /></Solution>");

        var scope = ProjectScope.Everything(_root);

        Assert.True(scope.Includes(Path_("src/Dead/Code.cs")));
        Assert.False(scope.ExcludesAnything);
        Assert.NotEmpty(scope.Decision);
    }

    [Fact]
    public void A_repository_with_no_project_files_is_indexed_whole()
    {
        File.Delete(Path_("src/Live/Live.csproj"));
        File.Delete(Path_("src/Dead/Dead.csproj"));

        var scope = ProjectScope.Discover(_root);

        Assert.True(scope.Includes(Path_("src/Dead/Code.cs")));
        Assert.False(scope.ExcludesAnything);
    }

    [Fact]
    public void Skipped_projects_are_named_rather_than_dropped_in_silence()
    {
        Write("App.slnx", "<Solution><Project Path=\"src/Live/Live.csproj\" /></Solution>");

        var scope = ProjectScope.Discover(_root);

        Assert.Single(scope.Excluded);
        Assert.Contains("Dead.csproj", scope.Excluded[0]);
        Assert.NotEmpty(scope.Reason);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}