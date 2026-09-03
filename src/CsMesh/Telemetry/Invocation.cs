using System.Text.Json.Serialization;

namespace CsMesh.Telemetry;

/// <summary>
/// Represents a single CLI invocation record for usage analysis.
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
    /// Count of source file locations referenced in the query output.
    /// </summary>
    public int FilesAvoided { get; set; }

    public string Caller { get; set; } = string.Empty;
    public string CallerVia { get; set; } = string.Empty;
    public bool Tty { get; set; }
    public string? Session { get; set; }
    public string? Parents { get; set; }

    [JsonIgnore]
    public string Root { get; set; } = string.Empty;
}
