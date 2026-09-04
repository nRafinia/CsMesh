using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The indexer skipped obj/ as build output and *.g.cs as generated, and the SDK's global usings
/// file is both. Every project with ImplicitUsings therefore compiled with no System namespace:
/// List&lt;&gt;, Task, Guid and CancellationToken were unbound, and about half the call sites in a
/// real solution failed to resolve. Nothing caught it because every fixture wrote its usings out.
/// </summary>
public sealed class ImplicitUsingsTests : IClassFixture<GraphFixture>
{
    private readonly GraphFixture _f;

    public ImplicitUsingsTests(GraphFixture fixture) => _f = fixture;

    [Fact]
    public void The_fixture_has_a_file_with_no_using_directives_so_this_can_fail()
    {
        var source = File.ReadAllText(Path.Combine(_f.Root, "src", "Implicit.cs"));

        Assert.DoesNotContain("using System", source);
        Assert.Contains("List<string>", source);
    }

    [Fact]
    public void Global_usings_reach_the_compilation()
    {
        Assert.True(_f.Graph.GlobalUsingSources > 0);
    }

    [Theory]
    [InlineData("Modern.Basket.CountAsync")]
    [InlineData("Modern.Basket.Sorted")]
    [InlineData("Modern.Basket.Id")]
    public void Members_typed_only_through_implicit_usings_are_indexed(string name)
    {
        Assert.Contains(_f.Graph.Nodes, n => n.Name == name);
    }

    [Fact]
    public void Framework_types_from_implicit_usings_bind()
    {
        // Task, Guid, CancellationToken and List<> all come from the implicit set. If any is
        // unbound it shows up here as a type the compiler could not resolve.
        var names = new[] { "Task", "Guid", "CancellationToken", "List", "IEnumerable" };

        Assert.DoesNotContain(_f.Graph.Unresolved,
            u => u.Kind == "type" && names.Contains(u.Expression, StringComparer.Ordinal));
    }

    [Fact]
    public void A_call_into_an_implicitly_imported_type_is_not_reported_as_unbound()
    {
        Assert.DoesNotContain(_f.Graph.Unresolved,
            u => u.Expression.Contains("Guid.NewGuid", StringComparison.Ordinal)
                 || u.Expression.Contains("Task.FromResult", StringComparison.Ordinal));
    }

    [Fact]
    public void Full_counts_are_kept_separately_from_the_capped_sample()
    {
        // The sample is the first N in traversal order. Reading proportions off it described the
        // first few files rather than the solution, and produced a wrong conclusion once.
        var sampled = _f.Graph.Unresolved.Count;
        var counted = _f.Graph.UnresolvedByReason.Values.Sum();

        Assert.True(counted >= sampled);
    }
}