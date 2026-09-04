namespace CsMesh.Models;

/// <summary>
/// A type the code depends on whose declaration lives outside this repository.
/// </summary>
public sealed class ExternalType
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The assembly it came from, which is usually the package name.</summary>
    public string Assembly { get; set; } = string.Empty;

    /// <summary>A sample of places it is used, as file:line. Capped; this is a pointer, not a log.</summary>
    public List<string> Sites { get; set; } = [];
}
