using System.Text.Json;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Skill;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The installed rules block carries no version and no hash, so an AGENTS.md left behind by an older
/// binary keeps steering agents with the budgets that release recommended while the binary uses
/// others, and nothing tells the user to reinstall. These pin doctor's comparison of what is
/// installed against what this build would write: it names a block that differs and stays silent on
/// a file that has none.
/// </summary>
[Collection("console-capture")]
public sealed class DoctorSkillDriftTests : IDisposable
{
    private readonly string _root;
    private readonly string _home;

    public DoctorSkillDriftTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-drift-" + Guid.NewGuid().ToString("N")[..8]);
        _home = Path.Combine(Path.GetTempPath(), "csmesh-home-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(_home);

        File.WriteAllText(Path.Combine(_root, "src", "Thing.cs"), "namespace Demo; public sealed class Thing { }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
        try { Directory.Delete(_home, recursive: true); } catch { /* temp dir */ }
    }

    private (int Exit, string Output) RunDoctor(params string[] args)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            var exit = DoctorCommand.Execute(_root, new Options(args), _home);
            return (exit, buffer.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    /// <summary>A block written by 0.4.1: trace was recommended at 600, not today's 700.</summary>
    private static string OldBudgetBlock() =>
        SkillBlock.Render(SkillText.Rules.Replace("--budget 700", "--budget 600"));

    [Fact]
    public void AReinstalledRepoReportsNoDrift()
    {
        Assert.Equal(Exit.Ok, SkillCommand.Execute(_root, new Options(["--agent", "all"]), SkillMode.Install));

        var (exit, output) = RunDoctor("--no-telemetry");

        Assert.Equal(Exit.Ok, exit);
        Assert.DoesNotContain("differ from this build", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstalledBlockFromAnOlderBudgetIsNamedAsDiffering()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), OldBudgetBlock());

        var (exit, output) = RunDoctor("--no-telemetry");

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains(
            "AGENTS.md: installed csmesh instructions differ from this build",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ABlockRewrittenWithCrlfIsNotReportedAsDrift()
    {
        Assert.Equal(Exit.Ok, SkillCommand.Execute(_root, new Options(["--agent", "codex"]), SkillMode.Install));

        var path = Path.Combine(_root, "AGENTS.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n"));

        var (exit, output) = RunDoctor("--no-telemetry");

        Assert.Equal(Exit.Ok, exit);
        Assert.DoesNotContain("differ from this build", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithoutABlockIsNotStaleAndKeepsTheExitCode()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "# Notes\n\nNothing installed here yet.\n");

        var (exit, output) = RunDoctor("--no-telemetry");

        Assert.Equal(Exit.Ok, exit);
        Assert.DoesNotContain("differ from this build", output, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonModeCarriesTheStaleBlockAndEmitsNothingElse()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), OldBudgetBlock());

        var (exit, raw) = RunDoctor("--json", "--no-telemetry");
        Assert.Equal(Exit.Ok, exit);

        // One frame: a warning printed straight to stdout would corrupt the JSON-RPC stream the MCP
        // server multiplexes, not merely look untidy.
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);

        using var document = JsonDocument.Parse(lines[0]);
        Assert.True(document.RootElement.TryGetProperty("stale_instructions", out var stale),
            "the report should carry stale_instructions");

        Assert.Contains(
            stale.EnumerateArray().Select(x => x.GetString()),
            value => value is not null && value.Contains("AGENTS.md", StringComparison.Ordinal));
    }
}
