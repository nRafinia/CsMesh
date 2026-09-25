using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A [Fact] that feeds exported Mermaid to mermaid-cli. When <c>mmdc</c> is not on PATH the test is
/// reported skipped with that reason.
/// </summary>
public sealed class RequiresMermaidCliAttribute : FactAttribute
{
    public RequiresMermaidCliAttribute()
    {
        if (!ExternalTool.TryRun("mmdc", "--version", out _))
        {
            Skip = "mmdc (mermaid-cli) is not on PATH.";
        }
    }
}
