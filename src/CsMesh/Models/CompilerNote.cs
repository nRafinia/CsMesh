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

    /// <summary>
    /// The project this diagnostic is about, relative to the repository root. Empty for a graph
    /// built from a repository with no projects, where there is only one compilation to name.
    /// </summary>
    public string Project { get; set; } = string.Empty;
}