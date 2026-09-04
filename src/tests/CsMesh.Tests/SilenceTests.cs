using CsMesh.Analysis;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

public sealed class SilenceTests : IClassFixture<GraphFixture>
{
    private readonly GraphFixture _f;

    public SilenceTests(GraphFixture fixture) => _f = fixture;

    private static BudgetWriter Writer(int budget = 4000) => new(budget);

    private static string Text(BudgetWriter w) => string.Join("\n", w.Lines);

    // ------------------------------------------------------------------ paths

    [Fact]
    public void Explains_a_missing_path_instead_of_only_reporting_its_absence()
    {
        var w = Writer();
        var exit = Queries.Silence(_f.Graph, _f.Node("Loop.Alpha.Go"), _f.Node("Api.OrderController.Post"), 12, w, []);
        var text = Text(w);

        Assert.Equal(Exit.NotFound, exit);
        Assert.Contains("no path", text);
        Assert.Contains("walked", text);
    }

    [Fact]
    public void Says_so_when_a_path_actually_exists()
    {
        // Being asked to explain a silence that is not there is itself an answer worth giving.
        var w = Writer();
        var exit = Queries.Silence(_f.Graph,
            _f.Node("Api.OrderController.Post"),
            _f.Node("Infrastructure.SqlOrderStore.Save"), 12, w, []);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("a path does exist", Text(w));
    }

    [Fact]
    public void Reports_the_reverse_direction_when_only_that_one_connects()
    {
        // The commonest reason a path query fails is that the two symbols were given the wrong
        // way round, and concluding "unrelated" from that is the wrong conclusion.
        var w = Writer();
        var exit = Queries.Silence(_f.Graph,
            _f.Node("Infrastructure.SqlOrderStore.Save"),
            _f.Node("Api.OrderController.Post"), 12, w, []);

        Assert.Equal(Exit.NotFound, exit);
        Assert.Contains("REVERSE", Text(w));
    }

    [Fact]
    public void Never_claims_a_reverse_route_that_does_not_exist()
    {
        var w = Writer();
        Queries.Silence(_f.Graph, _f.Node("Loop.Alpha.Go"), _f.Node("Api.OrderController.Post"), 12, w, []);

        Assert.DoesNotContain("REVERSE", Text(w));
    }

    // ------------------------------------------------------------------ single symbols

    [Fact]
    public void Explains_a_symbol_nothing_reaches()
    {
        // CompanyB's handler is deliberately never dispatched to: its request type is a different
        // type that happens to share a class name.
        var w = Writer();
        var exit = Queries.Silence(_f.Graph, _f.Node("CompanyB.Handlers.CreateOrderHandler.Handle"), null, 12, w, []);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("NOTHING REACHES THIS SYMBOL", Text(w));
    }

    [Fact]
    public void Redirects_to_context_when_the_symbol_is_connected_both_ways()
    {
        var w = Writer();
        var exit = Queries.Silence(_f.Graph, _f.Node("CompanyA.Handlers.CreateOrderHandler.Handle"), null, 12, w, []);
        var text = Text(w);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("connected in both directions", text);
        Assert.Contains("csmesh context", text);
    }

    [Fact]
    public void Names_an_interface_that_nothing_in_source_implements()
    {
        // IMediator is injected and called but never implemented here, so a walk through it stops.
        var w = Writer();
        Queries.Silence(_f.Graph, _f.Node("Shared.IMediator"), null, 12, w, []);

        Assert.Contains("no type in this repository implements IMediator", Text(w));
    }

    [Fact]
    public void Mentions_assembly_scanning_when_something_appears_unreferenced()
    {
        // A scan can resolve a type nothing in source names. Calling such a type unused would be
        // the wrong conclusion, so the possibility has to be stated.
        var w = Writer();
        Queries.Silence(_f.Graph, _f.Node("CompanyB.Handlers.CreateOrderHandler.Handle"), null, 12, w, []);

        Assert.Contains("assembly-scan registration", Text(w));
    }

    [Fact]
    public void Ends_with_the_distinction_the_whole_command_exists_to_make()
    {
        var w = Writer();
        Queries.Silence(_f.Graph, _f.Node("CompanyB.Handlers.CreateOrderHandler.Handle"), null, 12, w, []);

        Assert.Contains("Absent from the graph is not the same as absent from the codebase.", Text(w));
    }
}
