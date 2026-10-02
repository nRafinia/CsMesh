using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A <c>.csmesh</c> that holds nothing but telemetry must not stand in for a repository root.
///
/// Every command writes <c>.csmesh/usage.jsonl</c> into the root it resolved, so a telemetry-only
/// <c>.csmesh</c> appears wherever csmesh happened to run. While any <c>.csmesh</c> counted as a
/// marker, one left in a parent directory captured every unmarked folder beneath it: a query from
/// such a folder was answered for the parent instead of the tree it was run in.
/// </summary>
public sealed class RepositoryLocatorTests : IDisposable
{
    private readonly string _parent;

    public RepositoryLocatorTests()
    {
        _parent = Path.Combine(Path.GetTempPath(), "csmesh-findroot-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_parent);
    }

    public void Dispose()
    {
        try { Directory.Delete(_parent, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void A_telemetry_only_csmesh_does_not_capture_the_folder_beneath_it()
    {
        var top = Path.Combine(_parent, "top");
        Directory.CreateDirectory(Path.Combine(_parent, ".csmesh"));
        File.WriteAllText(Path.Combine(_parent, ".csmesh", "usage.jsonl"), "{}");
        Directory.CreateDirectory(Path.Combine(top, "src"));
        File.WriteAllText(Path.Combine(top, "src", "X.slnx"), "<Solution />");

        var root = RepositoryLocator.FindRoot(top);

        Assert.Equal(Path.GetFullPath(top), root);
    }

    [Fact]
    public void A_csmesh_that_holds_a_graph_is_still_a_marker()
    {
        var top = Path.Combine(_parent, "top");
        Directory.CreateDirectory(Path.Combine(top, ".csmesh"));
        File.WriteAllText(Path.Combine(top, ".csmesh", "graph.json"), "{}");
        var below = Path.Combine(top, "src");
        Directory.CreateDirectory(below);

        var root = RepositoryLocator.FindRoot(below);

        Assert.Equal(Path.GetFullPath(top), root);
    }
}
