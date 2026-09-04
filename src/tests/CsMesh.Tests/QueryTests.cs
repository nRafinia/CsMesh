using CsMesh.Analysis;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

public sealed class QueryTests(GraphFixture fixture) : IClassFixture<GraphFixture>
{
    private static BudgetWriter Writer(int budget = 4000) => new(budget);

    private static string Text(BudgetWriter w) => string.Join("\n", w.Lines);

    // ------------------------------------------------------------------ path

    [Fact]
    public void Path_crosses_mediator_dispatch_and_container_binding_in_one_answer()
    {
        var w = Writer();
        var exit = Queries.Path(
            fixture.Graph,
            fixture.Node("Api.OrderController.Post"),
            fixture.Node("Infrastructure.SqlOrderStore.Save"),
            12, w, []);

        var text = Text(w);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("CreateOrderHandler.Handle", text);
        Assert.Contains("[mediatr", text);
        Assert.Contains("SqlOrderStore.Save", text);
    }

    [Fact]
    public void Path_never_routes_through_the_wrong_namespaces_handler()
    {
        var w = Writer();
        var exit = Queries.Path(
            fixture.Graph,
            fixture.Node("Api.OrderController.Post"),
            fixture.Node("CompanyB.Handlers.CreateOrderHandler.Handle"),
            12, w, []);

        Assert.Equal(Exit.NotFound, exit);
    }

    [Fact]
    public void Path_reports_not_found_rather_than_inventing_a_route()
    {
        var w = Writer();
        var exit = Queries.Path(
            fixture.Graph,
            fixture.Node("Loop.Alpha.Go"),
            fixture.Node("Api.OrderController.Post"),
            12, w, []);

        Assert.Equal(Exit.NotFound, exit);
        Assert.Contains("no path", Text(w));
    }

    // ------------------------------------------------------------------ cycles

    [Fact]
    public void Cycles_finds_the_three_type_loop()
    {
        var w = Writer();
        var exit = Queries.Cycles(fixture.Graph, "type", null, w, []);
        var text = Text(w);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("circular dependency", text);
        foreach (var type in new[] { "Alpha", "Beta", "Gamma" })
        {
            Assert.Contains(type, text);
        }
    }

    [Fact]
    public void Cycles_ignores_recursion_inside_a_single_type()
    {
        var w = Writer();
        Queries.Cycles(fixture.Graph, "type", null, w, []);

        // The handler calls the store and nothing calls it back; it must not appear as a cycle.
        Assert.DoesNotContain("CreateOrderHandler", Text(w));
    }

    // ------------------------------------------------------------------ context

    [Fact]
    public void Context_answers_callers_callees_and_entrypoints_in_one_call()
    {
        var w = Writer();
        var exit = Queries.Context(fixture.Graph, fixture.Node("CompanyA.Handlers.CreateOrderHandler.Handle"), 3, w, []);
        var text = Text(w);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("CALLED BY", text);
        Assert.Contains("OrderController.Post", text);
        Assert.Contains("CALLS", text);
        Assert.Contains("IOrderStore.Save", text);
        Assert.Contains("FILES", text);
    }

    [Fact]
    public void Context_exits_over_budget_instead_of_overflowing()
    {
        var w = Writer(budget: 12);
        var exit = Queries.Context(fixture.Graph, fixture.Node("CompanyA.Handlers.CreateOrderHandler.Handle"), 3, w, []);

        Assert.Equal(Exit.OverBudget, exit);
        Assert.Contains("OVER BUDGET", Text(w));
    }

    // ------------------------------------------------------------------ impl ranking

    [Fact]
    public void Impl_ranks_registered_implementations_above_the_unregistered_one()
    {
        var w = Writer();
        var exit = Queries.Impl(fixture.Graph, fixture.Node("Shared.IOrderStore"), w, []);

        Assert.Equal(Exit.Ok, exit);

        var lines = w.Lines.Where(l => l.StartsWith("  ")).ToList();
        var fake = lines.FindIndex(l => l.Contains("FakeOrderStore"));
        var sql = lines.FindIndex(l => l.Contains("SqlOrderStore"));

        Assert.True(sql >= 0 && fake >= 0);
        Assert.True(sql < fake, "the DI-bound store must outrank the test double");
    }
}
