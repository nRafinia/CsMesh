using System.Text.Json.Serialization;

namespace CsMesh.Telemetry;

/// <summary>
/// Represents a single CLI invocation record for usage analysis.
/// Serialized to .csmesh/usage.jsonl with snake_case property names.
/// </summary>
public sealed class Invocation
{
    /// <summary>
    /// The record format this build writes. A line with no <c>schema_version</c> was written before
    /// the field existed and reads as <see cref="LegacySchemaVersion"/>.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>The schema assumed for a record written before the field existed.</summary>
    public const int LegacySchemaVersion = 1;

    /// <summary>
    /// The record format. Defaults to <see cref="LegacySchemaVersion"/> so a line parsed without it
    /// is not mistaken for current; <see cref="Telemetry.End"/> stamps
    /// <see cref="CurrentSchemaVersion"/> on every line it writes.
    /// </summary>
    public int SchemaVersion { get; set; } = LegacySchemaVersion;

    public string Ts { get; set; } = string.Empty;
    public string Cmd { get; set; } = string.Empty;
    public string Args { get; set; } = string.Empty;
    public int Budget { get; set; }
    public int Exit { get; set; }
    public long Ms { get; set; }
    public int OutTokens { get; set; }

    /// <summary>
    /// The size the answer wanted, when the emitted output was cut by the budget. <see cref="OutTokens"/>
    /// is what was emitted; this is the emitted total plus the first line the budget refused, which is
    /// a lower bound on the untruncated answer: a query stops at the first refusal, so everything after
    /// it is never measured, and this stays a lower bound until truncate-and-mark measures past the
    /// first refusal. Equal to <see cref="OutTokens"/> when nothing overflowed.
    ///
    /// Without this, an exit=2 row read as "under budget" whenever the refused line was what pushed
    /// it over, and the amount over was unrecoverable from the log.
    ///
    /// The other half of the same warning: it is recorded only when an overflow happens, so it
    /// cannot reveal the ceiling of answers that currently fit the default -- establishing that
    /// needs a deliberate high-budget run. And as a lower bound it can understate badly: map's own
    /// marker reported ~828 while the unclipped answer was 1463.
    /// </summary>
    public int WouldBeTokens { get; set; }

    /// <summary>
    /// Tokens the forced pre-query notes spent before the query ran -- a stale-index line, a
    /// version-gap line. <see cref="Budget"/> is the cap the writer was given; the query's real
    /// allowance was <c>Budget - ReservedTokens</c>. Recorded because two identical queries otherwise
    /// differ only by whether the index happened to be stale, and nothing in the log said so.
    /// </summary>
    public int ReservedTokens { get; set; }

    /// <summary>Distinct source files the answer pointed the caller at.</summary>
    public int FilesReferenced { get; set; }

    /// <summary>Node count of the graph that served this query; 0 when no graph was loaded.</summary>
    public int Nodes { get; set; }

    /// <summary>Edge count of the graph that served this query; 0 when no graph was loaded.</summary>
    public int Edges { get; set; }

    public string Caller { get; set; } = string.Empty;
    public string CallerVia { get; set; } = string.Empty;
    public bool Tty { get; set; }
    public string? Session { get; set; }
    public string? Parents { get; set; }

    [JsonIgnore]
    public string Root { get; set; } = string.Empty;
}
