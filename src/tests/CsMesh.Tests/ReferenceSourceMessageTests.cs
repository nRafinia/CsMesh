using CsMesh.Commands;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Packages come from two sources now, so the old "nothing from bin/, run dotnet build" advice is
/// wrong half the time: a restored tree resolves its packages from the assets files even with an
/// empty bin/, and an empty bin/ on a restored tree is not a build problem at all. These pin the
/// two messages apart: no package source at all, and unrestored projects.
/// </summary>
[Collection("console-capture")]
public sealed class ReferenceSourceMessageTests : IDisposable
{
    private readonly string _root;

    public ReferenceSourceMessageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-refmsg-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string Csproj =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
        "<ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable></PropertyGroup></Project>";

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

    private void Index()
    {
        Capture(() => IndexCommand.Execute(_root, new Options([])));
    }

    private string Doctor() => Capture(() => DoctorCommand.Execute(_root, new Options([])));

    [Fact]
    public void No_assets_and_no_bin_says_restore_or_build()
    {
        Write("App/App.csproj", Csproj);
        Write("App/Use.cs",
            "namespace App; public sealed class Use { public void Go() { Nope.Missing.Do(); } }");

        Index();
        var output = Doctor();

        Assert.Contains("NOTHING from bin/ or project.assets.json", output, StringComparison.Ordinal);
        Assert.Contains("dotnet restore", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrestored_project_is_named_and_the_fix_is_restore()
    {
        // A is restored: its assets file resolves an assembly that exists on disk and is not part
        // of the shared framework, so it counts as an assets reference.
        var existing = typeof(ReferenceSourceMessageTests).Assembly.Location;
        var folder = Path.GetDirectoryName(existing)!.Replace('\\', '/') + "/";
        var dll = Path.GetFileName(existing);
        Write("A/A.csproj", Csproj);
        Write("A/Thing.cs", "namespace A; public sealed class Thing { }");
        Write("A/obj/project.assets.json", $$"""
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Package.Lib/1.0.0": {
                    "type": "package",
                    "compile": { "{{dll}}": {} }
                  }
                }
              },
              "libraries": { "Package.Lib/1.0.0": { "type": "package", "path": "" } },
              "packageFolders": { "{{folder}}": {} },
              "project": { "frameworks": { "net10.0": {} } }
            }
            """);

        // B is not restored and calls a package type, so it stays unbound.
        Write("B/B.csproj", Csproj);
        Write("B/Use.cs",
            "namespace B; public sealed class Use { public void Go() { Nope.Missing.Do(); } }");

        Index();
        var output = Doctor();

        Assert.Contains("1 in-scope project(s) have no obj/project.assets.json", output, StringComparison.Ordinal);
        Assert.Contains("dotnet restore", output, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTHING from bin/", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_framework_named_package_asset_is_not_counted_as_a_new_reference()
    {
        // The assets file names a framework assembly; the runtime copy already supplies it, so the
        // references line must not count it again and the sources still sum to the compilation.
        var existing = typeof(object).Assembly.Location;
        var folder = Path.GetDirectoryName(existing)!.Replace('\\', '/') + "/";
        var dll = Path.GetFileName(existing);
        Write("A/A.csproj", Csproj);
        Write("A/Thing.cs", "namespace A; public sealed class Thing { }");
        Write("A/obj/project.assets.json", $$"""
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Package.Lib/1.0.0": {
                    "type": "package",
                    "compile": { "{{dll}}": {} }
                  }
                }
              },
              "libraries": { "Package.Lib/1.0.0": { "type": "package", "path": "" } },
              "packageFolders": { "{{folder}}": {} },
              "project": { "frameworks": { "net10.0": {} } }
            }
            """);

        Index();
        var output = Doctor();

        Assert.Contains("0 assets", output, StringComparison.Ordinal);
    }
}
