using CsMesh.Analysis;
using CsMesh.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A class library does not copy its package closure to <c>bin/</c>, so the reference set used to
/// depend on whether some app project in the tree happened to copy a package. These pin the
/// replacement: a project compiles against the compile assets its own <c>obj/project.assets.json</c>
/// names, even with no <c>bin/</c> at all.
///
/// The package assembly is emitted in-test into a local package folder written by the same test, so
/// the fixture restores from no feed and references no NuGet package from a source.
/// </summary>
[Collection("console-capture")]
public sealed class PackageAssetsReferenceTests : IDisposable
{
    private readonly string _root;

    public PackageAssetsReferenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-assets-" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>
    /// Emits one package assembly that the fixture's hand-written assets file names, and that no
    /// build copies to <c>bin/</c>.
    /// </summary>
    private string EmitPackageAssembly()
    {
        var dll = Path.Combine(_root, "packages", "package.lib", "1.0.0", "lib", "net10.0", "Package.Lib.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);

        var tree = CSharpSyntaxTree.ParseText(
            "namespace PackageLib { public static class Greeter { public static string Hello() => \"hi\"; } }");

        var compilation = CSharpCompilation.Create(
            "Package.Lib",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = compilation.Emit(dll);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        return Path.Combine(_root, "packages");
    }

    private void WriteAssets(string packageFolder)
    {
        var folder = packageFolder.Replace('\\', '/') + "/";
        Write("App/obj/project.assets.json", $$"""
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Package.Lib/1.0.0": {
                    "type": "package",
                    "compile": { "lib/net10.0/Package.Lib.dll": {} }
                  }
                }
              },
              "libraries": {
                "Package.Lib/1.0.0": { "type": "package", "path": "package.lib/1.0.0" }
              },
              "packageFolders": { "{{folder}}": {} },
              "project": { "frameworks": { "net10.0": {} } }
            }
            """);
    }

    [Fact]
    public void A_package_type_binds_from_the_assets_file_when_bin_is_empty()
    {
        var packageFolder = EmitPackageAssembly();
        WriteAssets(packageFolder);

        Write("App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            "<ImplicitUsings>enable</ImplicitUsings><Nullable>disable</Nullable></PropertyGroup></Project>");
        Write("App/Program.cs",
            "namespace App { public static class Program { public static string Run() => PackageLib.Greeter.Hello(); } }");

        var graph = Indexer.Build(_root);
        graph.Freeze();

        // The call binds when the package assembly is a reference. An external method gets no call
        // edge -- only in-source targets do -- so a bound call is one that is not among the
        // unresolved sites.
        Assert.True(graph.TotalCallSites >= 1);
        Assert.Equal(0, graph.UnresolvedCallSites);
    }

    [Fact]
    public void A_package_type_is_unbound_without_an_assets_file_and_without_bin()
    {
        Write("App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            "<ImplicitUsings>enable</ImplicitUsings><Nullable>disable</Nullable></PropertyGroup></Project>");
        Write("App/Program.cs",
            "namespace App { public static class Program { public static string Run() => PackageLib.Greeter.Hello(); } }");

        var graph = Indexer.Build(_root);
        graph.Freeze();

        Assert.True(graph.UnresolvedCallSites > 0);
    }
}
