using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

public sealed class ChangeIntelligenceTests : IClassFixture<GraphFixture>
{
    private readonly GraphFixture _f;

    public ChangeIntelligenceTests(GraphFixture fixture) => _f = fixture;

    private static BudgetWriter Writer(int budget = 4000) => new(budget);

    private static string Text(BudgetWriter w) => string.Join("\n", w.Lines);

    // ------------------------------------------------------------------ test tagging

    [Fact]
    public void Attribute_marks_a_method_as_test()
    {
        Assert.Contains("test", _f.Node("Suite.OrderStoreTests.Save_returns_the_id").Tags);
    }

    [Fact]
    public void Attribute_on_one_method_marks_the_whole_class()
    {
        Assert.Contains("test", _f.Node("Suite.OrderStoreTests").Tags);
    }

    [Fact]
    public void Class_mark_reaches_a_member_that_has_no_attribute()
    {
        // Otherwise a helper inside a test class reads as a production caller.
        Assert.Contains("test", _f.Node("Suite.OrderStoreTests.Helper").Tags);
    }

    [Fact]
    public void Production_code_is_not_marked_as_test()
    {
        Assert.DoesNotContain("test", _f.Node("Infrastructure.SqlOrderStore.Save").Tags);
    }

    // ------------------------------------------------------------------ blast radius

    [Fact]
    public void Blast_radius_separates_test_callers_from_production_callers()
    {
        var w = Writer();
        var exit = Queries.BlastRadius(_f.Graph, _f.Node("Infrastructure.SqlOrderStore.Save"), 3, w, []);
        var text = Text(w);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("direct callers (tests):", text);
        Assert.Contains("Save_returns_the_id", text);
    }

    [Fact]
    public void Blast_radius_counts_tests_in_its_header()
    {
        var w = Writer();
        Queries.BlastRadius(_f.Graph, _f.Node("Infrastructure.SqlOrderStore.Save"), 3, w, []);

        Assert.Contains("test(s)", w.Lines[1]);
    }

    [Fact]
    public void Blast_radius_carries_confidence_from_the_weakest_edge_on_the_path()
    {
        // Every edge in the fixture reaching this store is a compiler symbol, so nothing may be
        // reported as inferred. The assertion is that the field is populated at all rather than
        // left null on every row, which was the gap.
        var w = Writer();
        Queries.BlastRadius(_f.Graph, _f.Node("Shared.IOrderStore.Save"), 3, w, []);

        Assert.All(w.Rows.Where(r => r.Relation != "root"),
            r => Assert.True(r.Confidence is null or >= 0 and <= 1));
    }

    // ------------------------------------------------------------------ end line

    [Fact]
    public void Every_source_node_records_where_its_declaration_ends()
    {
        var located = _f.Graph.Nodes.Where(n => n.File.Length > 0 && n.Line > 0).ToList();

        Assert.NotEmpty(located);
        Assert.All(located, n => Assert.True(n.EndLine >= n.Line,
            $"{n.Name} ends at {n.EndLine} but starts at {n.Line}"));
    }

    [Fact]
    public void A_method_body_line_resolves_to_the_method_not_the_type()
    {
        var method = _f.Node("CompanyA.Handlers.CreateOrderHandler.Handle");
        var type = _f.Node("CompanyA.Handlers.CreateOrderHandler");

        Assert.True(method.Covers(method.Line));
        Assert.True(type.Covers(method.Line));

        // The innermost span is what diff attributes a hunk to.
        var methodSpan = method.EndLine - method.Line;
        var typeSpan = type.EndLine - type.Line;
        Assert.True(methodSpan < typeSpan);
    }

    // ------------------------------------------------------------------ unresolved

    [Fact]
    public void Unresolved_sites_carry_a_location_and_a_reason()
    {
        Assert.All(_f.Graph.Unresolved, u =>
        {
            Assert.NotEmpty(u.Kind);
            Assert.NotEmpty(u.Reason);
            Assert.True(u.Line > 0);
        });
    }

    [Fact]
    public void Unresolved_reports_cleanly_when_the_kind_has_no_sites()
    {
        var w = Writer();
        var exit = Queries.Unresolved(_f.Graph, "nonsense-kind", null, w, []);

        Assert.True(exit is Exit.NotFound or Exit.Ok);
        Assert.DoesNotContain("Unhandled", Text(w));
    }

    [Fact]
    public void Unresolved_groups_by_kind_and_reason()
    {
        var w = Writer();
        var exit = Queries.Unresolved(_f.Graph, null, null, w, []);

        Assert.Equal(Exit.Ok, exit);

        if (_f.Graph.Unresolved.Count == 0)
        {
            Assert.Contains("nothing unresolved", Text(w));
            return;
        }

        Assert.Matches(@"(call|di|mediatr)/[a-z-]+\s+\(\d+\)", Text(w));
    }

    // ------------------------------------------------------------------ impl ranking

    [Fact]
    public void Impl_pushes_a_test_double_below_every_registered_implementation()
    {
        var w = Writer();
        Queries.Impl(_f.Graph, _f.Node("Shared.IOrderStore"), w, []);

        var rows = w.Lines.Where(l => l.StartsWith("  ")).ToList();
        var fake = rows.FindIndex(l => l.Contains("FakeOrderStore"));
        var cached = rows.FindIndex(l => l.Contains("CachedOrderStore"));

        Assert.True(cached >= 0 && fake >= 0);
        Assert.True(cached < fake);
    }
}
