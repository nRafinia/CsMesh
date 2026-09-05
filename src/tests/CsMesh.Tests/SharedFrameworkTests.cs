using CsMesh.Analysis;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The reference set used to be the running runtime directory plus whatever sat in bin/. That is
/// Microsoft.NETCore.App and nothing else, so a framework-dependent web project had no ASP.NET
/// Core assemblies anywhere in its compilation -- they live in a sibling shared framework and are
/// never copied to bin/.
///
/// It did not look like a failure. Roslyn still bound most of each file; it just could not choose
/// between overloads whose parameter types it had never seen, so it returned candidates and no
/// symbol. On a plain `dotnet new webapi` that was 3 of 12 call sites resolved, reported as an
/// unremarkable handful of ambiguous overloads.
/// </summary>
public sealed class SharedFrameworkTests : IDisposable
{
    private readonly List<string> _temp = [];

    private string Layout(params (string Framework, string Version)[] entries)
    {
        var root = Path.Combine(Path.GetTempPath(), "csmesh-fx-" + Guid.NewGuid().ToString("N")[..8]);
        _temp.Add(root);

        foreach (var (framework, version) in entries)
        {
            Directory.CreateDirectory(Path.Combine(root, "shared", framework, version));
        }

        return Path.Combine(root, "shared", "Microsoft.NETCore.App", "10.0.11");
    }

    [Fact]
    public void The_aspnet_framework_beside_the_runtime_is_picked_up()
    {
        var runtime = Layout(
            ("Microsoft.NETCore.App", "10.0.11"),
            ("Microsoft.AspNetCore.App", "10.0.11"));

        var found = Indexer.SiblingSharedFrameworks(runtime).ToList();

        Assert.Single(found);
        Assert.EndsWith(Path.Combine("Microsoft.AspNetCore.App", "10.0.11"), found[0]);
    }

    [Fact]
    public void The_running_framework_is_not_added_twice()
    {
        var runtime = Layout(
            ("Microsoft.NETCore.App", "10.0.11"),
            ("Microsoft.AspNetCore.App", "10.0.11"));

        Assert.DoesNotContain(
            Indexer.SiblingSharedFrameworks(runtime),
            d => d.Contains("Microsoft.NETCore.App", StringComparison.Ordinal));
    }

    [Fact]
    public void The_matching_version_wins_over_a_newer_one()
    {
        // Referencing 11.x while running on 10.x would put two versions of the same type in the
        // compilation and turn every call against them into an ambiguity -- the exact failure the
        // sibling lookup exists to remove.
        var runtime = Layout(
            ("Microsoft.NETCore.App", "10.0.11"),
            ("Microsoft.AspNetCore.App", "9.0.4"),
            ("Microsoft.AspNetCore.App", "10.0.11"),
            ("Microsoft.AspNetCore.App", "11.0.0"));

        var found = Assert.Single(Indexer.SiblingSharedFrameworks(runtime));
        Assert.EndsWith("10.0.11", found);
    }

    [Fact]
    public void Without_an_exact_match_the_newest_is_used()
    {
        var runtime = Layout(
            ("Microsoft.NETCore.App", "10.0.11"),
            ("Microsoft.AspNetCore.App", "8.0.3"),
            ("Microsoft.AspNetCore.App", "9.0.4"));

        var found = Assert.Single(Indexer.SiblingSharedFrameworks(runtime));
        Assert.EndsWith("9.0.4", found);
    }

    [Fact]
    public void Every_sibling_framework_is_returned_not_only_aspnet()
    {
        var runtime = Layout(
            ("Microsoft.NETCore.App", "10.0.11"),
            ("Microsoft.AspNetCore.App", "10.0.11"),
            ("Microsoft.WindowsDesktop.App", "10.0.11"));

        Assert.Equal(2, Indexer.SiblingSharedFrameworks(runtime).Count());
    }

    [Fact]
    public void A_runtime_directory_with_no_shared_root_yields_nothing()
    {
        var orphan = Path.Combine(Path.GetTempPath(), "csmesh-fx-" + Guid.NewGuid().ToString("N")[..8]);
        _temp.Add(orphan);
        Directory.CreateDirectory(orphan);

        Assert.Empty(Indexer.SiblingSharedFrameworks(orphan));
    }

    [Fact]
    public void A_trailing_separator_does_not_break_the_walk()
    {
        // RuntimeEnvironment.GetRuntimeDirectory() returns a trailing separator. Taking Parent
        // twice without trimming it lands one level too high and finds nothing.
        var runtime = Layout(
            ("Microsoft.NETCore.App", "10.0.11"),
            ("Microsoft.AspNetCore.App", "10.0.11"));

        Assert.Single(Indexer.SiblingSharedFrameworks(runtime + Path.DirectorySeparatorChar));
    }

    public void Dispose()
    {
        foreach (var dir in _temp)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }
}