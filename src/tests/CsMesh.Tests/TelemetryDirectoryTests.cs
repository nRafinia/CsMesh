using CsMesh.Common;
using CsMesh.Telemetry;
using TelemetryApi = CsMesh.Telemetry.Telemetry;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Telemetry appends to a <c>.csmesh</c> that already exists and must never create one.
///
/// <c>End</c> used to call <c>CsMeshDir.Ensure</c>, so a command that failed the usage check in a
/// folder that is not a repository -- an <c>index</c> run in a plain folder, exit 64 in 3 ms --
/// left <c>&lt;root&gt;\.csmesh</c> behind. The root rule then read that directory as a marker and
/// answered the parent for every unmarked folder beneath it. This pins the process-wide singleton,
/// so it joins the telemetry-state collection.
/// </summary>
[Collection("telemetry-state")]
public sealed class TelemetryDirectoryTests : IDisposable
{
    private readonly string _root;
    private readonly string _previousRoot;
    private readonly bool _previousDisabled;

    public TelemetryDirectoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-telemetry-dir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _previousRoot = TelemetryApi.Current.Root;
        _previousDisabled = TelemetryApi.Disabled;
    }

    public void Dispose()
    {
        TelemetryApi.Current.Root = _previousRoot;
        TelemetryApi.Disabled = _previousDisabled;
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void A_command_that_exits_64_in_a_folder_without_a_csmesh_leaves_none_behind()
    {
        var exit = RunUsageError();

        TelemetryApi.Current.Root = _root;
        TelemetryApi.Disabled = false;
        TelemetryApi.End(exit);

        Assert.Equal(Exit.Usage, exit);
        Assert.False(Directory.Exists(Path.Combine(_root, ".csmesh")));
    }

    [Fact]
    public void An_existing_csmesh_receives_the_record()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".csmesh"));

        var exit = RunUsageError();

        TelemetryApi.Current.Root = _root;
        TelemetryApi.Disabled = false;
        TelemetryApi.End(exit);

        Assert.Equal(Exit.Usage, exit);
        Assert.Single(TelemetryApi.Read(_root));
    }

    /// <summary>
    /// A bad command line still runs with telemetry enabled -- the state the stray <c>.csmesh</c>
    /// came from. Program then forwards this exit code to <see cref="TelemetryApi.End"/>, which is
    /// the call the test makes. Output is captured because the test host is not a terminal.
    /// </summary>
    private static int RunUsageError()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            return CliRunner.Run(["definitely-not-a-command"]);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
