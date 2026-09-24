using System.Text.Json;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A restore rewrites a project's obj/project.assets.json and moves no .cs file. Without the assets
/// file in the freshness list, that rewrite was invisible: the index reported itself current while
/// still compiled against the packages an earlier restore resolved. These pin that the assets file
/// is a freshness input and that the base cache identity changes with it.
/// </summary>
[Collection("console-capture")]
public sealed class AssetsFreshnessTests : IDisposable
{
    private readonly string _root;

    public AssetsFreshnessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-assets-fresh-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string Assets(string version)
    {
        var folder = Path.GetTempPath().Replace('\\', '/');
        return $$"""
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Package.Lib/{{version}}": {
                    "type": "package",
                    "compile": { "lib/net10.0/Package.Lib.dll": {} }
                  }
                }
              },
              "libraries": {
                "Package.Lib/{{version}}": { "type": "package", "path": "package.lib/{{version}}" }
              },
              "packageFolders": { "{{folder}}": {} },
              "project": { "frameworks": { "net10.0": {} } }
            }
            """;
    }

    private void WriteProject()
    {
        Write("App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            "<ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        Write("App/Thing.cs", "namespace App; public sealed class Thing { }");
    }

    private static T Parse<T>(string raw, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        return JsonSerializer.Deserialize(lines[0], info)!;
    }

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
    public void A_changed_assets_file_forces_a_full_pass_rather_than_current()
    {
        WriteProject();
        Write("App/obj/project.assets.json", Assets("1.0.0"));

        Capture(() => IndexCommand.Execute(_root, new Options([])));

        // Rewrite only the assets file; no .cs moves.
        Write("App/obj/project.assets.json", Assets("2.0.0-with-a-longer-version"));

        var raw = Capture(() => IndexCommand.Execute(_root, new Options(["--json"])));
        var report = Parse(raw, AppJsonContext.Default.IndexReport);

        Assert.Equal("full", report.Mode);
    }

    [Fact]
    public void An_unchanged_tree_is_still_reported_current()
    {
        WriteProject();
        Write("App/obj/project.assets.json", Assets("1.0.0"));

        Capture(() => IndexCommand.Execute(_root, new Options([])));

        var raw = Capture(() => IndexCommand.Execute(_root, new Options(["--json"])));
        var report = Parse(raw, AppJsonContext.Default.IndexReport);

        Assert.Equal("current", report.Mode);
    }

    [Fact]
    public void The_review_base_reference_key_tracks_the_assets_file()
    {
        WriteProject();
        Write("App/obj/project.assets.json", Assets("1.0.0"));

        var before = GraphStore.ReferenceKeyFor(_root);

        Write("App/obj/project.assets.json", Assets("2.0.0-with-a-longer-version"));

        var after = GraphStore.ReferenceKeyFor(_root);

        Assert.NotEqual(before, after);
    }
}
