using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// 'changes' compares two graphs by building a signature per edge. What goes into that signature
/// decides what the command is able to see at all, and three things that belong in it were missing:
/// parameter types, the registration note, and the confidence.
/// </summary>
public sealed class ChangeSignatureTests(GraphFixture fixture) : IClassFixture<GraphFixture>
{
    private static BudgetWriter Writer(int budget = 4000) => new(budget);

    private static string Text(BudgetWriter w) => string.Join("\n", w.Lines);

    /// <summary>A shallow copy that can be mutated without disturbing the shared fixture.</summary>
    private static Graph Fork(Graph source)
    {
        var copy = new Graph
        {
            Root = source.Root,
            FormatVersion = source.FormatVersion,
            BuiltAt = source.BuiltAt,
            Nodes = source.Nodes,
            Edges = source.Edges.Select(e => new Edge
            {
                From = e.From,
                To = e.To,
                Kind = e.Kind,
                Note = e.Note,
                Confidence = e.Confidence,
                Source = e.Source,
                Site = e.Site
            }).ToList()
        };

        copy.Freeze();
        return copy;
    }

    [Fact]
    public void Overloads_do_not_share_one_signature()
    {
        // Both handlers are named Handle and both take a type called CreateOrder, differing only
        // in namespace and return type. Keyed on the display name they collapse into one entry and
        // one of the two dispatch edges vanishes from every comparison -- which on a MediatR
        // codebase is most of them.
        var handlers = fixture.Graph.Nodes
            .Where(n => n.Short.EndsWith(".Handle", StringComparison.Ordinal))
            .ToList();

        Assert.True(handlers.Count > 1, "fixture should declare more than one Handle");
        Assert.True(
            handlers.Select(n => n.Name).Distinct(StringComparer.Ordinal).Count() < handlers.Count
            || handlers.Select(n => n.Short).Distinct(StringComparer.Ordinal).Count() < handlers.Count,
            "fixture should contain a display-name collision for this test to mean anything");

        Assert.Equal(
            handlers.Count,
            handlers.Select(n => n.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_binding_that_changed_lifetime_is_reported()
    {
        var before = Fork(fixture.Graph);
        var after = Fork(fixture.Graph);

        var binding = after.Edges.First(e => e.Kind == EdgeKind.DiBinding && e.Note == "scoped");
        binding.Note = "singleton";

        var w = Writer();
        var exit = Queries.Changes(after, before, includeCalls: false, w, []);
        var text = Text(w);

        Assert.Equal(Exit.Ok, exit);

        // The edge exists on both sides and points at the same pair. Only the note moved, and
        // nothing else in the toolchain reports it: the compiler is silent, the diff shows a
        // one-word edit, and mocked unit tests keep passing.
        Assert.Contains("LIFETIME", text);
        Assert.Contains("scoped", text);
        Assert.Contains("singleton", text);
    }

    [Fact]
    public void An_edge_the_indexer_grew_less_sure_of_is_reported_as_degraded()
    {
        var before = Fork(fixture.Graph);
        var after = Fork(fixture.Graph);

        var edge = after.Edges.First(e => e.Kind == EdgeKind.DiBinding && e.Confidence == null);
        edge.Confidence = 0.65;
        edge.Source = "short-name-match";

        var w = Writer();
        var exit = Queries.Changes(after, before, includeCalls: false, w, []);
        var text = Text(w);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("DEGRADED", text);
        Assert.Contains("1.00 -> 0.65", text);
        Assert.Contains("short-name-match", text);
    }

    [Fact]
    public void Float_drift_is_not_a_change()
    {
        var before = Fork(fixture.Graph);
        var after = Fork(fixture.Graph);

        var edge = after.Edges.First(e => e.Kind == EdgeKind.DiBinding && e.Confidence == null);
        edge.Confidence = 0.99;

        var w = Writer();
        Queries.Changes(after, before, includeCalls: false, w, []);

        Assert.DoesNotContain("DEGRADED", Text(w));
    }

    [Fact]
    public void An_unchanged_graph_still_reports_nothing()
    {
        var w = Writer();
        var exit = Queries.Changes(fixture.Graph, fixture.Graph, includeCalls: false, w, []);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("no structural change", Text(w));
    }
}