using CsMesh.Commands;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The budget column used to be resolved twice -- the writer from the per-kind default, the log from
/// a flat 600 -- so every command whose default is not 600 logged a cap it was never held to. One
/// value, resolved once, used by both.
/// </summary>
public sealed class TelemetryBudgetTests
{
    [Fact]
    public void The_writer_and_telemetry_share_the_resolved_kind_default()
    {
        CsMesh.Telemetry.Telemetry.Current.Budget = -1;

        var writer = QueryCommand.WriterFor("where", new Options([]));

        Assert.Equal(400, writer.Budget);
        Assert.Equal(400, CsMesh.Telemetry.Telemetry.Current.Budget);
    }

    [Fact]
    public void An_explicit_budget_overrides_the_kind_default_for_both()
    {
        CsMesh.Telemetry.Telemetry.Current.Budget = -1;

        var writer = QueryCommand.WriterFor("where", new Options(["--budget", "123"]));

        Assert.Equal(123, writer.Budget);
        Assert.Equal(123, CsMesh.Telemetry.Telemetry.Current.Budget);
    }

    /// <summary>
    /// The two code defaults raised on the exit=2 distribution: entrypoints and unresolved answers
    /// clustered just past 600. where and review stay -- their p90s are well under the cap.
    /// </summary>
    [Fact]
    public void Entrypoints_and_unresolved_carry_the_raised_defaults()
    {
        Assert.Equal(700, QueryCommand.WriterFor("entrypoints", new Options([])).Budget);
        Assert.Equal(700, QueryCommand.WriterFor("unresolved", new Options([])).Budget);
        Assert.Equal(400, QueryCommand.WriterFor("where", new Options([])).Budget);
        // map's old 700 was clipping it: every exit=0 sample sat 2-27 tokens under the cap and one
        // explicit-budget row reached 760, so the ceiling above 700 was never observable.
        Assert.Equal(850, QueryCommand.WriterFor("map", new Options([])).Budget);
    }
}
