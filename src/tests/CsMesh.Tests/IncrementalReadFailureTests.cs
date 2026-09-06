using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A file the incremental pass could not read used to disappear from tracking entirely: its stamp
/// never reached the fresh list, the fresh list replaced Files wholesale, and with the old stamp
/// gone nothing could ever notice the file again.
/// </summary>
public sealed class IncrementalReadFailureTests
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
    /// The parse loop used to swallow a read failure. The file's stamp then never reached the
    /// fresh list, the fresh list replaced Files wholesale, and with the old stamp gone nothing
    /// could ever notice the file again -- its symbols stayed frozen in the graph and no query
    /// ever called them stale. Declining is the only honest outcome: the caller falls back to a
    /// full index.
    /// </summary>
    [Fact]
    public void AnUnreadableSourceFileMakesTheIncrementalPassDecline()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();
        graph.Root = sandbox.Root;

        var edited = Path.Combine(sandbox.Root, "src", "Thing.cs");
        File.AppendAllText(edited, "\npublic sealed class Extra { }\n");

        // A dangling symlink is enumerated as a .cs file and throws on read, which is the shape
        // of every real cause here -- a lock, a permission, a vanished network mount.
        var broken = Path.Combine(sandbox.Root, "src", "Ghost.cs");
        File.CreateSymbolicLink(broken, Path.Combine(sandbox.Root, "src", "does-not-exist.cs"));

        var dirty = GraphStore.DirtyFiles(graph);
        Assert.NotEmpty(dirty);

        Assert.Null(Indexer.BuildIncremental(graph, dirty));
    }
}
