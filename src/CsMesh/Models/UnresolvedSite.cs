namespace CsMesh.Models;

/// <summary>
/// One place the indexer wanted an edge and did not get one, or got one it is not sure about.
/// </summary>
public sealed class UnresolvedSite
{
    /// <summary>What kind of edge was missed: call, di, or mediatr.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Source file, relative to the repository root.</summary>
    public string File { get; set; } = string.Empty;

    public int Line { get; set; }

    /// <summary>The expression as written, trimmed. Enough to find it in the file.</summary>
    public string Expression { get; set; } = string.Empty;

    /// <summary>
    /// Why it failed, in a fixed vocabulary so callers can group on it:
    /// no-candidate-symbol, ambiguous-overload, ambiguous-type-name, ambiguous-request-name,
    /// no-handler.
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}
