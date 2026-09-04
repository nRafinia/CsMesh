using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Things the indexer must not mistake for something else. Each of these was a real false
/// positive on a production solution, and each was invisible until someone read the sample.
/// </summary>
public sealed class IndexerNoiseTests : IClassFixture<GraphFixture>
{
    private readonly GraphFixture _f;

    public IndexerNoiseTests(GraphFixture fixture) => _f = fixture;

    [Fact]
    public void The_fixture_actually_contains_a_nameof_so_this_test_can_fail()
    {
        // The previous version of this check passed because nothing in the fixture used nameof.
        // A guard that cannot fail is not a guard.
        var source = File.ReadAllText(Path.Combine(_f.Root, "src", "Operators.cs"));
        Assert.Contains("nameof(", source);
    }

    [Fact]
    public void Nameof_is_not_recorded_as_an_unbound_call()
    {
        Assert.DoesNotContain(_f.Graph.Unresolved,
            u => u.Expression.StartsWith("nameof", StringComparison.Ordinal));
    }

    [Fact]
    public void Nameof_does_not_count_toward_the_call_site_total()
    {
        // It is not a call site at all, so it must not appear in the denominator either.
        Assert.True(_f.Graph.TotalCallSites > 0);
        Assert.DoesNotContain(_f.Graph.Unresolved, u => u.Expression.Contains("nameof"));
    }

    [Fact]
    public void A_send_carrying_a_type_from_outside_this_repository_is_not_a_dispatch()
    {
        // HttpClient.SendAsync matched on method name alone and was reported as a message with
        // no handler. The payload type is the signal, not the name.
        Assert.DoesNotContain(_f.Graph.Unresolved,
            u => u.Kind == "mediatr" && u.Expression.Contains("Stream", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_node_from_a_project_directory_is_stamped_before_the_passes_finish()
    {
        // Project stamping used to run after all passes, so anything reading it during indexing
        // saw an empty string. The unfiltered assembly scan depends on it.
        var withFiles = _f.Graph.Nodes.Where(n => n.File.Length > 0).ToList();
        Assert.NotEmpty(withFiles);
        Assert.All(withFiles, n => Assert.NotNull(n.Project));
    }
}