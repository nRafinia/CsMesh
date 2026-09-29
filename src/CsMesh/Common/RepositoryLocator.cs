using CsMesh.Storage;

namespace CsMesh.Common;

/// <summary>
/// Discovers repository boundaries and version control metadata.
/// </summary>
public static class RepositoryLocator
{
    /// <summary>
    /// Searches upward from the starting directory to find the repository root: a <c>.git</c>
    /// directory, a solution file, or a <c>.csmesh</c>/<c>.csgraph</c> directory that holds a graph.
    ///
    /// A bare <c>.csmesh</c> is deliberately not a marker. Every command writes
    /// <c>.csmesh/usage.jsonl</c> into the root it resolved, so a telemetry-only <c>.csmesh</c> is
    /// created wherever csmesh happens to run -- under a user profile, for one, because a folder
    /// with no marker of its own falls back to just that. While the bare directory counted, such a
    /// <c>.csmesh</c> in a parent captured every unmarked folder beneath it, so a query from any
    /// one of them was answered for the parent instead of the tree it was run in.
    /// </summary>
    public static string FindRoot(string start)
    {
        // Many IDEs (VS Code, Cursor, JetBrains) pass macros like ${workspaceFolder} or $PROJECT_DIR$.
        // If an editor failed to expand them before passing, discard the literal macro name and fall back
        // to well-known IDE environment variables or CWD.
        if (string.IsNullOrWhiteSpace(start) || (start.Contains('$') && !Directory.Exists(start)))
        {
            start = Environment.GetEnvironmentVariable("CSMESH_REPO")
                    ?? Environment.GetEnvironmentVariable("WORKSPACE_FOLDER")
                    ?? Environment.GetEnvironmentVariable("VSCODE_WORKSPACE_FOLDER")
                    ?? Environment.GetEnvironmentVariable("PROJECT_DIR")
                    ?? Environment.GetEnvironmentVariable("WORKSPACE")
                    ?? Directory.GetCurrentDirectory();
        }

        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                HasGraphMarker(dir.FullName) ||
                dir.EnumerateFiles("*.sln").Any() ||
                dir.EnumerateFiles("*.slnx").Any())
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Path.GetFullPath(start);
    }

    /// <summary>
    /// Whether a directory is a csmesh repository marker: it holds a <c>.csmesh</c> or legacy
    /// <c>.csgraph</c> directory that actually contains the graph file. A directory left behind by
    /// telemetry alone is not one; see <see cref="FindRoot"/> for the bug the distinction prevents.
    /// Shared with McpServer's root preference so the two checks cannot drift apart.
    /// </summary>
    public static bool HasGraphMarker(string directory) =>
        HasGraph(Path.Combine(directory, ".csmesh")) ||
        HasGraph(Path.Combine(directory, ".csgraph"));

    private static bool HasGraph(string directory) =>
        File.Exists(Path.Combine(directory, GraphStore.GraphFileName));

    /// <summary>
    /// Reads the current Git HEAD commit hash, following a symbolic ref when present.
    /// </summary>
    public static string GitHead(string root)
    {
        try
        {
            var head = Path.Combine(root, ".git", "HEAD");
            if (!File.Exists(head)) return string.Empty;

            var line = File.ReadAllText(head).Trim();
            if (!line.StartsWith("ref:", StringComparison.Ordinal)) return Abbrev(line);

            var refName = line[4..].Trim();
            var refPath = Path.Combine(root, ".git", refName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(refPath)) return Abbrev(File.ReadAllText(refPath).Trim());

            // Ref has been packed; scan packed-refs for the matching entry.
            var packed = Path.Combine(root, ".git", "packed-refs");
            if (!File.Exists(packed)) return string.Empty;

            foreach (var entry in File.ReadLines(packed))
            {
                if (entry.Length == 0 || entry[0] is '#' or '^') continue;
                var space = entry.IndexOf(' ');
                if (space <= 0) continue;
                if (entry[(space + 1)..].Trim() == refName) return Abbrev(entry[..space]);
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Abbrev(string hash) => hash.Length <= 8 ? hash : hash[..8];
}
