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

    public static void Save(Graph g)
    {
        Directory.CreateDirectory(DirFor(g.Root));
        using var stream = File.Create(PathFor(g.Root));
        JsonSerializer.Serialize(stream, g, AppJsonContext.Default.Graph);
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
            using var stream = File.OpenRead(path);
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
            if (info.Length != file.Size || info.LastWriteTimeUtc.Ticks != file.Ticks)
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
                if (Directory.GetLastWriteTimeUtc(full).Ticks != dir.Ticks) return true;
            }
            catch
            {
                return true;
            }
        }

        return false;
    }
}
