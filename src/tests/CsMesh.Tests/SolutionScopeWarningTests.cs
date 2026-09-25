using CsMesh.Commands;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A solution file that is found but does not fully decide scope used to be invisible: the fall back
/// to the ProjectReference closure was only a doctor scope line, and a single unmatched path was
/// dropped with no trace at all. These tests hold the line and the JSON field that name it, for
/// doctor and for index, and that the fall back is stated once for the scope, not once per solution.
/// </summary>
[Collection("console-capture")]
public sealed class SolutionScopeWarningTests : IDisposable
{
    private readonly string _root;
    private readonly string _home;

    public SolutionScopeWarningTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-slnwarn-" + Guid.NewGuid().ToString("N")[..8]);
        _home = Path.Combine(Path.GetTempPath(), "csmesh-slnhome-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
        try { Directory.Delete(_home, recursive: true); } catch { /* temp dir */ }
    }

    private const string ExeProject =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>";

    private const string LibraryProject = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // An executable root keeps the repository on the real closure path, which is the branch that
    // carries the "scope fell back" suffix; a library alone would take the indexing-everything path.
    private void WriteProjects()
    {
        Write("App/App.csproj", ExeProject);
        Write("App/Code.cs", "namespace App; public class A { }");
        Write("Lib/Lib.csproj", LibraryProject);
        Write("Lib/Code.cs", "namespace Lib; public class L { }");
    }

    private static (string Output, int Exit) Capture(Func<int> run)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(buffer);
            exit = run();
        }
        finally
        {
            Console.SetOut(original);
        }

        return (buffer.ToString(), exit);
    }

    private (string Output, int Exit) Doctor(string[] args) =>
        Capture(() => DoctorCommand.Execute(_root, new Options(args), _home));

    private (string Output, int Exit) Index(string[] args) =>
        Capture(() => IndexCommand.Execute(_root, new Options(args)));

    [Fact]
    public void Doctor_names_a_solution_that_matched_nothing_and_states_the_fall_back()
    {
        WriteProjects();
        Write("App.slnx", "<Solution><Project Path=\"Nowhere/Gone.csproj\" /></Solution>");

        Index([]);
        var (output, exit) = Doctor([]);

        var line = SolutionScopeWarnings.Label
            + "App.slnx: 0 of 1 project path(s) matched on disk "
            + "(first unmatched: Nowhere/Gone.csproj); scope fell back to the ProjectReference closure";
        Assert.Equal(Exit.Ok, exit);
        Assert.Contains(line, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_names_a_partial_match_without_the_fall_back_suffix()
    {
        WriteProjects();
        Write("App.slnx",
            "<Solution><Project Path=\"App/App.csproj\" />" +
            "<Project Path=\"Nowhere/Gone.csproj\" /></Solution>");

        Index([]);
        var (output, exit) = Doctor([]);

        var line = SolutionScopeWarnings.Label
            + "App.slnx: 1 of 2 project path(s) matched on disk (first unmatched: Nowhere/Gone.csproj)";
        Assert.Equal(Exit.Ok, exit);
        Assert.Contains(line, output, StringComparison.Ordinal);
        Assert.DoesNotContain("scope fell back", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_doctor_json_report_carries_the_finding()
    {
        WriteProjects();
        Write("App.slnx", "<Solution><Project Path=\"Nowhere/Gone.csproj\" /></Solution>");

        Index([]);
        var (raw, exit) = Doctor(["--json"]);

        Assert.Equal(Exit.Ok, exit);
        var report = System.Text.Json.JsonSerializer.Deserialize(
            raw.Trim(), AppJsonContext.Default.DoctorReport)!;
        var finding = Assert.Single(report.SolutionFindings);
        Assert.Equal("App.slnx", finding.Solution);
        Assert.Equal(1, finding.Named);
        Assert.Equal(0, finding.Matched);
        Assert.Equal("Nowhere/Gone.csproj", finding.FirstUnmatched);
        Assert.Equal(Exit.Ok, report.Exit);
        Assert.Contains(SolutionScopeWarnings.Label + "App.slnx", string.Join("\n", report.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void Index_reports_the_solution_finding_in_text_and_json()
    {
        WriteProjects();
        Write("App.slnx", "<Solution><Project Path=\"Nowhere/Gone.csproj\" /></Solution>");

        var (text, textExit) = Index([]);

        var line = SolutionScopeWarnings.Label
            + "App.slnx: 0 of 1 project path(s) matched on disk "
            + "(first unmatched: Nowhere/Gone.csproj); scope fell back to the ProjectReference closure";
        Assert.Equal(Exit.Ok, textExit);
        Assert.Contains(line, text, StringComparison.Ordinal);

        var (raw, jsonExit) = Index(["--json", "--full"]);

        Assert.Equal(Exit.Ok, jsonExit);
        var report = System.Text.Json.JsonSerializer.Deserialize(
            raw.Trim(), AppJsonContext.Default.IndexReport)!;
        var finding = Assert.Single(report.SolutionFindings);
        Assert.Equal("App.slnx", finding.Solution);
        Assert.Equal(1, finding.Named);
        Assert.Equal(0, finding.Matched);
        Assert.Equal(Exit.Ok, report.Exit);
    }

    [Fact]
    public void A_no_change_incremental_run_prints_no_solution_line()
    {
        WriteProjects();
        Write("App.slnx", "<Solution><Project Path=\"Nowhere/Gone.csproj\" /></Solution>");

        var (fullText, _) = Index(["--no-telemetry"]);
        Assert.Contains(SolutionScopeWarnings.Label, fullText, StringComparison.Ordinal);

        // Nothing changed on disk, so this run builds nothing and decides no scope: it must stay
        // quiet rather than derive a scope of its own. Doctor remains the command that always shows
        // the finding, because it recomputes at read time.
        var (raw, exit) = Index(["--json", "--no-telemetry"]);

        Assert.Equal(Exit.Ok, exit);
        var report = System.Text.Json.JsonSerializer.Deserialize(
            raw.Trim(), AppJsonContext.Default.IndexReport)!;
        Assert.Equal("current", report.Mode);
        Assert.Empty(report.SolutionFindings);
        Assert.DoesNotContain(SolutionScopeWarnings.Label, string.Join("\n", report.Text), StringComparison.Ordinal);
    }
}
