using System.Reflection.PortableExecutable;
using CsMesh.Analysis;
using CsMesh.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A project that was retargeted leaves more than one framework tree behind: <c>obj/Debug/net8.0</c>
/// survives beside <c>obj/Debug/net10.0</c>, and so does a <c>bin/Debug/net8.0</c> holding an old
/// copy of every package. The build reads one of them. The indexer used to read all of them, which
/// imports namespaces the current build never imports and can shadow a current assembly with a
/// stale one of the same simple name.
///
/// These pin the choice to the csproj's declared framework, with the newest on-disk tree only as
/// the fallback for a project whose declared framework was never built.
/// </summary>
public sealed class ProjectTfmSelectionTests : IDisposable
{
    private readonly string _root;

    public ProjectTfmSelectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-tfm-" + Guid.NewGuid().ToString("N")[..8]);
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

    private const string AppProject =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
        "<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>" +
        "</PropertyGroup></Project>";

    /// <summary>
    /// The stale framework's global-using set must not be compiled. With both sets in the
    /// compilation, <c>Marker</c> resolves to two namespaces and the object creation binds to
    /// neither; with only the declared framework's set, it resolves to the fresh one.
    /// </summary>
    [Fact]
    public void Stale_target_framework_global_usings_are_not_compiled()
    {
        Write("App.csproj", AppProject);
        Write("Types.cs", """
            namespace FreshNs { public class Marker { } }
            namespace StaleNs { public class Marker { } }
            namespace App { public class Use { public void Go() { var m = new Marker(); } } }
            """);
        Write("obj/Debug/net10.0/App.GlobalUsings.g.cs", "global using global::FreshNs;\n");
        Write("obj/Debug/net8.0/App.GlobalUsings.g.cs", "global using global::StaleNs;\n");

        var graph = Indexer.Build(_root);
        graph.Freeze();

        Assert.Contains(graph.Edges, e =>
            graph.ById(e.From)?.Short == "Use.Go" && graph.ById(e.To)?.Name == "FreshNs.Marker");
        Assert.DoesNotContain(graph.Edges, e => graph.ById(e.To)?.Name == "StaleNs.Marker");
    }

    /// <summary>
    /// The stale framework's assemblies must not be referenced. Both types are fully qualified, so
    /// only the reference set decides which one binds; the fresh type comes from the declared
    /// framework's DLL and the stale one from a sibling that must stay out.
    /// </summary>
    [Fact]
    public void Stale_target_framework_assemblies_are_not_referenced()
    {
        Write("App.csproj", AppProject);
        Write("Use.cs", """
            namespace App
            {
                public class Use
                {
                    public Fresh.Ns.FreshThing A { get; set; }
                    public Stale.Ns.StaleThing B { get; set; }
                }
            }
            """);
        Emit("bin/Debug/net10.0/Fresh.dll", "namespace Fresh.Ns { public class FreshThing { } }");
        Emit("bin/Debug/net8.0/Stale.dll", "namespace Stale.Ns { public class StaleThing { } }");

        var graph = Indexer.Build(_root);
        graph.Freeze();

        Assert.DoesNotContain(graph.Unresolved, u => u.Kind == "type" && u.Expression.Contains("FreshThing"));
        Assert.Contains(graph.Unresolved, u => u.Kind == "type" && u.Expression.Contains("StaleThing"));
    }

    private void Emit(string relative, string source)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var runtime = RuntimeLocator.FindSharedFramework();
        Assert.NotNull(runtime);

        var references = Directory.GetFiles(runtime!, "*.dll")
            .Where(IsManaged)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(path),
            [CSharpSyntaxTree.ParseText(source, path: "library.cs")],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("; ",
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage()).Take(3)));
    }

    private static bool IsManaged(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.HasMetadata;
        }
        catch
        {
            return false;
        }
    }
}
