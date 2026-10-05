using System.Text;
using CsMesh.Analysis;
using CsMesh.Models;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// "Stamp a file before reading it" makes a write that lands mid-index visible. The source read is
/// injectable, so the test can interleave a write between the stat and the read with no static hook
/// in production code, then assert the stamp records the pre-write time and the bytes that were
/// actually parsed.
/// </summary>
public sealed class StampOrderTests
{
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }
        public string Source { get; }
        public byte[] OriginalBytes { get; }
        public byte[] RewrittenBytes { get; }

        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "csmesh-stamp-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            Source = Path.Combine(Root, "src", "Thing.cs");

            File.WriteAllText(Source, """
                namespace Demo;
                public sealed class Thing { public void Go() { } }
                """);

            OriginalBytes = File.ReadAllBytes(Source);

            // One byte changed in the declared type name, so the byte count is identical and only a
            // content hash can separate the two revisions.
            RewrittenBytes = (byte[])OriginalBytes.Clone();
            var at = Array.IndexOf(RewrittenBytes, (byte)'T');
            Assert.True(at >= 0, "the sandbox source should contain the byte this test changes");
            RewrittenBytes[at] = (byte)'X';
            Assert.Equal(OriginalBytes.Length, RewrittenBytes.Length);
        }

        /// <summary>
        /// A reader standing in for the production one with a writer winning the race: it stats, then
        /// the writer lands the rewrite, then it reads. When <paramref name="torn"/> the read caught
        /// the pre-write revision, so the bytes it returns differ from the file's final bytes.
        /// </summary>
        public Func<string, Indexer.SourceRead> RacingReader(bool torn, out Func<long> preTicks)
        {
            long captured = 0;
            preTicks = () => captured;

            return path =>
            {
                var info = new FileInfo(path);
                captured = info.LastWriteTimeUtc.Ticks;
                var size = info.Length;
                var before = File.ReadAllBytes(path);

                File.WriteAllBytes(path, RewrittenBytes);

                if (torn)
                {
                    return new Indexer.SourceRead(captured, size, before, Encoding.UTF8.GetString(before));
                }

                var after = File.ReadAllBytes(path);
                return new Indexer.SourceRead(captured, size, after, Encoding.UTF8.GetString(after));
            };
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    private static string StampPath(string root) =>
        Path.Combine(root, "src", "Thing.cs");

    private static FileStamp Stamp(Graph graph) =>
        graph.Files.Single(f => f.Path.EndsWith("Thing.cs", StringComparison.Ordinal));

    /// <summary>The write lands after the stat and before the read, so the graph holds the writer's
    /// revision: the stamp must carry the pre-write time (or the delta would be zero and hide it) and
    /// the digest of the bytes parsed, and the file the graph describes makes it clean.</summary>
    [Fact]
    public void AWriteBetweenStatAndReadLeavesAPreWriteStampThatMatchesTheFile()
    {
        using var sandbox = new Sandbox();
        var reader = sandbox.RacingReader(torn: false, out var preTicks);

        var graph = Indexer.BuildWithScope(sandbox.Root, null, false, null, reader).Graph;
        var stamp = Stamp(graph);
        var expected = FileStamp.HashOf(sandbox.RewrittenBytes);

        Assert.Equal(preTicks(), stamp.Ticks);
        Assert.Equal(sandbox.OriginalBytes.Length, stamp.Size);
        Assert.Equal(expected, stamp.Hash);
        Assert.Equal(expected, FileStamp.HashOf(File.ReadAllBytes(sandbox.Source)));

        // Force the nonzero-delta band, so a clean verdict cannot come from the write landing on the
        // stamp's own tick.
        File.SetLastWriteTimeUtc(StampPath(sandbox.Root),
            new DateTime(stamp.Ticks + TimeSpan.TicksPerSecond, DateTimeKind.Utc));

        Assert.DoesNotContain(GraphStore.DirtyFiles(graph), d => d.EndsWith("Thing.cs", StringComparison.Ordinal));
    }

    /// <summary>A torn read returns bytes the file no longer has. The byte count still matches, so only
    /// the digest can tell the graph apart from the file, and it must report dirty.</summary>
    [Fact]
    public void ATornReadIsDirtyEvenThoughTheByteCountMatches()
    {
        using var sandbox = new Sandbox();
        var reader = sandbox.RacingReader(torn: true, out _);

        var graph = Indexer.BuildWithScope(sandbox.Root, null, false, null, reader).Graph;
        var stamp = Stamp(graph);

        Assert.Equal(FileStamp.HashOf(sandbox.OriginalBytes), stamp.Hash);
        Assert.NotEqual(stamp.Hash, FileStamp.HashOf(File.ReadAllBytes(sandbox.Source)));

        File.SetLastWriteTimeUtc(StampPath(sandbox.Root),
            new DateTime(stamp.Ticks + TimeSpan.TicksPerSecond, DateTimeKind.Utc));

        Assert.Contains(GraphStore.DirtyFiles(graph), d => d.EndsWith("Thing.cs", StringComparison.Ordinal));
    }

    /// <summary>The incremental stamp site reads through the same reader, so the ordering fix is not a
    /// full-build-only property.</summary>
    [Fact]
    public void TheIncrementalStampSiteReadsThroughTheSameReader()
    {
        using var sandbox = new Sandbox();
        var before = Indexer.Build(sandbox.Root);

        var reader = sandbox.RacingReader(torn: false, out var preTicks);
        var relative = Path.GetRelativePath(sandbox.Root, StampPath(sandbox.Root));

        var after = Indexer.BuildIncrementalWithScope(before, new[] { relative }, out var reason, null, reader);

        Assert.Null(reason);
        Assert.NotNull(after);
        var stamp = Stamp(after!.Graph);
        Assert.Equal(preTicks(), stamp.Ticks);
        Assert.Equal(FileStamp.HashOf(sandbox.RewrittenBytes), stamp.Hash);
    }
}
