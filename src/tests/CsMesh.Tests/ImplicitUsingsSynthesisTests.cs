using CsMesh.Analysis;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The SDK writes its implicit global usings to obj/ only when a build ran non-incrementally with
/// EmitCompilerGeneratedFiles. A project with no obj/ therefore had no implicit set at all, and
/// once the index stops treating every other project's set as its own, List&lt;&gt; and Task stop
/// binding in it entirely. The set can be reconstructed from the two things the csproj states: the
/// ImplicitUsings opt-in and the Sdk attribute.
///
/// The Web SDK adds the ASP.NET Core namespaces; the plain SDK does not. Synthesizing the Web set
/// for a plain project hands it types its real build cannot see, which is a false binding rather
/// than a missing one.
/// </summary>
public sealed class ImplicitUsingsSynthesisTests : IDisposable
{
    private readonly string _root;

    public ImplicitUsingsSynthesisTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-usings-" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public void A_plain_project_with_no_obj_binds_the_base_set_and_not_the_web_set()
    {
        Write("Plain.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>" +
            "</PropertyGroup></Project>");
        Write("Use.cs", """
            namespace App
            {
                public class Use
                {
                    public List<string> Names { get; set; }
                    public WebApplication App { get; set; }
                }
            }
            """);

        var graph = Indexer.Build(_root);
        graph.Freeze();

        // List<> arrives through the synthesized base set; WebApplication does not, because this is
        // not the Web SDK.
        Assert.DoesNotContain(graph.Unresolved, u => u.Expression == "List");
        Assert.Contains(graph.Unresolved, u => u.Expression == "WebApplication");
    }

    [Fact]
    public void A_web_project_with_no_obj_gets_the_aspnet_namespaces()
    {
        Write("Web.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup>" +
            "<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>" +
            "</PropertyGroup></Project>");
        Write("Use.cs", "namespace App { public class Use { public WebApplication App { get; set; } } }");

        var graph = Indexer.Build(_root);
        graph.Freeze();

        Assert.DoesNotContain(graph.Unresolved, u => u.Expression == "WebApplication");
    }

    /// <summary>
    /// ImplicitUsings is opt-in. A csproj that does not ask for it gets no synthesized set, even
    /// with no obj/, which is what the property being absent means to the SDK.
    /// </summary>
    [Fact]
    public void A_project_that_does_not_opt_in_gets_no_synthesized_set()
    {
        Write("Plain.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFramework>net10.0</TargetFramework>" +
            "</PropertyGroup></Project>");
        Write("Use.cs", "namespace App { public class Use { public List<string> Names { get; set; } } }");

        var graph = Indexer.Build(_root);
        graph.Freeze();

        Assert.Contains(graph.Unresolved, u => u.Expression == "List");
    }

    [Fact]
    public void The_synthesized_text_adds_aspnet_only_for_the_web_sdk()
    {
        var plain = Write("Plain.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var web = Write("Web.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        Assert.DoesNotContain("Microsoft.AspNetCore.Builder", ProjectTfm.Synthesize(plain), StringComparison.Ordinal);
        Assert.Contains("Microsoft.AspNetCore.Builder", ProjectTfm.Synthesize(web), StringComparison.Ordinal);
        Assert.Contains("global using global::System;", ProjectTfm.Synthesize(plain), StringComparison.Ordinal);
    }
}
