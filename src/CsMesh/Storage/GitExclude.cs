using System.Text;

namespace CsMesh.Storage;

/// <summary>
/// Keeps the <c>.csmesh</c> directory out of git by adding one line to <c>.git/info/exclude</c>.
///
/// The index folder is created in whatever repository <c>csmesh</c> runs in, so without this it
/// shows up as untracked in a work repository and can be staged into a commit by accident -- a
/// directory of local graph snapshots and an audit log, none of which belongs in the history.
/// <c>.git/info/exclude</c> is local to the clone and is not itself tracked, so this never edits a
/// <c>.gitignore</c> a reviewer would see. That is also why it is written here rather than by
/// <c>install</c>: <c>index</c> creates the folder without <c>install</c> ever running.
///
/// The git directory is located by walking the filesystem, not by spawning <c>git</c>: startup must
/// stay cheap for an AOT tool, and <c>git</c> refuses a repository it considers to be owned by
/// another user, which is exactly the machine where the folder is most likely to be committed.
/// </summary>
internal static class GitExclude
{
    /// <summary>The pattern written to the exclude file. Unanchored so it matches at any depth.</summary>
    public const string Entry = ".csmesh/";

    /// <summary>
    /// Adds <see cref="Entry"/> to the exclude file of the nearest git directory, if there is one.
    ///
    /// Returns true only when the file was actually written. <paramref name="path"/> is the exclude
    /// file that was (or would have been) written; <paramref name="error"/> is set when the attempt
    /// failed. Never throws: a read-only or locked git directory must not fail the command.
    /// </summary>
    public static bool Ensure(string start, out string? path, out string? error)
    {
        path = null;
        error = null;

        try
        {
            var gitDir = FindGitDirectory(start);
            if (gitDir is null) return false;

            path = Path.Combine(gitDir, "info", "exclude");
            var existing = File.Exists(path) ? File.ReadAllText(path) : "";

            if (HasEntry(existing))
            {
                path = null;
                return false;
            }

            // Keep the file's own line endings; a fresh file takes the platform's.
            var newline = existing.Length == 0 ? Environment.NewLine
                        : existing.Contains("\r\n") ? "\r\n"
                        : "\n";

            var builder = new StringBuilder(existing);
            if (builder.Length > 0 && builder[^1] != '\n') builder.Append(newline);
            builder.Append(Entry).Append(newline);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, builder.ToString());
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool HasEntry(string content) => content
        .Split('\n')
        .Select(line => line.TrimEnd('\r').Trim())
        .Any(line => line is ".csmesh" or ".csmesh/" or "/.csmesh" or "/.csmesh/");

    /// <summary>
    /// The first <c>.git</c> at or above <paramref name="start"/>. A directory is used as-is; a
    /// file carrying <c>gitdir:</c> is followed, and when that directory names a <c>commondir</c>
    /// (a linked worktree or submodule) the shared common directory is returned so the exclude lands
    /// where the main clone reads it.
    /// </summary>
    private static string? FindGitDirectory(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));

        while (directory != null)
        {
            var dotGit = Path.Combine(directory.FullName, ".git");

            if (Directory.Exists(dotGit)) return dotGit;

            if (File.Exists(dotGit))
            {
                var pointer = ReadGitDirPointer(dotGit);
                if (pointer is null) return null;

                var gitDir = Path.GetFullPath(Path.Combine(directory.FullName, pointer));
                if (!Directory.Exists(gitDir)) return null;

                var common = ReadCommonDir(gitDir);
                return common ?? gitDir;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? ReadGitDirPointer(string file)
    {
        foreach (var line in File.ReadLines(file))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["gitdir:".Length..].Trim();
                if (value.Length > 0) return value;
            }
        }

        return null;
    }

    private static string? ReadCommonDir(string gitDir)
    {
        var file = Path.Combine(gitDir, "commondir");
        if (!File.Exists(file)) return null;

        try
        {
            var value = File.ReadAllText(file).Trim();
            if (value.Length == 0) return null;

            var resolved = Path.GetFullPath(Path.Combine(gitDir, value));
            return Directory.Exists(resolved) ? resolved : null;
        }
        catch
        {
            return null;
        }
    }
}
