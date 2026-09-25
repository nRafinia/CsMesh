using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Publishing writes outside the run: NuGet, npm and GitHub Releases. Releases are
/// started by hand only, so the workflow has no push trigger and nothing publishes
/// unless the dispatch is on main with a non-empty tag_name.
///
/// Every publishing job and step is gated on the same expression. This pins it: a
/// publish command whose job or step lacks the gate fails, and a publish command in
/// any job other than the three known publishers fails too. It also pins that there
/// is no push trigger and that the tag/version check runs before the build jobs.
/// </summary>
public sealed class ReleasePublishGateTests
{
    private const string Gate =
        "${{ github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main' && inputs.tag_name != '' }}";

    /// <summary>Commands and actions that write outside the run.</summary>
    private static readonly string[] PublishMarkers =
    {
        "dotnet nuget push",
        "npm publish",
        "action-gh-release",
        "gh release",
        "git push",
        "docker push",
        "cargo publish",
        "twine upload"
    };

    private static readonly string[] PublishingJobs = { "publish-nuget", "publish-release", "publish-npm" };

    [Fact]
    public void Every_publishing_job_and_step_is_gated_on_a_valid_dispatch()
    {
        var lines = File.ReadAllLines(RepoFile(Path.Combine(".github", "workflows", "release.yml")));
        var jobs = ParseJobs(lines);

        var publishing = jobs.Where(job => job.Body.Any(IsPublishLine)).ToList();

        // Nothing publishes outside these jobs. A new publisher has to be added here,
        // so it gets the same review and the same gate.
        Assert.Equal(
            PublishingJobs.OrderBy(name => name, StringComparer.Ordinal),
            publishing.Select(job => job.Name).OrderBy(name => name, StringComparer.Ordinal));

        foreach (var job in publishing)
        {
            Assert.Equal(Gate, job.If);

            var publishingSteps = job.Steps.Where(step => step.Body.Any(IsPublishLine)).ToList();
            Assert.NotEmpty(publishingSteps);
            foreach (var step in publishingSteps)
                Assert.Equal(Gate, step.If);
        }
    }

    /// <summary>
    /// A RID tool package must ship only the executable. The archive publish runs
    /// first in the same job and leaves a native csmesh.pdb / csmesh.dbg /
    /// csmesh.dSYM in the shared native output directory; the SDK's _CopyAotSymbols
    /// target copies that stale symbol into the pack's publish directory (and then
    /// into the nupkg) unless CopyOutputSymbolsToPublishDirectory is off. DebugType
    /// alone does not stop it.
    /// </summary>
    [Fact]
    public void The_rid_tool_pack_step_excludes_native_and_managed_symbols()
    {
        var lines = File.ReadAllLines(RepoFile(Path.Combine(".github", "workflows", "release.yml")));
        var build = ParseJobs(lines).Single(job => job.Name == "build-aot");
        var pack = build.Steps.Single(step =>
            step.Name.StartsWith("Pack RID-specific .NET tool package", StringComparison.Ordinal));

        var command = string.Join("\n", pack.Body);
        Assert.Contains("-p:DebugType=none", command, StringComparison.Ordinal);
        Assert.Contains("-p:CopyOutputSymbolsToPublishDirectory=false", command, StringComparison.Ordinal);
    }

    [Fact]
    public void The_workflow_has_no_push_trigger()
    {
        var lines = File.ReadAllLines(RepoFile(Path.Combine(".github", "workflows", "release.yml")));

        var onBlock = lines
            .SkipWhile(line => line.TrimEnd() != "on:")
            .Skip(1)
            .TakeWhile(line => !line.StartsWith("permissions:", StringComparison.Ordinal)
                               && !line.StartsWith("jobs:", StringComparison.Ordinal))
            .ToList();

        Assert.DoesNotContain(onBlock, line => Regex.IsMatch(line, @"^  push:\s*$"));
        Assert.Contains(onBlock, line => Regex.IsMatch(line, @"^  workflow_dispatch:\s*$"));
    }

    [Fact]
    public void The_version_check_runs_before_the_build_jobs()
    {
        var lines = File.ReadAllLines(RepoFile(Path.Combine(".github", "workflows", "release.yml")));
        var jobs = ParseJobs(lines);

        var test = jobs.Single(job => job.Name == "test");
        var testSteps = test.Steps.ToList();
        var check = testSteps.FindIndex(step => step.Name == "Verify tag_name and package versions");
        Assert.True(check >= 0, "The test job has no 'Verify tag_name and package versions' step.");

        var build = testSteps.FindIndex(step => step.Name == "Build (Release)");
        Assert.True(build < 0 || check < build, "The version check must run before the test job builds.");

        foreach (var buildJob in new[] { "build-aot", "pack-pointer" })
            Assert.Contains("test", Needs(jobs.Single(job => job.Name == buildJob)));
    }

