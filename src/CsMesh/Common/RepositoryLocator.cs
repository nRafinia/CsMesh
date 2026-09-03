namespace CsMesh.Common;

/// <summary>
/// Discovers repository boundaries and version control metadata.
/// </summary>
public static class RepositoryLocator
{
    /// <summary>
    /// Searches upward from the starting directory to find the repository root (.git, .csmesh, or solution files).
    /// </summary>
    public static string FindRoot(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".csmesh")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".csgraph")) ||
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
