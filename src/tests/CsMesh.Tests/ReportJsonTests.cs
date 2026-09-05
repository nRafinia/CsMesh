using System.Text.Json;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// doctor and index were the two commands --json silently ignored. Everything else already
/// answered structurally, so a caller wiring csmesh to anything other than a terminal had to
/// special-case exactly these two -- and they are the two that say whether the answers from the
/// other twelve can be trusted at all.
/// </summary>
public sealed class ReportJsonTests
{
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }

        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "csmesh-report-" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>
    /// Captures stdout, because the assertion that matters is not only what the payload contains
    /// but that nothing else reached the stream alongside it.
    /// </summary>
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

    private static T Parse<T>(string raw, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // One line, not "the last line": a stray WriteLine would be a stream corruption for the
        // MCP transport, not a cosmetic problem, so the test has to fail on it rather than skip it.
        Assert.Single(lines);

        return JsonSerializer.Deserialize(lines[0], info)!;
    }

    [Fact]
    public void DoctorEmitsAStructuredReportAndNothingElse()
    {
        using var sandbox = new Sandbox();
        IndexCommand.Execute(sandbox.Root, new Options(["--json"]));

        var raw = Capture(() => DoctorCommand.Execute(sandbox.Root, new Options(["--json"])));
        var report = Parse(raw, AppJsonContext.Default.DoctorReport);

        Assert.Equal("doctor", report.Command);
        Assert.True(report.HasIndex);
        Assert.Null(report.Problem);
        Assert.True(report.Nodes > 0);
        Assert.Equal(AppVersion.Get(), report.RunningVersion);
        Assert.False(report.VersionGap);
        Assert.NotEmpty(report.Text);
    }

    /// <summary>
    /// The no-index case is the one a caller most needs to branch on, and it is the path that
    /// returns early -- so it is the one most likely to skip serialising.
    /// </summary>
    [Fact]
    public void DoctorReportsAMissingIndexStructurally()
    {
        using var sandbox = new Sandbox();

        var raw = Capture(() => DoctorCommand.Execute(sandbox.Root, new Options(["--json"])));
        var report = Parse(raw, AppJsonContext.Default.DoctorReport);

        Assert.False(report.HasIndex);
        Assert.Equal("MISSING", report.Problem);
        Assert.Equal(0, report.Nodes);
    }

    [Fact]
    public void DoctorTextModeStillPrintsLines()
    {
        using var sandbox = new Sandbox();
        IndexCommand.Execute(sandbox.Root, new Options([]));

        var raw = Capture(() => DoctorCommand.Execute(sandbox.Root, new Options([])));

        Assert.Contains("repo", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"command\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void AFullIndexReportsItsMode()
    {
        using var sandbox = new Sandbox();

        var raw = Capture(() => IndexCommand.Execute(sandbox.Root, new Options(["--json"])));
        var report = Parse(raw, AppJsonContext.Default.IndexReport);

        Assert.Equal("full", report.Mode);
        Assert.True(report.Nodes > 0);
        Assert.True(report.Files > 0);
        Assert.Equal(AppVersion.Get(), report.BuiltByVersion);
    }

    /// <summary>
    /// The incremental branch returns from a different method, so it is the return path most
    /// likely to be left without a serialiser -- and it is the run an agent does most often.
    /// </summary>
    [Fact]
    public void AnUnchangedTreeReportsCurrentRatherThanReindexing()
    {
        using var sandbox = new Sandbox();
        IndexCommand.Execute(sandbox.Root, new Options([]));

        var raw = Capture(() => IndexCommand.Execute(sandbox.Root, new Options(["--json"])));
        var report = Parse(raw, AppJsonContext.Default.IndexReport);

        Assert.Equal("current", report.Mode);
        Assert.True(report.Nodes > 0);
    }

    [Fact]
    public void AnEditedFileReportsAnIncrementalRunAndWhatItRebound()
    {
        using var sandbox = new Sandbox();
        IndexCommand.Execute(sandbox.Root, new Options([]));

        var path = Path.Combine(sandbox.Root, "src", "Thing.cs");
        File.AppendAllText(path, "\npublic sealed class Extra { }\n");

        var raw = Capture(() => IndexCommand.Execute(sandbox.Root, new Options(["--json"])));
        var report = Parse(raw, AppJsonContext.Default.IndexReport);

        Assert.Equal("incremental", report.Mode);
        Assert.Equal(1, report.ReboundFiles);
        Assert.NotNull(report.NodeDelta);
    }
}
