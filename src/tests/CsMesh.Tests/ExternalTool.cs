using System.Diagnostics;

namespace CsMesh.Tests;

/// <summary>
/// Starts an optional external tool and waits for it. Used only by attributes that decide at
/// discovery time whether a tool exists, so a missing tool is a skipped test rather than a failure.
/// </summary>
internal static class ExternalTool
{
    internal static bool TryRun(string fileName, string arguments, out string output)
    {
        output = "";
        try
        {
            var info = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(info);
            if (process == null) return false;

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
            output = stdout + stderr;
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
