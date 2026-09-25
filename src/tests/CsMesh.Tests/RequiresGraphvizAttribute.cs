using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A [Fact] that feeds exported DOT to Graphviz. When <c>dot</c> is not on PATH the test is
/// reported skipped with that reason: a missing tool is a property of the machine, not a defect.
/// </summary>
public sealed class RequiresGraphvizAttribute : FactAttribute
{
    public RequiresGraphvizAttribute()
    {
        if (!ExternalTool.TryRun("dot", "--version", out _))
        {
            Skip = "dot (Graphviz) is not on PATH.";
        }
    }
}
