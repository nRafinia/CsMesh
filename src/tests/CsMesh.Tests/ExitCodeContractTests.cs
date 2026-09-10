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
    /// Only IOException and UnauthorizedAccessException exhausted on the rename path become
    /// LockContentedException and therefore exit 75. Any other exception -- including
    /// NullReferenceException or ArgumentException -- must stay at exit 70.
    ///
    /// If this boundary is blurred, a real crash in the indexer gets classified as "contended" and
    /// the agent waits and retries forever instead of reporting the fault.
    /// </summary>
    [Fact]
    public void OnlyIoExceptionsOnTheRenamePathReach75()
    {
        // LockContentedException (wrapping an IO exception) -> 75.
        Assert.Equal(Exit.Contended,
            RunGuardedQuietly(["index"],
                _ => throw new LockContentedException("contended", new IOException("blocked"))));

        // A generic exception not from the rename path -> 70, not 75.
        Assert.Equal(Exit.Internal,
            RunGuardedQuietly(["index"],
                _ => throw new InvalidOperationException("not a lock")));

        // NullReferenceException (a real crash) -> 70.
        Assert.Equal(Exit.Internal,
            RunGuardedQuietly(["index"],
                _ => throw new NullReferenceException("null crash")));
    }

    /// <summary>
    /// The stderr line emitted on a contended write must tell the agent to retry, and must name
    /// the exit code so a log-scraper can confirm the classification without running a subprocess.
    /// The word "Retry" is what distinguishes this from a crash report ("that is a bug, not your
    /// query") -- an agent that does not see it will report a fault instead of waiting.
    /// </summary>
    [Fact]
    public void TheStderrMessageOnAContentionTellsTheAgentToRetry()
    {
        var errCapture = new StringWriter();
        var origErr = Console.Error;
        try
        {
            Console.SetError(errCapture);
            CliRunner.RunGuarded(["index"],
                _ => throw new LockContentedException("held", new IOException("blocked")));
        }
        finally { Console.SetError(origErr); }

        var stderr = errCapture.ToString();
        Assert.Contains("Retry", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("75", stderr, StringComparison.Ordinal);
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