    /// <summary>
    /// A dispatch input and the event payload are attacker-controlled once a workflow is
    /// dispatched, and GitHub splices them into the script text before the shell runs. A
    /// value like <c>"; rm -rf / #</c> then executes. Binding them through a step-level
    /// <c>env:</c> variable keeps the value data, not code: the shell reads it as <c>$TAG_NAME</c>.
    /// This fails the moment a <c>run:</c> block interpolates either context again.
    /// </summary>
    [Fact]
    public void No_run_block_interpolates_dispatch_inputs_or_the_event_payload()
    {
        var lines = File.ReadAllLines(RepoFile(Path.Combine(".github", "workflows", "release.yml")));
        var offenders = new List<string>();

        foreach (var job in ParseJobs(lines))
        foreach (var step in job.Steps)
        {
            var script = ExtractRunScript(step.Body);
            if (script == null) continue;
            if (script.Contains("${{ inputs.", StringComparison.Ordinal)
                || script.Contains("${{ github.event.", StringComparison.Ordinal))
                offenders.Add($"{job.Name}/{step.Name}");
        }

        Assert.Empty(offenders);
    }

    /// <summary>Returns the text of a step's <c>run:</c> script, inline or block scalar.</summary>
    private static string? ExtractRunScript(IReadOnlyList<string> body)
    {
        for (var i = 0; i < body.Count; i++)
        {
            var line = body[i];
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("run:", StringComparison.Ordinal)) continue;

            var indent = line.Length - trimmed.Length;
            var inline = trimmed["run:".Length..].Trim();
            if (inline.Length > 0 && inline != "|" && inline != ">")
                return inline;

            var script = new StringBuilder();
            for (var j = i + 1; j < body.Count; j++)
            {
                var next = body[j];
                if (next.Trim().Length == 0)
                {
                    script.AppendLine(next);
                    continue;
                }

                if (next.Length - next.TrimStart().Length <= indent) break;
                script.AppendLine(next);
            }

            return script.ToString();
        }

        return null;
    }

    private static IReadOnlyList<string> Needs(Job job)
    {
        var line = job.Body.FirstOrDefault(l => Regex.IsMatch(l, @"^    needs:\s*"));
        if (line == null) return Array.Empty<string>();

        return line["    needs:".Length..]
            .Trim()
            .Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsPublishLine(string line) =>
        PublishMarkers.Any(marker => line.Contains(marker, StringComparison.Ordinal));

    private sealed record Step(string Name, string? If, IReadOnlyList<string> Body);

    private sealed record Job(string Name, string? If, IReadOnlyList<Step> Steps, IReadOnlyList<string> Body);

    private static List<Job> ParseJobs(string[] lines)
    {
        var jobs = new List<Job>();
        var start = Array.FindIndex(lines, line => line.TrimEnd() == "jobs:");
        Assert.True(start >= 0, "release.yml has no jobs: section.");

        var i = start + 1;
        while (i < lines.Length)
        {
            if (!IsJobHeader(lines[i]))
            {
                i++;
                continue;
            }

            var name = lines[i].Trim().TrimEnd(':').Trim();
            i++;
            var body = new List<string>();
            while (i < lines.Length && !IsJobHeader(lines[i]))
            {
                body.Add(lines[i]);
                i++;
            }

            jobs.Add(new Job(name, ExtractIf(body, 4), ParseSteps(body), body));
        }

        return jobs;
    }

    private static bool IsJobHeader(string line) =>
        Regex.IsMatch(line, @"^  [A-Za-z0-9_.-]+:\s*$");

    private static string? ExtractIf(IEnumerable<string> body, int indent)
    {
        var prefix = new string(' ', indent) + "if:";
        var line = body.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        return line?[prefix.Length..].Trim();
    }

    private static List<Step> ParseSteps(IReadOnlyList<string> jobBody)
    {
        var steps = new List<Step>();
        List<string>? current = null;
        foreach (var line in jobBody)
        {
            if (Regex.IsMatch(line, @"^ {6}-(\s|$)"))
            {
                if (current != null) steps.Add(BuildStep(current));
                current = new List<string> { line };
            }
            else if (current != null)
            {
                current.Add(line);
            }
        }

        if (current != null) steps.Add(BuildStep(current));
        return steps;
    }

    private static Step BuildStep(List<string> lines)
    {
        var match = Regex.Match(lines[0], @"^\s*-\s*(?:name|uses):\s*(?<value>.*?)\s*$");
        var name = match.Success ? match.Groups["value"].Value : lines[0].Trim();
        return new Step(name, ExtractIf(lines, 8), lines);
    }

    /// <summary>
    /// Anchors on the solution file rather than a fixed number of parent directories, because
    /// the test host's output depth moves with the SDK and configuration.
    /// </summary>
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CsMesh.slnx"))) dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
