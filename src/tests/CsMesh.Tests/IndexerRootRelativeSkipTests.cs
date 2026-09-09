using CsMesh.Analysis;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// 'review' indexes a base revision from a worktree it deliberately creates at .csmesh/base, so
/// that revision's own root sits inside a directory the indexer otherwise treats as its own cache
/// and always excludes. Skip matching used to run against the absolute path, so every file in that
/// worktree carried '.csmesh/' in its ancestry regardless of what was actually being scanned, and
/// the base graph indexed to zero files on every single run. The fix judges each file's path
/// relative to the root being indexed rather than its absolute path.
/// </summary>
public sealed class IndexerRootRelativeSkipTests : IDisposable
{
    private readonly string _root;

    public IndexerRootRelativeSkipTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-skiproot-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void A_root_that_itself_lives_under_a_dot_csmesh_directory_is_still_indexed()
    {
        // Mirrors exactly where 'review' places a base-revision worktree: <repo>/.csmesh/base/...
        var worktree = Path.Combine(_root, ".csmesh", "base");
        Directory.CreateDirectory(Path.Combine(worktree, "src"));
        File.WriteAllText(Path.Combine(worktree, "src", "Thing.cs"),
            "namespace Demo; public sealed class Thing { }");

        var files = Indexer.EnumerateSourceFiles(worktree).ToList();

        Assert.Single(files);
    }

    [Fact]
    public void A_dot_csmesh_directory_found_while_scanning_a_normal_root_is_still_skipped()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Included.cs"),
            "namespace Demo; public sealed class Included { }");

        Directory.CreateDirectory(Path.Combine(_root, ".csmesh", "graveyard"));
        File.WriteAllText(Path.Combine(_root, ".csmesh", "graveyard", "Ignored.cs"),
            "namespace Demo; public sealed class Ignored { }");

        var files = Indexer.EnumerateSourceFiles(_root).Select(Path.GetFileName).ToList();

        Assert.Contains("Included.cs", files);
        Assert.DoesNotContain("Ignored.cs", files);
    }
}
