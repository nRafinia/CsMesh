using System.Text.Json;
using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;

namespace CsMesh.Storage;

/// <summary>
/// Handles persistence and freshness tracking of the code graph on disk.
/// </summary>
public static class GraphStore
{
    public static string DirFor(string root) => Path.Combine(root, ".csmesh");
    public static string PathFor(string root) => Path.Combine(DirFor(root), "graph.json");

    /// <summary>
    /// The graph as it was before the current index. Kept so 'changes' can answer what the shape
    /// of the codebase did, not just what its text did.
    /// </summary>
    public static string PreviousPathFor(string root) => Path.Combine(DirFor(root), "graph.prev.json");

    /// <summary>
    /// Held for the whole of a write so two csmesh processes cannot interleave one.
    ///
    /// This is not hypothetical: CSMESH_AUTO_INDEX makes any query a potential writer, so a query
    /// in one terminal and an explicit 'csmesh index' in another are an ordinary pairing.
    /// </summary>
    private static string LockPathFor(string root) => Path.Combine(DirFor(root), "lock");

    private const int LockAttempts = 50;
    private const int LockWaitMs = 100;

    /// <summary>
    /// Takes the write lock, or returns null when it cannot be taken.
    ///
    /// Null is not a failure to report upward. A read-only checkout, an exotic filesystem or a
    /// container mount without file locking would all land here, and refusing to write in those
    /// cases would be a worse outcome than an unsynchronised write on a machine that has no
    /// second writer anyway. The atomic rename below is what actually protects the file; the lock
    /// is what stops two writers fighting over the rotation.
    /// </summary>
    private static FileStream? AcquireLock(string root)
    {
        var path = LockPathFor(root);

        for (var attempt = 0; attempt < LockAttempts; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // Another csmesh holds it. Wait rather than clobber.
                Thread.Sleep(LockWaitMs);
            }
            catch (Exception ex)
            {
                Dbg.Log($"graph lock unavailable, writing unsynchronised: {ex.Message}");
                return null;
            }
        }

