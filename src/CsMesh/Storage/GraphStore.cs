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
    /// Takes the write lock, or reports that it could not be taken.
    ///
    /// The lock is what stops two writers fighting over the rotation; the atomic rename below is
    /// what protects the file itself. This returns null only for a lock that cannot be taken at
    /// all -- a read-only checkout, an exotic filesystem or a container mount without file
    /// locking. There is no second writer to serialise against in those cases, so the caller writes
    /// unsynchronised and the rename still keeps the file whole.
    ///
    /// A lock that is <em>held</em> is contention, not a filesystem quirk, and this commits the
    /// write path to treating it as such: it never returns null for a held lock, because doing so
    /// let the caller write beside the holder -- on Windows two processes replacing one
    /// destination, on any platform a writer that overwrote the result it waited out. Instead it
    /// raises <see cref="LockContentedException"/> so the runner answers <see cref="Exit.Contended"/>
    /// (retry). With <paramref name="wait"/> true a held lock is retried up to
    /// <see cref="LockAttempts"/> × <see cref="LockWaitMs"/> ms first; with false it raises at
    /// once, which is what the implicit heal needs so a query never stalls behind another writer.
    /// </summary>
    private static FileStream? AcquireLock(string root, bool wait)
    {
        var path = LockPathFor(root);
        Exception? lastContention = null;

        for (var attempt = 0; attempt < (wait ? LockAttempts : 1); attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another csmesh holds it. Wait rather than clobber, unless the caller asked not to.
                //
                // UnauthorizedAccessException belongs here and its absence was a real bug: a
                // sharing violation on Windows does not always surface as IOException, and it is
                // contention, not evidence that the filesystem cannot lock.
                lastContention = ex;

                if (!wait)
                {
                    throw new LockContentedException(
                        $"the graph lock '{path}' is held by another process", ex);
                }

                Thread.Sleep(LockWaitMs);
            }
            catch (Exception ex)
            {
                // A read-only checkout or a filesystem without locking. Genuinely nothing to wait
                // for, refusing to index would be the worse answer, and there is no second writer
                // whose work an unsynchronised write could clobber.
                Dbg.Log($"graph lock unavailable, writing unsynchronised: {ex.Message}");
                return null;
            }
        }

        throw new LockContentedException(
            $"could not take the graph lock '{path}' after {LockAttempts * LockWaitMs}ms (held by another process)",
            lastContention!);
    }

    /// <summary>
    /// The lock the index paths take until this series moves them onto <see cref="AcquireLock"/>.
    ///
    /// It waits and, when the lock is still held, writes unsynchronised rather than failing. That is
    /// exactly the behaviour being removed: it is what put two writers on one graph file after a
    /// five-second wait. It survives only so the query-path commit changes the heal alone.
    /// </summary>
    private static FileStream? AcquireLockOrNull(string root)
    {
        try { return AcquireLock(root, wait: true); }
        catch (LockContentedException ex)
        {
            Dbg.Log($"graph lock still held, writing unsynchronised: {ex.Message}");
            return null;
        }
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

    private const int RenameMaxAttempts = 10;
    private const int RenameInitialWaitMs = 25;
    private const int RenameMaxWaitMs = 2000;

    /// <summary>
    /// Moves the finished graph into place, waiting out a transient hold on the destination.
    ///
    /// Bounded on purpose. If something keeps the file open this backs off exponentially (25ms
    /// doubling, capped at 2s) and, once the retries are gone, raises
    /// <see cref="LockContentedException"/> rather than letting the raw IO error surface: the
    /// runner turns that into exit 75 so an agent knows to wait and retry instead of reporting a
    /// crash. A write that silently did not happen is worse than one that failed loudly -- the
    /// next query would answer from the old graph and say it was current.
    /// </summary>
    private static void Rename(string temp, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(destination))
                {
                    // ReplaceFile rather than MoveFileEx. Windows treats them differently: the
                    // move opens the destination for delete and fails outright if any handle
                    // disallows it, while the replace is built for swapping a file that readers
                    // may hold, which is the whole situation here. On Unix both land on rename()
                    // and the distinction does not arise.
                    File.Replace(temp, destination, destinationBackupFileName: null,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, destination, overwrite: true);
                }

                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (attempt >= RenameMaxAttempts)
                {
                    Dbg.Log($"could not move '{temp}' onto '{destination}' after " +
                            $"{RenameMaxAttempts} attempts: {ex.Message}");
                    throw new LockContentedException(
                        $"could not replace the graph file '{destination}' after " +
                        $"{RenameMaxAttempts} retries (held by another process): {ex.Message}", ex);
                }

                Thread.Sleep(Math.Min(RenameInitialWaitMs << (attempt - 1), RenameMaxWaitMs));
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
        CsMeshDir.Ensure(g.Root);

        using var guard = AcquireLockOrNull(g.Root);

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
        CsMeshDir.Ensure(g.Root);

        using var guard = AcquireLockOrNull(g.Root);
        WriteAtomic(g, PathFor(g.Root));
    }

    /// <summary>
    /// The explicit heal's write: waits for the lock and lets <see cref="LockContentedException"/>
    /// propagate when it stays held. <c>--heal</c> asked for the write, so a timeout is the exit-75
    /// contract that always applied to it, now reached through acquisition as well as the rename.
    /// </summary>
    public static void SaveHealed(Graph g)
    {
        CsMeshDir.Ensure(g.Root);

        using var guard = AcquireLock(g.Root, wait: true);
        WriteAtomic(g, PathFor(g.Root));
    }

    /// <summary>
    /// The implicit heal's write: takes the lock only if it is free and returns false when another
    /// process holds it.
    ///
    /// A query that heals by default must not stall behind another writer, and it must not write
    /// beside one. Returning false lets the caller answer from the graph it already loaded and say
    /// the heal was skipped -- the same outcome the rename path's contention catch produces. A
    /// rename that loses its race still raises, so the caller's existing catch handles both.
    /// </summary>
    public static bool TrySaveInPlace(Graph g)
    {
        CsMeshDir.Ensure(g.Root);

        FileStream? guard;
        try { guard = AcquireLock(g.Root, wait: false); }
        catch (LockContentedException) { return false; }

        using (guard)
        {
            WriteAtomic(g, PathFor(g.Root));
        }

        return true;
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
    /// Where 'review' checks out a base revision to index it. A sibling of the graph files rather
    /// than a temp directory, so a crash leaves evidence where the next run already looks instead
    /// of orphaning a worktree somewhere git will not find it on its own.
    /// </summary>
    public static string BaseWorktreePath(string root) => Path.Combine(DirFor(root), "base");

    /// <summary>
    /// A short identity of the reference set a base graph was compiled against: the indexer build,
    /// the graph format, the shared framework, the working tree's bin/ DLLs by name, size and write
    /// time, and each in-scope project's obj/project.assets.json by path, size and write time. The
    /// base cache is keyed on it because a base built by an older binary, or against references that
    /// have since changed, would compile the same source against different references -- unbound
    /// package types, dropped edges -- and review would report a false exit 5.
    ///
    /// Cost is one walk and stat of the working tree's bin/ DLLs and assets files, tens of files,
    /// next to the full index the base build already does; the write time is in the identity so a
    /// restored file of the same size still invalidates the cache.
    /// </summary>
    public static string ReferenceKeyFor(string root)
    {
        var parts = new List<string> { AppVersion.Get(), Graph.CurrentFormatVersion.ToString() };
        try { parts.Add(RuntimeLocator.FindSharedFramework() ?? ""); } catch { parts.Add(""); }

        if (Directory.Exists(root))
        {
            foreach (var bin in Directory.EnumerateDirectories(root, "bin", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            foreach (var dll in Directory.EnumerateFiles(bin, "*.dll", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                long size = 0, ticks = 0;
                try { var info = new FileInfo(dll); size = info.Length; ticks = info.LastWriteTimeUtc.Ticks; }
                catch { /* a stat was not available; the name still identifies it */ }
                parts.Add($"{Path.GetFileName(dll)}|{size}|{ticks}");
            }

            // The assets files are the other reference input. A restore changes the package set the
            // graph compiles against without touching bin/, so the same path, size and write time
            // that stamp freshness belong in the cache identity too.
            foreach (var directory in ProjectScope.Discover(root).LiveDirectories
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                var assets = ProjectAssets.PathFor(directory);
                if (!File.Exists(assets)) continue;

                long size = 0, ticks = 0;
                try { var info = new FileInfo(assets); size = info.Length; ticks = info.LastWriteTimeUtc.Ticks; }
                catch { /* a stat was not available; the path still identifies it */ }
                parts.Add($"{Path.GetRelativePath(root, assets).Replace('\\', '/')}|{size}|{ticks}");
            }
        }

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", parts)));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    /// <summary>The cached graph for one base revision and reference set. The commit alone is not
    /// enough: the same commit indexed against a different reference set is a different graph.</summary>
    public static string BaseGraphPathFor(string root, string sha, string referenceKey) =>
        Path.Combine(DirFor(root), $"base-{sha}-{referenceKey}.json");

    /// <summary>
    /// Loads a cached base-revision graph for this reference set, or null when there is none or it
    /// predates the current keying rules -- same reasoning as <see cref="LoadPrevious"/>.
    /// </summary>
    public static Graph? LoadBaseGraph(string root, string sha, string referenceKey)
    {
        var path = BaseGraphPathFor(root, sha, referenceKey);
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = OpenReadShared(path);
            var graph = JsonSerializer.Deserialize(stream, AppJsonContext.Default.Graph);
            if (graph == null || graph.FormatVersion != Graph.CurrentFormatVersion) return null;

            graph.Root = root;
            graph.Freeze();
            return graph;
        }
        catch (Exception ex)
        {
            Dbg.Log($"base graph load failed: {ex.Message}");
            return null;
        }
    }

    public static void SaveBaseGraph(string root, string sha, string referenceKey, Graph g)
    {
        CsMeshDir.Ensure(root);
        WriteAtomic(g, BaseGraphPathFor(root, sha, referenceKey));
    }

    /// <summary>
    /// Keeps only the most recently written base graphs, deleting the rest by write time. Each one
    /// is a full-solution graph and an unbounded cache of them in .csmesh/ is exactly the kind of
    /// noise this tool exists to avoid causing.
    /// </summary>
    public static void PruneBaseGraphs(string root, int keep = 5)
    {
        var dir = DirFor(root);
        if (!Directory.Exists(dir)) return;

        var stale = Directory.EnumerateFiles(dir, "base-*.json")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(keep);

        foreach (var file in stale)
        {
            try { file.Delete(); }
            catch (Exception ex) { Dbg.Log($"could not prune base graph '{file.Name}': {ex.Message}"); }
        }
    }

    /// <summary>
    /// Deletes the base graphs for the revision just written that carry another reference key.
    ///
    /// The cache key is (revision, reference set), so one commit can have several files: review it
    /// again from a tree whose bin/ or assets moved and the old file can never be reused by the run
    /// that just wrote the new one. The count rule would hold those as dead weight against files for
    /// other revisions, so this is scoped to the same revision only -- a file for a different sha is
    /// left to <see cref="PruneBaseGraphs"/>. Best effort: another csmesh reviewing the same
    /// repository can hold a base graph open on Windows, and that must not turn a review that
    /// already succeeded into a failure.
    /// </summary>
    public static void PruneBaseGraphsForRevision(string root, string sha, string referenceKey)
    {
        var dir = DirFor(root);
        if (!Directory.Exists(dir)) return;

        var current = Path.GetFileName(BaseGraphPathFor(root, sha, referenceKey));
        foreach (var path in Directory.EnumerateFiles(dir, $"base-{sha}-*.json"))
        {
            if (string.Equals(Path.GetFileName(path), current, StringComparison.OrdinalIgnoreCase)) continue;

            try { File.Delete(path); }
            catch (Exception ex) { Dbg.Log($"could not prune stale base graph '{Path.GetFileName(path)}': {ex.Message}"); }
        }
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
            var skipped = g.IndexedAllProjects ? [] : SkippedProjectDirectories(g);
            foreach (var sourceFile in Indexer.EnumerateSourceFiles(g.Root))
            {
                var relative = Path.GetRelativePath(g.Root, sourceFile);
                if (knownPaths.Contains(relative)) continue;

                // A file the index deliberately left out is not "new". Without this the walk's
                // Everything enumeration reports every project the graph does not index as an added
                // file the moment any tracked directory's timestamp moves.
                if (skipped.Count > 0 && IsInsideSkippedProject(g.Root, sourceFile, skipped)) continue;

                dirty.Add(relative);
            }
        }

        return dirty.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The directories of the projects the index left out, from the csproj paths the graph already
    /// records. Keeps the new-file walk from calling a deliberately excluded project's sources
    /// "new" without re-deriving the scope, which would cost a full <c>ProjectScope.Discover</c>.
    /// </summary>
    private static HashSet<string> SkippedProjectDirectories(Graph g)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in g.SkippedProjects)
        {
            var full = Path.GetFullPath(Path.Combine(g.Root, project));
            var directory = Path.GetDirectoryName(full);
            if (directory is not null) directories.Add(directory);
        }

        return directories;
    }

    /// <summary>
    /// True when the file's nearest csproj ancestor is one of the skipped projects. A nested in-scope
    /// project wins over a skipped ancestor, matching the nearest-project ownership rule.
    /// </summary>
    private static bool IsInsideSkippedProject(string root, string file, HashSet<string> skippedDirectories)
    {
        var stop = Path.GetFullPath(root);
        var current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(file))!);

        while (current is not null && current.FullName.StartsWith(stop, StringComparison.OrdinalIgnoreCase))
        {
            bool hasProject;
            try { hasProject = current.EnumerateFiles("*.csproj").Any(); }
            catch { return false; }

            if (hasProject) return skippedDirectories.Contains(current.FullName);
            current = current.Parent;
        }

        return false;
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
