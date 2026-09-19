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
}