        Dbg.Log($"graph lock still held after {LockAttempts * LockWaitMs}ms, writing unsynchronised");
        return null;
    }

    /// <summary>
    /// Serialises to a sibling temp file and renames it into place.
    ///
    /// File.Create truncates first and fills afterwards, so any reader arriving mid-write saw a
    /// half-written graph and any crash left one on disk permanently. A rename gives a reader
    /// either the whole old file or the whole new one.
    ///
    /// Windows will not let the rename happen while anything holds the destination open without
    /// FILE_SHARE_DELETE, which is why OpenReadShared exists below. That covers csmesh's own
    /// readers; it says nothing about an editor, a backup agent or a virus scanner that opened the
    /// file for its own reasons, so a brief retry follows. Linux has no such rule -- rename() does
    /// not consult open handles -- which is exactly why this was invisible until the suite ran on
    /// Windows.
    /// </summary>
    private static void WriteAtomic(Graph g, string destination)
    {
        var temp = destination + ".tmp-" + Environment.ProcessId + "-" + Environment.CurrentManagedThreadId;

        try
        {
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, g, AppJsonContext.Default.Graph);
            }

            Rename(temp, destination);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) { Dbg.Log($"could not clean temp graph: {ex.Message}"); }
            throw;
        }
    }

    private const int RenameAttempts = 20;
    private const int RenameWaitMs = 25;

    /// <summary>
    /// Moves the finished graph into place, waiting out a transient hold on the destination.
    ///
    /// Bounded on purpose. If something keeps the file open for half a second this gives up and
    /// lets the exception surface, because a write that silently did not happen is worse than one
    /// that failed loudly -- the next query would answer from the old graph and say it was current.
    /// </summary>
    private static void Rename(string temp, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                       && attempt < RenameAttempts)
            {
                Thread.Sleep(RenameWaitMs);
            }
        }
    }

    /// <summary>
    /// Opens the graph for reading without blocking a concurrent replacement.
    ///
    /// File.OpenRead asks for FileShare.Read, which on Windows is a promise that the file will not
    /// be deleted or renamed while the handle lives -- so a reader in one process makes the writer
    /// in another fail outright. Adding Delete is what lets the rename go through, and the reader
    /// keeps reading the old file it already opened.
    /// </summary>
    private static FileStream OpenReadShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static void Save(Graph g)
    {
        Directory.CreateDirectory(DirFor(g.Root));

        using var guard = AcquireLock(g.Root);

        var current = PathFor(g.Root);
        if (File.Exists(current))
        {
            try { File.Copy(current, PreviousPathFor(g.Root), overwrite: true); }
            catch (Exception ex) { Dbg.Log($"could not keep previous graph: {ex.Message}"); }
        }

        WriteAtomic(g, current);
    }

    /// <summary>
    /// Writes the graph without rotating the previous snapshot.
    ///
    /// Save keeps a copy so 'changes' can report what the shape of the codebase did. An
    /// incremental refresh runs often and touches little, and rotating on each one would leave
    /// 'changes' comparing a graph against a near-copy of itself and reporting that no binding
    /// moved -- the one answer it must never give wrongly.
    /// </summary>
    public static void SaveInPlace(Graph g)
    {
        Directory.CreateDirectory(DirFor(g.Root));

        using var guard = AcquireLock(g.Root);
        WriteAtomic(g, PathFor(g.Root));
    }

    /// <summary>
    /// Loads the snapshot from before the last index, or null when there is none or it was written
    /// by an incompatible version. Comparing across format versions would report every edge as
    /// both added and removed.
    /// </summary>
    public static Graph? LoadPrevious(string root)
    {
        var path = PreviousPathFor(root);
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = OpenReadShared(path);
            var graph = JsonSerializer.Deserialize(stream, AppJsonContext.Default.Graph);
            if (graph == null || graph.FormatVersion != Graph.CurrentFormatVersion) return null;

            graph.Freeze();
            return graph;
        }
        catch (Exception ex)
        {
            Dbg.Log($"previous graph load failed: {ex.Message}");
            return null;
        }
    }

    public static Graph? Load(string root) => Load(root, out _);

    /// <summary>
    /// Loads the graph, rejecting anything written by an incompatible version so that stale
    /// keying rules never produce quietly wrong answers.
    /// </summary>
    /// <param name="problem">Human readable reason when the result is null.</param>
    public static Graph? Load(string root, out string? problem)
    {
        var path = PathFor(root);
        if (!File.Exists(path))
        {
            problem = "MISSING";
            return null;
        }

        Graph? graph;
        try
        {
            using var stream = OpenReadShared(path);
            graph = JsonSerializer.Deserialize(stream, AppJsonContext.Default.Graph);
        }
        catch (Exception ex)
        {
            Dbg.Log($"graph load failed: {ex.Message}");
            problem = "UNREADABLE";
            return null;
        }

        if (graph == null)
        {
            problem = "UNREADABLE";
            return null;
        }

        if (graph.FormatVersion != Graph.CurrentFormatVersion)
        {
            problem = $"FORMAT v{graph.FormatVersion} (this build expects v{Graph.CurrentFormatVersion})";
            return null;
        }

        graph.Root = root;
        problem = null;
        return graph;
    }

    /// <summary>
    /// Whether this graph was written by a different csmesh build than the one now running.
    ///
    /// Deliberately not a load failure. The file is readable and its answers are the answers the
    /// older binary would have given, which is usually fine to look at. What it must not do is
    /// claim to be current: most releases change what the indexer notices without touching the
    /// schema, so the fix a user upgraded for would otherwise stay invisible behind a graph that
    /// reports itself as up to date. Callers use this to force a rebuild and to say why.
    ///
    /// An empty stamp means the graph predates this field, which is the same situation.
    /// </summary>
    public static bool BuiltByOtherVersion(Graph g) =>
        !string.Equals(g.BuiltByVersion, AppVersion.Get(), StringComparison.Ordinal);

    /// <summary>Describes the version gap in one clause, for the line that reports it.</summary>
    public static string VersionGap(Graph g)
    {
        var built = string.IsNullOrEmpty(g.BuiltByVersion) ? "an older build" : $"csmesh {g.BuiltByVersion}";
        return $"index was built by {built}, this is {AppVersion.Get()}";
    }

    /// <summary>
    /// Filesystems disagree about how precisely they keep a write time. exFAT rounds to two
    /// seconds, and several network and container mounts round or drift by similar amounts, so an
    /// exact tick comparison reports files as edited that nobody has touched -- which shows up as
    /// a permanent [STALE] on every query and a heal that never finishes healing.
    ///
    /// Size is checked first and exactly, so this only ever forgives a timestamp that moved while
    /// the byte count stayed identical. Set CSMESH_MTIME_EXACT=1 to compare ticks strictly.
    /// </summary>
    private static readonly long MTimeToleranceTicks =
        Environment.GetEnvironmentVariable("CSMESH_MTIME_EXACT") == "1"
            ? 0
            : TimeSpan.TicksPerSecond * 2;

    private static bool TimesDiffer(long a, long b) => Math.Abs(a - b) > MTimeToleranceTicks;

    /// <summary>
    /// Identifies files that have been modified, removed, or added since the index was created.
    /// The full tree walk only runs when a tracked directory's timestamp moved, which is what
    /// keeps a query at a few milliseconds on a large solution.
    /// </summary>
    public static List<string> DirtyFiles(Graph g)
    {
        var dirty = new List<string>();

        foreach (var file in g.Files)
        {
            var fullPath = Path.Combine(g.Root, file.Path);
            if (!File.Exists(fullPath))
            {
                dirty.Add(file.Path);
                continue;
            }

            var info = new FileInfo(fullPath);
            if (info.Length != file.Size || TimesDiffer(info.LastWriteTimeUtc.Ticks, file.Ticks))
            {
                dirty.Add(file.Path);
            }
        }

        if (DirectoriesChanged(g))
        {
            var knownPaths = g.Files.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var sourceFile in Indexer.EnumerateSourceFiles(g.Root))
            {
                var relative = Path.GetRelativePath(g.Root, sourceFile);
                if (!knownPaths.Contains(relative)) dirty.Add(relative);
            }
        }

        return dirty.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Adding, renaming or deleting a file updates its directory's write time, so this is a
    /// sufficient gate for "might there be a source file the index has never seen".
    /// </summary>
    private static bool DirectoriesChanged(Graph g)
    {
        if (g.Dirs.Count == 0) return true;

        foreach (var dir in g.Dirs)
        {
            var full = Path.GetFullPath(Path.Combine(g.Root, dir.Path));
            if (!Directory.Exists(full)) return true;

            try
            {
                if (TimesDiffer(Directory.GetLastWriteTimeUtc(full).Ticks, dir.Ticks)) return true;
            }
            catch
            {
                return true;
            }
        }

        return false;
    }
}
