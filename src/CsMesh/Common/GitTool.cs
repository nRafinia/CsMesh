using System.Diagnostics;

namespace CsMesh.Common;

/// <summary>
/// Runs git as a subprocess and captures its output.
///
/// Shared by every command that shells out to git rather than reading .git by hand -- diff
/// parsing against packed objects, worktree management, revision resolution -- none of that is
/// something to reimplement per command.
/// </summary>
public static class GitTool
{
    public static bool TryRun(string root, string arguments, out string stdout, out string stderr, out int exitCode)
    {
        stdout = "";
        stderr = "";
        exitCode = -1;

        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (process == null)
            {
                stderr = "could not start git.";
                return false;
            }

            stdout = process.StandardOutput.ReadToEnd();
            stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            exitCode = process.ExitCode;

            return exitCode == 0;
        }
        catch (Exception ex)
        {
            stderr = $"could not run git in {root}: {ex.Message}";
            return false;
        }
    }
}
