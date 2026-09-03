using System.Text.Json;
using System.Text.Json.Serialization;
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

    public static Graph? Load(string root)
    {
        var path = PathFor(root);
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, AppJsonContext.Default.Graph);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Identifies files that have been modified, removed, or added since the index was created.
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

        var knownPaths = g.Files.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceFile in Indexer.EnumerateSourceFiles(g.Root))
        {
            var relative = Path.GetRelativePath(g.Root, sourceFile);
            if (!knownPaths.Contains(relative))
            {
                dirty.Add(relative);
            }
        }

        return dirty.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
