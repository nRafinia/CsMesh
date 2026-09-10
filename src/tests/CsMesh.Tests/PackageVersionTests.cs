using System.Text.Json;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The npm package's version is corrected by CI at publish time, which left the checked-in value
/// free to drift from the tool's: for four releases the repository said 0.2.6 while the binary said
/// 0.4.x, and nothing caught it. CI still rewrites the field at publish; this test exists so the
/// repository stops lying about its version between releases.
/// </summary>
public sealed class PackageVersionTests
{
    [Fact]
    public void The_npm_package_version_matches_the_tool_version()
    {
        var csproj = File.ReadAllText(RepoFile(Path.Combine("src", "CsMesh", "CsMesh.csproj")));
        var npm = File.ReadAllText(RepoFile(Path.Combine("npm", "package.json")));

        var toolVersion = CsprojVersion(csproj);
        var packageVersion = JsonDocument.Parse(npm).RootElement.GetProperty("version").GetString();

        Assert.False(string.IsNullOrWhiteSpace(toolVersion), "CsMesh.csproj has no <Version>.");
        Assert.Equal(toolVersion, packageVersion);
    }

    /// <summary>
    /// Anchors on the solution file rather than a fixed number of parent directories, because the
    /// test host's output depth moves with the SDK and configuration.
    /// </summary>
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CsMesh.slnx"))) dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    private static string? CsprojVersion(string content)
    {
        var start = content.IndexOf("<Version>", StringComparison.Ordinal);
        if (start < 0) return null;
        start += "<Version>".Length;

        var end = content.IndexOf("</Version>", start, StringComparison.Ordinal);
        return end < 0 ? null : content[start..end].Trim();
    }
}
