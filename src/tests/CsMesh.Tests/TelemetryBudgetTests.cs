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

        Assert.Equal(600, writer.Budget);
        Assert.Equal(600, CsMesh.Telemetry.Telemetry.Current.Budget);
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
    /// The code defaults raised on the exit=2 distribution: unresolved answers clustered just past
    /// 600, and every recorded entrypoints overflow was a large API surface the cap could not hold,
    /// so entrypoints moved further than unresolved. where moved too: its job is to surface the one
    /// symbol the task is about, and dropping the candidate that mattered is the failure mode, so it
    /// stops at 600.
    /// </summary>
    [Fact]
    public void Entrypoints_and_unresolved_carry_the_raised_defaults()
    {
        Assert.Equal(800, QueryCommand.WriterFor("entrypoints", new Options([])).Budget);
        Assert.Equal(700, QueryCommand.WriterFor("unresolved", new Options([])).Budget);
        Assert.Equal(600, QueryCommand.WriterFor("where", new Options([])).Budget);
        // map's old 700 was clipping it: every exit=0 sample sat 2-27 tokens under the cap and one
        // explicit-budget row reached 760, so the ceiling above 700 was never observable.
        Assert.Equal(850, QueryCommand.WriterFor("map", new Options([])).Budget);
    }

    /// <summary>
    /// Sized against the worst reasonable answer from the widened replay, not the median the original
    /// log happened to contain. Each is pinned here so the next person does not start from the log
    /// again: impl against a 20-implementation interface, path against a 12-hop chain, silence
    /// against the largest type that produced a diagnostic.
    /// </summary>
    [Theory]
    [InlineData("impl", 600)]
    [InlineData("path", 500)]
    [InlineData("silence", 300)]
    public void Defaults_are_sized_against_the_worst_replayed_answer(string kind, int budget)
    {
        Assert.Equal(budget, QueryCommand.WriterFor(kind, new Options([])).Budget);
    }
}
