using System.Text.RegularExpressions;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Publishing writes outside the run: NuGet, npm and GitHub Releases. The release
/// workflow must not do any of that unless the ref is a version tag, so that a branch
/// push — including the temporary feature-branch dry run — can never publish.
///
/// Every publishing job and step is gated on the same expression. This pins it: a
/// publish command whose job or step lacks the gate fails, and a publish command in
/// any job other than the three known publishers fails too.
/// </summary>
public sealed class ReleasePublishGateTests
{
    private const string Gate = "${{ github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v') }}";

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
    public void Every_publishing_job_and_step_is_gated_on_a_version_tag_ref()
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
