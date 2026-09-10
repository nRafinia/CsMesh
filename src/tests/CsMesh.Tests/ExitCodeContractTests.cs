using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Agents branch on the exit code without parsing prose, so the boundary between 64 and 70 is the
/// contract, not a detail. The catch-all used to report Exit.Usage for both, which told an agent
/// that hit a crash its syntax was wrong and sent it back to retry the same arguments.
/// </summary>
public sealed class ExitCodeContractTests
{
    [Fact]
    public void AnUnhandledFaultInsideACommandReports70Not64()
    {
        var exit = RunGuardedQuietly(["trace", "Something"],
            _ => throw new InvalidOperationException("boom"));

        Assert.Equal(Exit.Internal, exit);
    }

    [Fact]
    public void ABadCommandLineStillReports64()
    {
        // Both go through the same guard the crash does. Usage errors are returned, never thrown,
        // so they pass through untouched; if one ever starts being thrown this assertion fails
        // alongside the crash test and the 64/70 boundary has to be re-drawn deliberately.
        Assert.Equal(Exit.Usage, RunGuardedQuietly([], CliRunner.Run));
        Assert.Equal(Exit.Usage, RunGuardedQuietly(["definitely-not-a-command"], CliRunner.Run));
    }

    /// <summary>
    /// The guard's stderr line and the usage help are diagnostics for a human at a terminal; in the
    /// test host they are noise. Capture both streams the way the agent integration tests do.
    /// </summary>
    private static int RunGuardedQuietly(string[] args, Func<string[], int> run)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        try
        {
            Console.SetOut(new StringWriter());
            Console.SetError(new StringWriter());
            return CliRunner.RunGuarded(args, run);
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }
}
