using CsMesh.Telemetry;
using TelemetryApi = CsMesh.Telemetry.Telemetry;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The record format marker on usage.jsonl. Telemetry.Current is process-wide, so this joins the
/// telemetry-state collection; it writes to a temp root and restores the shared state.
/// </summary>
[Collection("telemetry-state")]
public sealed class TelemetrySchemaTests : IDisposable
{
    private readonly string _root;

    public TelemetrySchemaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-telemetry-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void A_written_line_carries_schema_version_two()
    {
        var previousRoot = TelemetryApi.Current.Root;
        var previousDisabled = TelemetryApi.Disabled;
        try
        {
            TelemetryApi.Disabled = false;
            TelemetryApi.Current.Root = _root;
            TelemetryApi.Current.SchemaVersion = Invocation.LegacySchemaVersion;

            TelemetryApi.End(0);

            var line = File.ReadAllText(TelemetryApi.LogPath(_root));
            Assert.Contains("\"schema_version\":2", line);
        }
        finally
        {
            TelemetryApi.Current.Root = previousRoot;
            TelemetryApi.Disabled = previousDisabled;
        }
    }

    [Fact]
    public void An_old_snake_case_line_without_the_field_reads_as_one()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".csmesh"));
        File.WriteAllText(TelemetryApi.LogPath(_root),
            "{\"ts\":\"2020-01-01T00:00:00Z\",\"cmd\":\"trace\",\"exit\":0}");

        var list = TelemetryApi.Read(_root);

        Assert.Single(list);
        Assert.Equal(Invocation.LegacySchemaVersion, list[0].SchemaVersion);
        Assert.Equal("2020-01-01T00:00:00Z", list[0].Ts);
    }
}
