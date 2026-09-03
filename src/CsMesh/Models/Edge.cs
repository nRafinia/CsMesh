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
}
