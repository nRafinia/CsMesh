using CsMesh.Analysis;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

public sealed class WiringSiteHopTests(GraphFixture fixture) : IClassFixture<GraphFixture>
{
    private static BudgetWriter Writer(int budget = 4000) => new(budget);

    [Fact]
    public void Path_and_trace_print_site_on_site_bearing_hop()
    {
        var from = fixture.Node("Api.OrderController.Post");
        var to = fixture.Node("CompanyA.Handlers.CreateOrderHandler.Handle");

        // Verify Path
        var wPath = Writer();
        var exitPath = Queries.Path(fixture.Graph, from, to, 6, wPath, []);
        Assert.Equal(Exit.Ok, exitPath);

        var pathHop = Assert.Single(wPath.Lines, l => l.Contains("CreateOrderHandler.Handle"));
        Assert.Matches(@"@ src[/\\]Api\.cs:\d+", pathHop);

        var pathRow = Assert.Single(wPath.Rows, r => r.Symbol == "CreateOrderHandler.Handle");
        Assert.NotNull(pathRow.Site);
        Assert.Matches(@"src[/\\]Api\.cs:\d+", pathRow.Site);

        // Verify Trace
        var wTrace = Writer();
        var exitTrace = Queries.Trace(fixture.Graph, from, 2, wTrace, []);
        Assert.Equal(Exit.Ok, exitTrace);

        var traceHop = Assert.Single(wTrace.Lines, l => l.Contains("CreateOrderHandler.Handle"));
        Assert.Matches(@"@ src[/\\]Api\.cs:\d+", traceHop);

        var traceRow = Assert.Single(wTrace.Rows, r => r.Symbol == "CreateOrderHandler.Handle");
        Assert.NotNull(traceRow.Site);
        Assert.Matches(@"src[/\\]Api\.cs:\d+", traceRow.Site);
    }
}
