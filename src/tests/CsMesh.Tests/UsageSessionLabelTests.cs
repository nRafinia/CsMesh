using System.Text.Json;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Telemetry;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A host that exports no conversation id (opencode exports only a process id) must not be reported
/// as "0 sessions". Zero is a fact about the host; printing it bare reads as "no sessions happened".
/// </summary>
[Collection("console-capture")]
public sealed class UsageSessionLabelTests : IDisposable
{
    private readonly string _root;

    public UsageSessionLabelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-usage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, ".csmesh"));

        var invocation = new Invocation
        {
            Ts = DateTimeOffset.UtcNow.ToString("O"),
            Cmd = "trace",
            Args = "trace Thing.Go",
            Caller = "opencode",
            CallerVia = "env:OPENCODE",
            Tty = false,
            Session = null,
            Exit = Exit.Ok,
            Ms = 5,
            OutTokens = 10
        };

        File.WriteAllText(
            Path.Combine(_root, ".csmesh", "usage.jsonl"),
            JsonSerializer.Serialize(invocation, AppJsonContext.Default.Invocation) + "\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    private static string Capture(Func<int> run)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            run();
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }

    [Fact]
    public void A_host_with_no_session_id_says_so_instead_of_zero()
    {
        var raw = Capture(() => UsageCommand.Execute(_root, new Options([])));

        Assert.Contains("exposes no session id", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("distinct sessions 0", raw, StringComparison.Ordinal);
    }
}
