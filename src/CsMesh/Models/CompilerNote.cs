namespace CsMesh.Models;

/// <summary>
/// One kind of compiler error seen while building the graph, with how many times it occurred.
/// </summary>
public sealed class CompilerNote
{
    /// <summary>The diagnostic id, e.g. CS0246 or CS0433.</summary>
    public string Id { get; set; } = string.Empty;

    public int Count { get; set; }

    /// <summary>One representative message, truncated.</summary>
    public string Message { get; set; } = string.Empty;
}