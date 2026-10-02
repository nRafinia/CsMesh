using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Filesystems disagree about how precisely they keep a write time, and an exact tick comparison
/// calls untouched files edited -- a permanent [STALE] and a heal that never finishes. Since v15 the
/// tolerance is settled by content: a file whose timestamp moved inside the band but whose bytes did
/// not stays clean, and a same-size edit inside it is caught instead of answered stale in silence.
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

            // A second plain file so the incremental test has an untouched stamp to carry.
            File.WriteAllText(Path.Combine(Root, "src", "Other.cs"), """
                namespace Demo;
                public sealed class Other { public void Do() { } }
                """);
        }

        public Graph Index() => Indexer.Build(Root);

        public FileStamp Stamp(Graph graph, string name) =>
            graph.Files.Single(f => f.Path.EndsWith(name, StringComparison.Ordinal));

        public string PathOf(string name) =>
            Path.Combine(Root, "src", name);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    /// <summary>Writes the exact timestamp a stamp holds, so a test controls the band it lands in.</summary>
    private static void SetMTime(string path, long ticks) =>
        File.SetLastWriteTimeUtc(path, new DateTime(ticks, DateTimeKind.Utc));

    /// <summary>Changes one byte in place, so the byte count is untouched and only the digest moves.</summary>
    private static void FlipOneByte(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var at = Array.IndexOf(bytes, (byte)'G');
        Assert.True(at >= 0, "the sandbox source should contain the letter this test changes");
        bytes[at] = (byte)'H';
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// exFAT rounds write times to two seconds and several network and container mounts round or
    /// drift similarly. An exact tick comparison calls those files edited, which shows as a
    /// permanent [STALE] and a heal that never finishes. The content hash is what now distinguishes
    /// this from a real edit: the timestamp moved, the bytes did not.
    /// </summary>
    [Fact]
    public void ASubGranularityTimestampShiftIsNotAnEdit()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();

        var stamp = sandbox.Stamp(graph, "Thing.cs");
        stamp.Ticks -= TimeSpan.TicksPerSecond;

        Assert.DoesNotContain(GraphStore.DirtyFiles(graph), d => d.EndsWith("Thing.cs", StringComparison.Ordinal));
    }

    /// <summary>The tolerance must not swallow a real edit: size is still compared exactly.</summary>
    [Fact]
    public void AChangedSizeIsStillDirtyWithinTheTolerance()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();

        var stamp = sandbox.Stamp(graph, "Thing.cs");
        stamp.Ticks -= TimeSpan.TicksPerSecond;
        stamp.Size -= 1;

        Assert.Contains(GraphStore.DirtyFiles(graph), d => d.EndsWith("Thing.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void ATimestampBeyondTheToleranceIsStillDirty()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();

        var stamp = sandbox.Stamp(graph, "Thing.cs");
        stamp.Ticks -= TimeSpan.TicksPerSecond * 30;

        Assert.Contains(GraphStore.DirtyFiles(graph), d => d.EndsWith("Thing.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// The silent stale answer this format exists to end: size and time both say "unchanged", so
    /// before the content hash a same-size edit inside the tolerance was called clean and the query
    /// served the pre-edit symbol with no [STALE] to warn the caller.
    /// </summary>
    [Fact]
    public void ASameSizeEditInTheToleranceBandIsDirty()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();
        var path = sandbox.PathOf("Thing.cs");

        var stamp = sandbox.Stamp(graph, "Thing.cs");
        var before = File.ReadAllBytes(path).Length;
        FlipOneByte(path);
        SetMTime(path, stamp.Ticks + TimeSpan.TicksPerSecond);
        Assert.Equal(before, File.ReadAllBytes(path).Length);

        Assert.Contains(GraphStore.DirtyFiles(graph), d => d.EndsWith("Thing.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of the band: the timestamp moved and the bytes are identical, so the content
    /// hash keeps the exFAT forgiveness that an unconditional "timestamp moved means dirty" would
    /// throw away. The read-back delta is asserted so the test cannot pass by landing outside the
    /// band it means to exercise.
    /// </summary>
    [Fact]
    public void IdenticalBytesInTheToleranceBandAreClean()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();
        var path = sandbox.PathOf("Thing.cs");

        var stamp = sandbox.Stamp(graph, "Thing.cs");
        SetMTime(path, stamp.Ticks + TimeSpan.TicksPerSecond);

        var delta = File.GetLastWriteTimeUtc(path).Ticks - stamp.Ticks;
        Assert.InRange(delta, 1, TimeSpan.TicksPerSecond * 2);

        Assert.DoesNotContain(GraphStore.DirtyFiles(graph), d => d.EndsWith("Thing.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// A zero delta is clean without opening the file. Proving "not opened" with no production seam:
    /// the stored digest is replaced with a value the file cannot match, so if the band path read
    /// and hashed the file it would have to report dirty. It reports clean, which is only possible
    /// because the digest was never consulted.
    /// </summary>
    [Fact]
    public void AnExactTimestampMatchIsCleanWithoutReadingTheFile()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();

        var stamp = sandbox.Stamp(graph, "Thing.cs");
        stamp.Hash = "sentinel-that-cannot-match";

        Assert.DoesNotContain(GraphStore.DirtyFiles(graph), d => d.EndsWith("Thing.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// The incremental pass parses every file, so it must not pay to rehash the ones it did not
    /// rebind. A sentinel digest on an untouched file survives the pass -- proof it was carried, not
    /// recomputed -- while the edited file's digest is refreshed to the bytes just written.
    /// </summary>
    [Fact]
    public void AnIncrementalPassCarriesTheHashOfAnUntouchedFileAndRefreshesTheEditedOne()
    {
        using var sandbox = new Sandbox();
        var before = sandbox.Index();

        var editedBefore = sandbox.Stamp(before, "Thing.cs");
        var untouched = sandbox.Stamp(before, "Other.cs");
        Assert.NotEqual(string.Empty, untouched.Hash);

        untouched.Hash = "sentinel-that-must-survive";

        var editedPath = sandbox.PathOf("Thing.cs");
        File.AppendAllText(editedPath, "\n// edited\n");

        var after = Indexer.BuildIncremental(before, GraphStore.DirtyFiles(before));
        Assert.NotNull(after);

        Assert.Equal("sentinel-that-must-survive", sandbox.Stamp(after!, "Other.cs").Hash);
        Assert.Equal(FileStamp.HashOf(File.ReadAllBytes(editedPath)), sandbox.Stamp(after!, "Thing.cs").Hash);
        Assert.NotEqual(editedBefore.Hash, sandbox.Stamp(after!, "Thing.cs").Hash);
    }

    /// <summary>
    /// The root directory stamp must be taken after the index's own .csmesh exists. Otherwise the
    /// first index creates .csmesh after recording the stamp, the root's timestamp moves, and the
    /// next run believes a tracked directory changed when nothing did.
    /// </summary>
    [Fact]
    public void TheRootStampIsTakenAfterTheIndexDirectoryExists()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();
        GraphStore.Save(graph);

        var stamp = graph.Dirs.Single(d => d.Path is "." or "");
        Assert.Equal(Directory.GetLastWriteTimeUtc(sandbox.Root).Ticks, stamp.Ticks);
    }
}
