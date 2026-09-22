using System.Text.RegularExpressions;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The pointer tool package is the only package <c>dotnet tool install</c> sees. Its
/// DotnetToolSettings.xml lists one RID-specific package per entry in
/// <c>ToolPackageRuntimeIdentifiers</c>, and the release matrix packs one of those on each
/// runner it builds. If the two drift, the pointer can name a package the release never
/// produced (install fails) or omit one it did (install silently falls back to the
/// framework-dependent build).
///
/// This reads both files and requires them to agree exactly, with <c>any</c> present as the
/// framework-dependent fallback — the one entry the matrix deliberately does not build.
/// </summary>
public sealed class PointerListsEveryMatrixRidTests
{
    [Fact]
    public void The_pointer_package_lists_every_rid_the_release_matrix_builds()
    {
        var csproj = File.ReadAllText(RepoFile(Path.Combine("src", "CsMesh", "CsMesh.csproj")));
        var releaseYml = File.ReadAllText(RepoFile(Path.Combine(".github", "workflows", "release.yml")));

        var pointerRids = ToolPackageRuntimeIdentifiers(csproj);
        var matrixRids = MatrixTargets(releaseYml);

        Assert.NotEmpty(matrixRids);
        Assert.Contains("any", pointerRids);

        // Every matrix RID must be in the pointer list, and the pointer list must add
        // nothing beyond the RIDs the matrix builds and the `any` fallback.
        var aotRids = pointerRids.Where(rid => rid != "any").ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            matrixRids.OrderBy(rid => rid, StringComparer.Ordinal),
            aotRids.OrderBy(rid => rid, StringComparer.Ordinal));
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

    private static List<string> ToolPackageRuntimeIdentifiers(string csproj)
    {
        var match = Regex.Match(csproj, @"<ToolPackageRuntimeIdentifiers>([^<]+)</ToolPackageRuntimeIdentifiers>");
        Assert.True(match.Success, "CsMesh.csproj has no <ToolPackageRuntimeIdentifiers>.");

        return match.Groups[1].Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static List<string> MatrixTargets(string releaseYml)
    {
        var job = Regex.Match(releaseYml, @"\n  build-aot:(?<body>.*?)\n  [a-z-]+:", RegexOptions.Singleline);
        Assert.True(job.Success, "release.yml has no build-aot job.");

        return Regex.Matches(job.Groups["body"].Value, @"^\s*target:\s*(?<rid>[A-Za-z0-9._-]+)\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups["rid"].Value)
            .ToList();
    }
}
