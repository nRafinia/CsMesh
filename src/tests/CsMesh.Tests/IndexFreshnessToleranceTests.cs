using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Filesystems disagree about how precisely they keep a write time, and an exact tick comparison
/// calls untouched files edited -- a permanent [STALE] and a heal that never finishes.
/// </summary>
public sealed class IndexFreshnessToleranceTests
{
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }

        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "csmesh-store-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(Root, "src"));

            // Deliberately free of interfaces, abstracts and registrations: every one of those
            // is a CrossFileConstruct that makes the incremental pass decline before it reads a
            // single file, which would let these tests pass without exercising anything.
            File.WriteAllText(Path.Combine(Root, "src", "Thing.cs"), """
                namespace Demo;
                public sealed class Thing { public void Go() { } }
                public sealed class Caller { public void Run(Thing t) => t.Go(); }
                """);
        }

        public Graph Index() => Indexer.Build(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }


    /// <summary>
    /// exFAT rounds write times to two seconds and several network and container mounts round or
    /// drift similarly. An exact tick comparison calls those files edited, which shows as a
    /// permanent [STALE] and a heal that never finishes.
    /// </summary>
    [Fact]
    public void ASubGranularityTimestampShiftIsNotAnEdit()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();

        var stamp = graph.Files.Single(f => f.Path.EndsWith("Thing.cs", StringComparison.Ordinal));
        stamp.Ticks -= TimeSpan.TicksPerSecond;

        Assert.DoesNotContain(GraphStore.DirtyFiles(graph), d => d.EndsWith("Thing.cs", StringComparison.Ordinal));
    }
    /// <summary>The tolerance must not swallow a real edit: size is still compared exactly.</summary>
    [Fact]
    public void AChangedSizeIsStillDirtyWithinTheTolerance()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();

        var stamp = graph.Files.Single(f => f.Path.EndsWith("Thing.cs", StringComparison.Ordinal));
        stamp.Ticks -= TimeSpan.TicksPerSecond;
        stamp.Size -= 1;

        Assert.Contains(GraphStore.DirtyFiles(graph), d => d.EndsWith("Thing.cs", StringComparison.Ordinal));
    }
    [Fact]
    public void ATimestampBeyondTheToleranceIsStillDirty()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();

        var stamp = graph.Files.Single(f => f.Path.EndsWith("Thing.cs", StringComparison.Ordinal));
        stamp.Ticks -= TimeSpan.TicksPerSecond * 30;

        Assert.Contains(GraphStore.DirtyFiles(graph), d => d.EndsWith("Thing.cs", StringComparison.Ordinal));
    }
}
