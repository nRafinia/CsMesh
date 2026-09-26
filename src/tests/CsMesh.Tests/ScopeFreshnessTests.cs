using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A source file the index deliberately left out must not be mistaken for one added since the index
/// merely because a directory timestamp moved. The new-file walk used to enumerate every project, so
/// an out-of-scope project's files forced an incremental pass on a tree nobody had edited.
/// </summary>
[Collection("console-capture")]
public sealed class ScopeFreshnessTests : IDisposable
{
    private readonly string _root;

    public ScopeFreshnessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-scopefresh-" + Guid.NewGuid().ToString("N")[..8]);

        Write("App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        Write("App/Code.cs", "namespace App; public class A { }");
        Write("Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        Write("Lib/Code.cs", "namespace Lib; public class OutOfScopeMarker { }");

        // Names only a project that does not exist, so the scope falls back to the closure from the
        // executable root and the library is left out -- the shape that triggered the flake.
        Write("App.slnx", "<Solution><Project Path=\"Nowhere/Gone.csproj\" /></Solution>");
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

    private static (string Output, int Exit) Capture(Func<int> run)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(buffer);
            exit = run();
        }
        finally
        {
            Console.SetOut(original);
        }

        return (buffer.ToString(), exit);
    }

    private IndexReport Index() =>
        System.Text.Json.JsonSerializer.Deserialize(
            Capture(() => IndexCommand.Execute(_root, new Options(["--json", "--no-telemetry"]))).Output.Trim(),
            AppJsonContext.Default.IndexReport)!;

    private Graph Load() => GraphStore.Load(_root, out _)!;

    private void MoveRootStampAhead() =>
        Directory.SetLastWriteTimeUtc(_root, Directory.GetLastWriteTimeUtc(_root).AddSeconds(5));

    [Fact]
    public void An_out_of_scope_project_is_not_reported_new_when_the_root_stamp_moves()
    {
        Assert.Equal("full", Index().Mode);
        Assert.DoesNotContain(Load().Files, f => f.Path.Contains("Lib", StringComparison.Ordinal));

        MoveRootStampAhead();

        Assert.Equal("current", Index().Mode);
        Assert.DoesNotContain(Load().Files, f => f.Path.Contains("Lib", StringComparison.Ordinal));
    }

    [Fact]
    public void A_new_project_not_in_the_skipped_set_is_still_reported_new()
    {
        Assert.Equal("full", Index().Mode);

        Write("Extra/Extra.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        Write("Extra/Code.cs", "namespace Extra; public class Added { }");
        MoveRootStampAhead();

        Assert.NotEqual("current", Index().Mode);
    }
}
