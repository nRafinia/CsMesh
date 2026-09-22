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

    /// <summary>
    /// Where this edge was declared, as file:line -- the registration call for a DI binding, the
    /// Send/Publish call for a dispatch. The definition site of the target is already on the node;
    /// this is the other half, and without it the next question ("where is it wired up?") sends
    /// the caller back to grep.
    /// </summary>
    public string? Site { get; set; }

    /// <summary>
    /// For a member-access Call edge, how the member is used: Read, Write, Subscribe, or a union
    /// of those. Null on every other edge -- a method call, a type use, a binding -- so the
    /// on-disk graph does not grow a field only member accesses use.
    ///
    /// Read|Write on a single edge is the reason this is flags: before it, a property read and a
    /// property write deduped to the same edge, and "where is this property written" had no
    /// answer. Test the bits, never equality against one named value, because Read|Write has no
    /// single name. When the union includes Write, <see cref="Site"/> holds the first write site
    /// in syntax order; a read-only edge keeps Site null.
    /// </summary>
    public EdgeRole? Role { get; set; }

    /// <summary>Confidence with the default applied. Never null, never serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double Score => Confidence ?? 1.0;

    /// <summary>Edges below this are guesses the caller should verify against source.</summary>
    public const double TrustThreshold = 0.8;
}
