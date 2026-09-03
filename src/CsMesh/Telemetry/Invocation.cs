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
