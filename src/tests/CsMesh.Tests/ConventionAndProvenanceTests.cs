using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

public sealed class ConventionAndProvenanceTests : IClassFixture<GraphFixture>
{
    private readonly GraphFixture _f;

    public ConventionAndProvenanceTests(GraphFixture fixture) => _f = fixture;

    private static BudgetWriter Writer(int budget = 4000) => new(budget);

    private static string Text(BudgetWriter w) => string.Join("\n", w.Lines);

    // ------------------------------------------------------------------ assembly scanning

    [Theory]
    [InlineData("Api.EmailSink")]
    [InlineData("Api.SlackSink")]
    public void Scan_binds_every_implementation_of_the_filtered_interface(string implementation)
    {
        var service = _f.Node("Api.INotificationSink");
        var impl = _f.Node(implementation);

        Assert.Contains(_f.Graph.Out(service.Id),
            e => e.Kind == EdgeKind.DiBinding && e.To == impl.Id);
    }

    [Fact]
    public void Scanned_binding_is_marked_inferred_rather_than_stated()
    {
        var edge = Assert.Single(_f.Graph.Out(_f.Node("Api.INotificationSink").Id),
            e => e.Kind == EdgeKind.DiBinding && e.To == _f.Node("Api.EmailSink").Id);

        Assert.Equal("assembly-scan", edge.Source);
        Assert.True(edge.Score < Edge.TrustThreshold,
            "a scan says a family is wired, not which pair; it must never pass as a named registration");
    }

    [Fact]
    public void Scanned_binding_does_not_outrank_a_named_registration()
    {
        // SqlOrderStore is registered by name. Nothing from a scan may be reported as [di-bound]
        // ahead of it, which is what _diBoundPairs controls.
        var w = Writer();
        Queries.Impl(_f.Graph, _f.Node("Shared.IOrderStore"), w, []);

        var first = w.Lines.First(l => l.StartsWith("  "));
        Assert.Contains("SqlOrderStore", first);
    }

    [Fact]
    public void Convention_helpers_are_recorded_so_doctor_can_report_them()
    {
        Assert.Contains(_f.Graph.ScanRegistrations, r => r.StartsWith("Scan @", StringComparison.Ordinal));
        Assert.Contains(_f.Graph.ScanRegistrations, r => r.StartsWith("AddMediatR @", StringComparison.Ordinal));
    }

    [Fact]
    public void An_interface_is_never_bound_as_its_own_implementation()
    {
        var service = _f.Node("Api.INotificationSink");

        Assert.All(_f.Graph.Out(service.Id).Where(e => e.Kind == EdgeKind.DiBinding),
            e => Assert.NotEqual("interface", _f.Graph.ById(e.To)!.Kind));
    }

    // ------------------------------------------------------------------ provenance

    [Fact]
    public void A_binding_records_where_it_was_registered()
    {
        var edge = Assert.Single(_f.Graph.Out(_f.Node("Shared.IOrderStore").Id),
            e => e.Kind == EdgeKind.DiBinding && e.To == _f.Node("Infrastructure.SqlOrderStore").Id);

        Assert.NotNull(edge.Site);
        Assert.Contains("Registration.cs:", edge.Site);
    }

    [Fact]
    public void A_dispatch_records_where_it_was_sent_from()
    {
        var edge = Assert.Single(_f.EdgesOfKind(EdgeKind.Mediatr));

        Assert.NotNull(edge.Site);
        Assert.Contains("Api.cs:", edge.Site);
    }

    [Fact]
    public void Impl_prints_the_registration_site_next_to_the_implementation()
    {
        var w = Writer();
        Queries.Impl(_f.Graph, _f.Node("Shared.IOrderStore"), w, []);

        Assert.Contains("@ ", Text(w));
    }

    // ------------------------------------------------------------------ project stamping

    [Fact]
    public void Nodes_without_a_project_file_above_them_are_left_blank_not_guessed()
    {
        // The fixture writes bare .cs files with no .csproj, so every project must be empty.
        Assert.All(_f.Graph.Nodes, n => Assert.Equal(string.Empty, n.Project));
    }

    // ------------------------------------------------------------------ structural changes

    [Fact]
    public void Changes_reports_a_removed_binding_as_removed()
    {
        var before = _f.Graph;
        var after = GraphFixture.WithoutEdge(before, EdgeKind.DiBinding,
            "Shared.IOrderStore", "Infrastructure.SqlOrderStore");

        var w = Writer();
        var exit = Queries.Changes(after, before, includeCalls: false, w, []);
        var text = Text(w);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("REMOVED", text);
        Assert.Contains("SqlOrderStore", text);
        Assert.Contains("no longer resolve", text);
    }

    [Fact]
    public void Changes_is_quiet_when_the_shape_did_not_move()
    {
        var w = Writer();
        var exit = Queries.Changes(_f.Graph, _f.Graph, includeCalls: false, w, []);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("no structural change", Text(w));
    }

    // ------------------------------------------------------------------ budget guidance

    [Fact]
    public void Trace_names_a_depth_that_fits_instead_of_telling_the_caller_to_guess()
    {
        var w = Writer(budget: 60);
        var exit = Queries.Trace(_f.Graph, _f.Node("Api.OrderController.Post"), 6, w, [],
                                 "csmesh trace OrderController.Post --budget 60");

        Assert.Equal(Exit.OverBudget, exit);
        Assert.Contains("OVER BUDGET", Text(w));
        Assert.Matches(@"(depth \d+ fits|Even depth 1 does not fit)", Text(w));
    }
}
