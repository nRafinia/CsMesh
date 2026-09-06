using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// FormatVersion only moves when the schema does. Most releases change what the indexer notices
/// without touching it, so without a record of the build an old graph reports itself as current.
/// </summary>
public sealed class GraphVersionStampTests
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
    /// FormatVersion only moves when the schema does, which is the rarer event. Without a record
    /// of the build, an index written before a detection was added deserializes cleanly and gets
    /// announced as current -- so the fix the user upgraded for stays invisible.
    /// </summary>
    [Fact]
    public void FullIndexRecordsTheBuildThatWroteIt()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();

        Assert.Equal(AppVersion.Get(), graph.BuiltByVersion);
        Assert.False(GraphStore.BuiltByOtherVersion(graph));
    }
    /// <summary>
    /// FormatVersion only moves when the schema does, which is the rarer event. Without a record
    /// of the build, an index written before a detection was added deserializes cleanly and gets
    /// announced as current -- so the fix the user upgraded for stays invisible.
    /// </summary>
    [Fact]
    public void AGraphFromAnotherBuildIsNotConsideredCurrent()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();

        graph.BuiltByVersion = "0.0.1-ancient";
        Assert.True(GraphStore.BuiltByOtherVersion(graph));
        Assert.Contains("0.0.1-ancient", GraphStore.VersionGap(graph), StringComparison.Ordinal);
    }
    /// <summary>A graph predating the field is in the same position as one from an older build.</summary>
    [Fact]
    public void AGraphWithNoVersionStampIsTreatedAsStale()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();

        graph.BuiltByVersion = string.Empty;
        Assert.True(GraphStore.BuiltByOtherVersion(graph));
    }
    /// <summary>A graph predating the field is in the same position as one from an older build.</summary>
    [Fact]
    public void TheVersionStampSurvivesARoundTrip()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();
        GraphStore.Save(graph);

        var reloaded = GraphStore.Load(sandbox.Root, out var problem);

        Assert.Null(problem);
        Assert.NotNull(reloaded);
        Assert.Equal(AppVersion.Get(), reloaded.BuiltByVersion);
    }
}
