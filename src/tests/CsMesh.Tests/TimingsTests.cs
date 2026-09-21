using System.Text.Json;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Mcp;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// CSMESH_TIMINGS is a diagnostic a user turns on to explain a slow run, and its whole contract is
/// that it changes nothing else. Two ways that can break are silent: an ordinary run that starts
/// paying for measurements nobody asked for, and a timing line that lands on stdout and corrupts the
/// JSON or JSON-RPC frame a caller is parsing. Both are asserted here.
/// </summary>
[Collection("console-capture")]
public sealed class TimingsTests
{
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }

        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "csmesh-timings-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(Root, "src"));

            File.WriteAllText(Path.Combine(Root, "src", "Thing.cs"), """
                namespace Demo;
                public sealed class Thing { public void Go() { } }
                public sealed class Caller { public void Run(Thing t) => t.Go(); }
                """);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    /// <summary>Runs a command with both streams redirected, the way the shell runs the process.</summary>
    private static (string Stdout, string Stderr) CaptureStreams(Func<int> run)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            run();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        return (stdout.ToString(), stderr.ToString());
    }

    private static T WithTimings<T>(string? value, Func<T> body)
    {
        var previous = Environment.GetEnvironmentVariable("CSMESH_TIMINGS");
        Environment.SetEnvironmentVariable("CSMESH_TIMINGS", value);
        try { return body(); }
        finally { Environment.SetEnvironmentVariable("CSMESH_TIMINGS", previous); }
    }

    private static (string Stdout, string Stderr) Index(string root, params string[] args) =>
        CaptureStreams(() =>
        {
            Timings.Begin();
            try { return IndexCommand.Execute(root, new Options(args)); }
            finally
            {
                // Mirror Program's finally so the telemetry, git-exclude and total lines a real
                // invocation writes are produced here too.
                using (Timings.Phase("telemetry")) CsMesh.Telemetry.Telemetry.End(0);
                Timings.Flush("git-exclude");
                Timings.End();
            }
        });

    [Fact]
    public void UnsetAddsNothingToEitherStream()
    {
        using var sandbox = new Sandbox();

        var (stdout, stderr) = WithTimings(null, () => Index(sandbox.Root, "--full"));

        Assert.DoesNotContain("[csmesh timings]", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("[csmesh timings]", stderr, StringComparison.Ordinal);
        Assert.Contains("indexed", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void SetWritesEveryPhaseToStderrAndNoneToStdout()
    {
        using var sandbox = new Sandbox();

        var (stdout, stderr) = WithTimings("1", () => Index(sandbox.Root, "--full"));

        Assert.DoesNotContain("[csmesh timings]", stdout, StringComparison.Ordinal);
        Assert.Contains("indexed", stdout, StringComparison.Ordinal);

        // Every phase named in the report has to actually appear, or a slow run cannot be read.
        foreach (var phase in new[]
                 {
                     "start-to-main", "runtime", "reference-set", "enumerate+ownership", "parse",
                     "compile", "diagnostics", "ivt-usings", "pass1", "pass2", "pass3", "save",
                     "telemetry", "total"
                 })
        {
            Assert.Contains($"[csmesh timings] {phase} ", stderr, StringComparison.Ordinal);
        }

        Assert.Matches(@"references=\d+ dlls-opened=\d+ bytes-opened=\d+", stderr);
    }

    [Fact]
    public void TheStartToMainLineIsTheOnlySourceOfPreMainTime()
    {
        using var sandbox = new Sandbox();

        var (_, stderr) = WithTimings("1", () => Index(sandbox.Root, "--full"));

        // It must carry a number, not the n/a the process-stamp path falls back to here.
        Assert.Matches(@"\[csmesh timings\] start-to-main \d+\.\dms", stderr);
    }

    [Fact]
    public void JsonStdoutStaysASingleParseableFrameWithTimingsOn()
    {
        using var sandbox = new Sandbox();

        var (stdout, stderr) = WithTimings("1", () => Index(sandbox.Root, "--full", "--json"));

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);

        using var document = JsonDocument.Parse(lines[0]);
        Assert.Equal("full", document.RootElement.GetProperty("mode").GetString());
        Assert.Contains("[csmesh timings]", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void McpRepliesStayOneFramePerCallWithTimingsOn()
    {
        using var sandbox = new Sandbox();
        IndexCommand.Execute(sandbox.Root, new Options([]));

        WithTimings("1", () =>
        {
            var stdin = Console.In;
            var stdout = Console.Out;
            var buffer = new StringWriter();

            try
            {
                Console.SetIn(new StringReader(
                    """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{}}}""" + "\n"));
                Console.SetOut(buffer);
                McpServer.Run(sandbox.Root);
            }
            finally
            {
                Console.SetIn(stdin);
                Console.SetOut(stdout);
            }

            // Parsing each line as JSON is the assertion: a leaked timing line is not JSON and fails.
            var frames = buffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(frames);
            using var frame = JsonDocument.Parse(frames[0]);
            Assert.True(frame.RootElement.TryGetProperty("result", out _));
            return 0;
        });
    }
}
