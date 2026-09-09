using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Three gaps found by review against a real 'dotnet build' rather than the handmade fixtures in
/// RazorComponentIndexingTests: a Razor Page/MVC view compiles through the same generator but to a
/// "*_cshtml.g.cs" file, never "*_razor.g.cs"; an edited .razor file whose generated C# was never
/// rebuilt was indexed anyway, and the freshness check it wrote afterwards came from the .razor
/// file's own current mtime, so the graph called itself clean while holding pre-edit content
/// forever; and a scaffold declaration Razor deliberately leaves unmapped (the class itself,
/// BuildRenderTree) was reported at its physical line in a ~100-line generated file, which is a
/// line number past the end of the ~10-line .razor file a reader is shown.
///
/// Every shape here -- the "*_cshtml.g.cs" name, the "#line (r,c)-(r,c) path" mapped directive
/// around @code content, the absence of any directive around scaffold declarations -- was checked
/// against real output from 'dotnet new blazor' and 'dotnet new webapp' built with
/// '--no-incremental -p:EmitCompilerGeneratedFiles=true'.
/// </summary>
public sealed class RazorCshtmlIndexingTests : IDisposable
{
    private readonly string _root;

    public RazorCshtmlIndexingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-cshtml-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "Pages"));
        Directory.CreateDirectory(Path.Combine(_root, "Services"));

        Write("App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write("Services/Greeter.cs", """
            namespace TestApp.Services
            {
                public class Greeter { public void Wave() { } }
            }
            """);
        Write("Pages/Index.cshtml", "<h1>Welcome</h1>");

        var generatedDir = Path.Combine(_root, "obj", "Debug", "net10.0", "generated",
            "Microsoft.CodeAnalysis.Razor.Compiler",
            "Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator");
        Directory.CreateDirectory(generatedDir);

        // The class name is mangled by the generator (Pages/Index.cshtml -> Pages_Index) and the
        // file lives flat under the generator's own directory, not nested by folder -- both taken
        // from a real build rather than assumed.
        var razorPath = Path.Combine(_root, "Pages", "Index.cshtml").Replace('\\', '/');
        File.WriteAllText(Path.Combine(generatedDir, "Pages_Index_cshtml.g.cs"), $$"""
            #pragma checksum "{{razorPath}}" "{8829d00f-11b8-4213-878b-770e8597ac16}" "0000"
            namespace TestApp.Pages
            {
                internal sealed class Pages_Index : global::Microsoft.AspNetCore.Mvc.RazorPages.Page
                {
                    private readonly TestApp.Services.Greeter _greeter = new TestApp.Services.Greeter();

                    public override async global::System.Threading.Tasks.Task ExecuteAsync()
                    {
                        _greeter.Wave();
                    }
                }
            }
            """);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void AGeneratedCshtmlSourceIsIndexed()
    {
        var graph = Indexer.Build(_root);

        Assert.Equal(1, graph.RazorFileCount);
        Assert.Equal(1, graph.RazorComponentsIndexed);
        Assert.Contains(graph.Nodes, n => n.Name == "TestApp.Pages.Pages_Index");
    }

    /// <summary>Distinct from razor-component: a View/Page is not a Blazor component.</summary>
    [Fact]
    public void TheCshtmlTypeIsTaggedRazorViewNotRazorComponent()
    {
        var graph = Indexer.Build(_root);
        var node = graph.Nodes.Single(n => n.Name == "TestApp.Pages.Pages_Index");

        Assert.Contains("razor-view", node.Tags);
        Assert.DoesNotContain("razor-component", node.Tags);
    }

    [Fact]
    public void TheNodeFileIsTheCshtmlSourceNotTheGeneratedFile()
    {
        var graph = Indexer.Build(_root);
        var node = graph.Nodes.Single(n => n.Name == "TestApp.Pages.Pages_Index");

        Assert.Equal(Path.Combine("Pages", "Index.cshtml"), node.File);
    }

    /// <summary>
    /// The actual failure this fixture reproduces: before the fix, doctor told the user to run
    /// exactly the build that had already produced this file, because the glob never matched it.
    /// </summary>
    [Fact]
    public void DoctorDoesNotClaimTheGeneratedOutputIsMissingWhenItIsPresent()
    {
        IndexCommand.Execute(_root, new Options([]));

        var original = Console.Out;
        var buffer = new StringWriter();
        string raw;
        try
        {
            Console.SetOut(buffer);
            DoctorCommand.Execute(_root, new Options([]));
        }
        finally
        {
            Console.SetOut(original);
            raw = buffer.ToString();
        }

        Assert.Contains("1 of 1 .razor/.cshtml file(s)", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("generator output was not found on disk", raw, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }
}

/// <summary>
/// A build that ran once and a .razor edit that never triggered another one leave obj/ holding
/// C# compiled from the file's pre-edit content. Indexing it regardless would not merely be stale
/// -- since the FileStamp this indexer writes afterwards comes from the .razor file's own current
/// mtime rather than the generated file's, the next freshness check finds nothing to disagree with
/// and calls the graph clean while it silently serves pre-edit content, permanently.
/// </summary>
public sealed class RazorStaleGeneratedSourceTests : IDisposable
{
    private readonly string _root;
    private readonly string _razorPath;
    private readonly string _generatedPath;

    public RazorStaleGeneratedSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-razorstale-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "Pages"));

        Write("App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        _razorPath = Path.Combine(_root, "Pages", "Counter.razor");
        File.WriteAllText(_razorPath, "<h1>@count</h1>");

        var generatedDir = Path.Combine(_root, "obj", "Debug", "net10.0", "generated",
            "Microsoft.CodeAnalysis.Razor.Compiler",
            "Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator", "Pages");
        Directory.CreateDirectory(generatedDir);

        _generatedPath = Path.Combine(generatedDir, "Counter_razor.g.cs");
        var razorAbs = _razorPath.Replace('\\', '/');
        File.WriteAllText(_generatedPath, $$"""
            #pragma checksum "{{razorAbs}}" "{ff1816ec-aa5e-4d10-87f6-980198115c8b}" "0000"
            namespace TestApp.Pages
            {
                public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase { }
            }
            """);

        // Both files are written back to back, so on a fast filesystem they can land in the same
        // tick. Force an unambiguous ordering rather than depend on wall-clock granularity: the
        // generated file is the older of the two, as if a build had produced it first.
        File.SetLastWriteTimeUtc(_generatedPath, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(_razorPath, DateTime.UtcNow);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void AGeneratedSourceOlderThanItsRazorFileIsSkippedNotIndexed()
    {
        var graph = Indexer.Build(_root);

        Assert.Equal(1, graph.RazorFileCount);
        Assert.Equal(0, graph.RazorComponentsIndexed);
        Assert.Equal(1, graph.RazorStaleSources);
        Assert.DoesNotContain(graph.Nodes, n => n.Name == "TestApp.Pages.Counter");
    }

    /// <summary>Once the generator actually reruns, the file is current again and indexes normally.</summary>
    [Fact]
    public void OnceTheGeneratedFileCatchesUpItIsIndexedAgain()
    {
        File.SetLastWriteTimeUtc(_generatedPath, DateTime.UtcNow.AddMinutes(10));

        var graph = Indexer.Build(_root);

        Assert.Equal(0, graph.RazorStaleSources);
        Assert.Equal(1, graph.RazorComponentsIndexed);
        Assert.Contains(graph.Nodes, n => n.Name == "TestApp.Pages.Counter");
    }

    [Fact]
    public void DoctorNamesTheStaleCountAndHowToClearIt()
    {
        IndexCommand.Execute(_root, new Options([]));

        var original = Console.Out;
        var buffer = new StringWriter();
        string raw;
        try
        {
            Console.SetOut(buffer);
            DoctorCommand.Execute(_root, new Options([]));
        }
        finally
        {
            Console.SetOut(original);
            raw = buffer.ToString();
        }

        Assert.Contains("razor stale", raw, StringComparison.Ordinal);
        Assert.Contains("csmesh index --full", raw, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }
}

/// <summary>
/// Razor maps @code content back to its real .razor line with a "#line" directive, which
/// GetMappedLineSpan() reads for free -- but only for the content Razor chooses to map. The
/// scaffolding around it (the class declaration, BuildRenderTree) is deliberately left unmapped,
/// and its physical position in the generated .g.cs is a line number that does not exist in the
/// much shorter .razor file a reader would actually open.
/// </summary>
public sealed class RazorLineMappingTests : IDisposable
{
    private readonly string _root;

    public RazorLineMappingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-razorlines-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "Pages"));

        Write("App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        // Line 4 is "private void Go()" -- the #line directive below points straight at it, the
        // same way a real build's directive points at a user's @code method.
        Write("Pages/Counter.razor", """
            <h1>@count</h1>

            @code {
                private void Go()
                {
                }
            }
            """);

        var generatedDir = Path.Combine(_root, "obj", "Debug", "net10.0", "generated",
            "Microsoft.CodeAnalysis.Razor.Compiler",
            "Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator", "Pages");
        Directory.CreateDirectory(generatedDir);

        var razorAbs = Path.Combine(_root, "Pages", "Counter.razor").Replace('\\', '/');
        File.WriteAllText(Path.Combine(generatedDir, "Counter_razor.g.cs"), $$"""
            #pragma checksum "{{razorAbs}}" "{ff1816ec-aa5e-4d10-87f6-980198115c8b}" "0000"
            namespace TestApp.Pages
            {
                public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase
                {
                    protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                    {
                    }
            #line (4,5)-(6,6) "{{razorAbs}}"
                    private void Go()
                    {
                    }
            #line default
            #line hidden
                }
            }
            """);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private CsMesh.Models.Node Node(string name)
    {
        var graph = Indexer.Build(_root);
        return graph.Nodes.FirstOrDefault(n => n.Name == name)
               ?? throw new InvalidOperationException($"no node named '{name}'. Have: " +
                                                        string.Join(", ", graph.Nodes.Select(n => n.Name)));
    }

    /// <summary>The failure this reproduces: line 95 reported in a file that has 7 lines.</summary>
    [Fact]
    public void AMappedCodeMethodReportsItsRealLineInTheRazorFile()
    {
        Assert.Equal(4, Node("TestApp.Pages.Counter.Go").Line);
    }

    /// <summary>
    /// No mapping is available for the class itself -- it corresponds to the whole file, not one
    /// line of it -- so it must not fall back to its physical line in the generated .g.cs, which
    /// would be a line number past the end of the 7-line .razor file.
    /// </summary>
    [Fact]
    public void AnUnmappedScaffoldDeclarationFallsBackToLineOneNotTheGeneratedFilesLine()
    {
        Assert.Equal(1, Node("TestApp.Pages.Counter").Line);
        Assert.Equal(1, Node("TestApp.Pages.Counter.BuildRenderTree").Line);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }
}
