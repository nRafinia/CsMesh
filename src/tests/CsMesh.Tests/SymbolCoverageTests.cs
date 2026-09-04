using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

public sealed class SymbolCoverageTests : IClassFixture<GraphFixture>
{
    private readonly GraphFixture _f;

    public SymbolCoverageTests(GraphFixture fixture) => _f = fixture;

    private static BudgetWriter Writer(int budget = 4000) => new(budget);

    private static string Text(BudgetWriter w) => string.Join("\n", w.Lines);

    // ------------------------------------------------------------------ enums

    [Fact]
    public void An_enum_is_a_node()
    {
        Assert.Equal("enum", _f.Node("Domain.OrderStatus").Kind);
    }

    [Theory]
    [InlineData("Domain.OrderStatus.Draft", "0")]
    [InlineData("Domain.OrderStatus.Submitted", "1")]
    [InlineData("Domain.OrderStatus.Cancelled", "2")]
    public void Enum_members_are_nodes_carrying_their_value(string name, string value)
    {
        var member = _f.Node(name);

        Assert.Equal("enum-member", member.Kind);
        Assert.Equal(value, member.Signature);
    }

    [Fact]
    public void An_enum_name_resolves_to_the_enum_and_not_to_a_property_of_the_same_name()
    {
        // Order.Status is typed OrderStatus. A query for the enum must not land on the property.
        var resolved = _f.Graph.Resolve("OrderStatus");

        Assert.Single(resolved);
        Assert.Equal("enum", resolved[0].Kind);
    }

    [Fact]
    public void Reading_an_enum_member_creates_an_edge_from_the_reader()
    {
        // This is what answers "if I add a member, which switches must I revisit".
        var draft = _f.Node("Domain.OrderStatus.Draft");

        Assert.Contains(_f.Graph.In(draft.Id),
            e => _f.NameOf(e.From) == "Domain.Order.Describe");
    }

    [Fact]
    public void Blast_radius_on_an_enum_reaches_the_code_that_switches_on_it()
    {
        var w = Writer();
        var exit = Queries.BlastRadius(_f.Graph, _f.Node("Domain.OrderStatus"), 3, w, []);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("Order.Describe", Text(w));
    }

    // ------------------------------------------------------------------ delegates and fields

    [Fact]
    public void A_delegate_is_a_node_with_its_invoke_signature()
    {
        var del = _f.Node("Domain.OrderChanged");

        Assert.Equal("delegate", del.Kind);
        Assert.Contains("OrderStatus from", del.Signature);
    }

    [Fact]
    public void A_private_field_is_a_node()
    {
        var field = _f.Node("Domain.Order._reference");

        Assert.Equal("field", field.Kind);
        Assert.Equal("string", field.Signature);
    }

    // ------------------------------------------------------------------ signatures

    [Fact]
    public void A_property_keeps_its_nullable_annotation()
    {
        Assert.Equal("Guid", _f.Node("Domain.Order.Id").Signature);
        Assert.Equal("Guid?", _f.Node("Domain.Order.ParentId").Signature);
    }

    [Fact]
    public void A_method_records_parameters_and_return_type()
    {
        Assert.Equal("() : string", _f.Node("Domain.Order.Describe").Signature);
    }

    // ------------------------------------------------------------------ context MEMBERS

    [Fact]
    public void Context_on_a_type_lists_its_shape_before_what_touches_it()
    {
        var w = Writer();
        var exit = Queries.Context(_f.Graph, _f.Node("Domain.Order"), 3, w, []);
        var text = Text(w);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("MEMBERS", text);
        Assert.Contains("ParentId  Guid?", text);
        // ROLE, then shape, then everything relational. Shape is what the caller would otherwise
        // have opened the file for, so it must survive a budget that truncates the tail.
        var members = text.IndexOf("MEMBERS", StringComparison.Ordinal);
        Assert.True(members > text.IndexOf("ROLE", StringComparison.Ordinal));
        Assert.True(members < text.IndexOf("FILES", StringComparison.Ordinal));
    }

    [Fact]
    public void Context_on_an_enum_lists_its_members()
    {
        var w = Writer();
        Queries.Context(_f.Graph, _f.Node("Domain.OrderStatus"), 3, w, []);

        Assert.Contains("Cancelled  2", Text(w));
    }

    // ------------------------------------------------------------------ unbound types

    [Fact]
    public void A_type_the_compiler_could_not_bind_is_recorded_as_unresolved_not_as_a_dependency()
    {
        // The fixture has no package references, so nothing may be claimed as external.
        Assert.Empty(_f.Graph.ExternalTypes);
        Assert.All(_f.Graph.Unresolved.Where(u => u.Kind == "type"),
            u => Assert.Equal("unbound-type", u.Reason));
    }
}
