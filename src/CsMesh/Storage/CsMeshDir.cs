namespace CsMesh.Storage;

/// <summary>
/// The one place the <c>.csmesh</c> directory is created.
///
/// Every command that writes local state goes through <see cref="Ensure(string, Action{string})"/>:
/// the graph snapshots, the usage log and the review baseline all used to create the folder
/// themselves, so a repository could end up with a <c>.csmesh</c> that was never added to git's
/// local exclude. The folder is created here, and immediately kept out of git, so an index run
/// cannot leave an untracked directory in a work repository that someone commits by accident.
/// </summary>
public static class CsMeshDir
{
    public static string For(string root) => Path.Combine(root, ".csmesh");

    /// <summary>
    /// Creates the <c>.csmesh</c> directory for <paramref name="root"/> and, once, adds
    /// <c>.csmesh/</c> to the repository's <c>.git/info/exclude</c>.
    ///
    /// <paramref name="note"/> receives the one line written when the exclude changes; it defaults
    /// to stderr so a command's stdout -- JSON frames and the MCP protocol included -- is untouched.
    /// Nothing is written on later calls. Failures are swallowed: a read-only or locked git
    /// directory is not a reason for the command to fail.
    /// </summary>
    public static string Ensure(string root, Action<string>? note = null)
    {
        var directory = For(root);
        var write = note ?? Console.Error.WriteLine;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            write($"csmesh: could not create {directory} ({ex.Message})");
            return directory;
        }

        try
        {
            if (GitExclude.Ensure(root, out var path, out var error))
            {
                write($"added {GitExclude.Entry} to {path}");
            }
            else if (error is not null)
            {
                write($"csmesh: could not update git exclude ({error})");
            }
        }
        catch (Exception ex)
        {
            write($"csmesh: could not update git exclude ({ex.Message})");
        }

        return directory;
    }
}
