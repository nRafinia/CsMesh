using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// graph.json is written while other csmesh processes may be reading it -- CSMESH_AUTO_INDEX makes
/// any query a potential writer, so a query in one terminal and an index in another is ordinary.
/// </summary>
public sealed class GraphWriteAtomicityTests
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
    /// Save used to truncate the destination and then fill it, with nothing serialising two
    /// writers. A reader arriving mid-write saw a prefix of a JSON document and a crash left one
    /// behind permanently.
    ///
    /// The window is proportional to how long serialising takes, so a four-node graph would close
    /// it before any reader could get in and the test would pass against the broken code. The
    /// graph is padded to a size where the write is measurable, which is also the size at which
    /// this actually bit people.
    /// </summary>
    [Fact]
    public void AReaderNeverSeesAHalfWrittenGraph()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();

        var template = graph.Nodes[0];
        for (var i = 0; i < 12_000; i++)
        {
            graph.Nodes.Add(new Node
            {
                Id = 100_000 + i,
                Key = $"Demo.Padding{i}",
                Name = $"Demo.Padding{i}",
                Short = $"Padding{i}",
                Kind = template.Kind,
                File = template.File,
                Line = i,
                Signature = $"public sealed class Padding{i}"
            });
        }

        GraphStore.Save(graph);

        var torn = 0;
        var reads = 0;

        Parallel.Invoke(
            () => { for (var i = 0; i < 10; i++) GraphStore.SaveInPlace(graph); },
            () => { for (var i = 0; i < 10; i++) GraphStore.SaveInPlace(graph); },
            () =>
            {
                // Deserialising 12k nodes 150 times costs seconds and proves nothing extra. A
                // torn write is detectable from the bytes: a truncated document stops mid-token,
                // so a complete one is empty of neither content nor its closing brace.
                var path = GraphStore.PathFor(sandbox.Root);

                for (var i = 0; i < 150; i++)
                {
                    // A read that could not start is not a torn read. On Windows ReplaceFile
                    // makes the destination briefly unopenable, so a reader hammering it in a
                    // loop is denied often -- 62 times out of 150 on the run that found this.
                    // That is the platform swapping the file correctly, which is the opposite of
                    // the defect. Only content that begins and stops mid-document counts.
                    byte[]? bytes = null;

                    for (var attempt = 0; attempt < 20 && bytes == null; attempt++)
                    {
                        try
                        {
                            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete);
                            using var memory = new MemoryStream();
                            stream.CopyTo(memory);
                            bytes = memory.ToArray();
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            Thread.Sleep(5);
                        }
                    }

                    if (bytes == null) continue;

                    Interlocked.Increment(ref reads);
                    if (bytes.Length == 0 || bytes[^1] != (byte)'}') Interlocked.Increment(ref torn);
                }
            });

        Assert.True(reads > 0);
        Assert.Equal(0, torn);

        // One full parse at the end, because "ends in a brace" is a cheap proxy and the graph
        // still has to be genuinely loadable when the dust settles.
        var final = GraphStore.Load(sandbox.Root, out var finalProblem);
        Assert.Null(finalProblem);
        Assert.NotNull(final);
        Assert.Equal(graph.Nodes.Count, final.Nodes.Count);

        var leftovers = Directory.EnumerateFiles(GraphStore.DirFor(sandbox.Root), "*.tmp-*").ToList();
        Assert.Empty(leftovers);
    }

    /// <summary>
    /// A write that exhausts all rename retries must leave the graph file byte-for-byte unchanged
    /// and must not leave a .tmp- orphan on disk.
    ///
    /// The failure mode this guards against: a write loses the rename race, the exception is
    /// swallowed, and the graph on disk is now a partial file claiming to be current -- meaning
    /// the next query answers from a torn graph and reports it as up to date. The correct outcome
    /// is that the old file survives intact, the temp is cleaned up, and LockContentedException
    /// propagates so RunGuarded can return exit 75 and tell the caller to retry.
    ///
    /// Simulated by pinning the destination open with a non-deletable handle for longer than the
    /// retry budget (RenameMaxAttempts * RenameMaxWaitMs). On Linux rename() does not consult
    /// open handles, so this test is Windows-only -- but that is exactly the platform where the
    /// failure mode bites, since MoveFileEx respects file handles and File.Replace does not recover
    /// when the destination is locked by a handle without FILE_SHARE_DELETE.
    /// </summary>
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AWriteThatLosesTheRaceLeavesPreviousGraphIntactAndNoOrphan()
    {
        if (!OperatingSystem.IsWindows()) return; // rename() is handle-agnostic on Unix

        using var sandbox = new Sandbox();
        var graph = sandbox.Index();
        GraphStore.Save(graph);

        var path = GraphStore.PathFor(sandbox.Root);
        var originalBytes = File.ReadAllBytes(path);

        // Hold the file open with a handle that blocks File.Replace / File.Move on Windows.
        // FileShare.None means no sharing at all: neither read nor delete, so any attempt to
        // replace the destination will fail with UnauthorizedAccessException or IOException
        // for longer than the 10-attempt retry budget.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<LockContentedException>(() => GraphStore.SaveInPlace(graph));
        }

        // The graph file must be unchanged.
        var afterBytes = File.ReadAllBytes(path);
        Assert.Equal(originalBytes, afterBytes);

        // No temp file should have been left behind.
        var orphans = Directory.EnumerateFiles(GraphStore.DirFor(sandbox.Root), "*.tmp-*").ToList();
        Assert.Empty(orphans);
    }

    /// <summary>
    /// A reader holding the graph open must not stop a write from completing.
    ///
    /// On Windows a handle opened with FileShare.Read is a promise the file will not be renamed
    /// while it lives, so the writer's File.Move fails outright with UnauthorizedAccessException.
    /// csmesh's own readers now ask for FileShare.Delete, but an editor, a backup agent or a virus
    /// scanner will not, and neither did File.ReadAllBytes in this very test file.
    ///
    /// On Linux this passes whether or not the fix is present, because rename() does not consult
    /// open handles -- which is precisely why the whole class of bug survived a green suite here
    /// and failed on the first Windows run.
    /// </summary>
    [Fact]
    public void AnExclusiveReaderDoesNotPreventTheWriteFromLanding()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();
        GraphStore.Save(graph);

        var path = GraphStore.PathFor(sandbox.Root);

        // Deliberately the restrictive share mode: this is what code outside csmesh does.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var writer = Task.Run(() => GraphStore.SaveInPlace(graph));

            // Long enough for the retry loop to be doing its work, short enough to stay ahead of
            // the point where it gives up.
            Thread.Sleep(120);

            Assert.False(writer.IsFaulted, writer.Exception?.GetBaseException().Message);
        }

        var reloaded = GraphStore.Load(sandbox.Root, out var problem);
        Assert.Null(problem);
        Assert.NotNull(reloaded);
    }
}
