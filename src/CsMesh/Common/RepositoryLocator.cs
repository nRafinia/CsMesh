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
    /// Reads the current Git HEAD commit hash or reference name.
    /// </summary>
    public static string GitHead(string root)
    {
        try
        {
            var head = Path.Combine(root, ".git", "HEAD");
            if (!File.Exists(head)) return string.Empty;

            var line = File.ReadAllText(head).Trim();
            if (line.StartsWith("ref:"))
            {
                var refPath = Path.Combine(root, ".git", line[4..].Trim());
                return File.Exists(refPath)
                    ? File.ReadAllText(refPath).Trim()[..Math.Min(8, 40)]
                    : string.Empty;
            }

            return line[..Math.Min(8, line.Length)];
        }
        catch
        {
            return string.Empty;
        }
    }
}
