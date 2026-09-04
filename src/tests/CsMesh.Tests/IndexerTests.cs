using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

public sealed class IndexerTests(GraphFixture fixture) : IClassFixture<GraphFixture>
{
    // ------------------------------------------------------------------ dispatch identity

    [Fact]
    public void Send_dispatches_to_exactly_one_handler_when_two_commands_share_a_name()
    {
        var mediator = fixture.EdgesOfKind(EdgeKind.Mediatr).ToList();

        Assert.Single(mediator);
        Assert.Equal("CompanyA.Handlers.CreateOrderHandler.Handle", fixture.NameOf(mediator[0].To));
    }

    [Fact]
    public void Handler_in_the_other_namespace_is_never_reached_from_the_controller()
    {
        var wrong = fixture.Node("CompanyB.Handlers.CreateOrderHandler.Handle");
        Assert.DoesNotContain(fixture.Graph.In(wrong.Id), e => e.Kind == EdgeKind.Mediatr);
    }

    [Fact]
    public void Semantic_dispatch_is_recorded_at_full_confidence()
    {
        var edge = Assert.Single(fixture.EdgesOfKind(EdgeKind.Mediatr));

        Assert.Equal("semantic-request", edge.Source);
        Assert.Equal(1.0, edge.Score);
        Assert.Null(edge.Confidence);
    }

    // ------------------------------------------------------------------ registration forms

    [Theory]
    [InlineData("Shared.IOrderStore", "Infrastructure.SqlOrderStore")]
    [InlineData("Shared.IOrderStore", "Infrastructure.CachedOrderStore")]
    [InlineData("Shared.IClock", "Infrastructure.SystemClock")]
    public void Registration_produces_a_binding(string service, string implementation)
    {
        var from = fixture.Node(service);
        var to = fixture.Node(implementation);

        Assert.Contains(fixture.Graph.Out(from.Id),
            e => e.Kind == EdgeKind.DiBinding && e.To == to.Id);
    }

    [Fact]
    public void TryAdd_is_treated_as_a_real_registration()
    {
        var edge = Binding("Shared.IOrderStore", "Infrastructure.SqlOrderStore");

        Assert.Equal("scoped", edge.Note);
        Assert.Equal("semantic-registration", edge.Source);
    }

    [Fact]
    public void Keyed_registration_keeps_its_key()
    {
        var edge = Binding("Shared.IOrderStore", "Infrastructure.CachedOrderStore");

        Assert.Equal("singleton keyed:cache", edge.Note);
        Assert.Contains("keyed:cache", fixture.Node("Infrastructure.CachedOrderStore").Tags);
    }

    [Fact]
    public void Factory_lambda_binds_the_type_it_constructs_at_reduced_confidence()
    {
        var edge = Binding("Shared.IClock", "Infrastructure.SystemClock");

        Assert.Equal("factory-lambda", edge.Source);
        Assert.Equal(0.9, edge.Score, 3);
        Assert.True(edge.Score >= Edge.TrustThreshold, "a constructed type is evidence, not a guess");
    }

    [Fact]
    public void Unregistered_implementation_is_present_but_not_bound()
    {
        var fake = fixture.Node("Infrastructure.FakeOrderStore");

        Assert.DoesNotContain(fake.Tags, t => t.StartsWith("di:"));
        Assert.DoesNotContain(fixture.Graph.In(fake.Id), e => e.Kind == EdgeKind.DiBinding);
    }

    // ------------------------------------------------------------------ graph quality

    [Fact]
    public void Graph_reports_no_ambiguity_when_every_type_resolves()
    {
        Assert.Equal(0, fixture.Graph.AmbiguousDiRegistrations);
        Assert.Equal(0, fixture.Graph.AmbiguousMessageDispatches);
    }

    [Fact]
    public void Call_site_totals_are_counted()
    {
        Assert.True(fixture.Graph.TotalCallSites > 0);
        Assert.True(fixture.Graph.UnresolvedCallSites <= fixture.Graph.TotalCallSites);
    }

    private Edge Binding(string service, string implementation)
    {
        var from = fixture.Node(service);
        var to = fixture.Node(implementation);

        return Assert.Single(fixture.Graph.Out(from.Id), e => e.Kind == EdgeKind.DiBinding && e.To == to.Id);
    }
}
