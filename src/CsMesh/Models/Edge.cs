namespace CsMesh.Models;

/// <summary>
/// Represents a directed relationship between two nodes in the code graph.
/// </summary>
public sealed class Edge
{
    public int From { get; set; }
    public int To { get; set; }
    public EdgeKind Kind { get; set; }

    /// <summary>
    /// Optional metadata or annotation describing the relationship.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// How much this edge can be trusted, from 0 to 1. Null means fully certain, which is the
    /// common case; leaving it unset keeps the serialized graph small. Read <see cref="Score"/>
    /// instead of this property.
    /// </summary>
    public double? Confidence { get; set; }

    /// <summary>
    /// What produced the edge: roslyn-symbol, semantic-registration, semantic-request,
    /// factory-lambda, short-name-match. Null means roslyn-symbol.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>Confidence with the default applied. Never null, never serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double Score => Confidence ?? 1.0;

    /// <summary>Edges below this are guesses the caller should verify against source.</summary>
    public const double TrustThreshold = 0.8;
}
