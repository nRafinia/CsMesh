using System.Text.Json.Serialization;

namespace CsMesh.Telemetry;

/// <summary>
/// Represents a single CLI invocation record for usage analysis.
/// Serialized to .csmesh/usage.jsonl with snake_case property names.
/// </summary>
public sealed class Invocation
{
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
    /// a lower bound on the untruncated answer -- a query stops at the first refusal, so the rest is
    /// never measured. Equal to <see cref="OutTokens"/> when nothing overflowed.
    ///
    /// Without this, an exit=2 row read as "under budget" whenever the refused line was what pushed
    /// it over, and the amount over was unrecoverable from the log.
    /// </summary>
    public int WouldBeTokens { get; set; }

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
