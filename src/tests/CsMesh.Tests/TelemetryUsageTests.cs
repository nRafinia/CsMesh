using CsMesh.Commands;
using CsMesh.Common;
using TelemetryApi = CsMesh.Telemetry.Telemetry;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A record written by an old release must still show up in `usage`. This captures stdout, so it
/// joins the console-capture collection.
/// </summary>
[Collection("console-capture")]
public sealed class TelemetryUsageTests : IDisposable
{
    private readonly string _root;

    public TelemetryUsageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-telemetry-usage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, ".csmesh"));
        File.WriteAllText(TelemetryApi.LogPath(_root),
            "{\"Ts\":\"2020-01-01T00:00:00Z\",\"Cmd\":\"impl\",\"Caller\":\"human\",\"Tty\":false}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    private static (int Exit, string Output) Capture(Func<int> run)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            return (run(), buffer.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public void A_pascal_case_line_appears_in_usage()
    {
        var (exit, output) = Capture(() =>
            UsageCommand.Execute(_root, new Options(["--days", "100000", "--tail", "5"])));

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("2020-01-01T00:00:00", output);
        Assert.Contains("impl", output);
    }
}
