using System.Diagnostics;

namespace CsMesh.Tests;

/// <summary>
/// Starts an optional external tool and waits for it. Used only by attributes that decide at
/// discovery time whether a tool exists, so a missing tool is a skipped test rather than a failure.
///
/// On Windows the tool is launched through <c>cmd.exe</c>: npm installs <c>mmdc</c> as a <c>.cmd</c>
/// shim, and a direct <see cref="Process.Start(ProcessStartInfo)"/> resolves only <c>.exe</c>, so the
/// mermaid validation silently skipped on every Windows machine even with mermaid-cli installed.
/// </summary>
internal static class ExternalTool
{
    internal static bool TryRun(string fileName, string arguments, out string output)
    {
        output = "";
        try
        {
            var info = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/c {fileName} {arguments}")
                : new ProcessStartInfo(fileName, arguments);
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;

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
