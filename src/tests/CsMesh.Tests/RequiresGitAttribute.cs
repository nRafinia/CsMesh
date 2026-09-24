using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A [Fact] that shells out to the git CLI. When git cannot be started -- not installed, or not on
/// PATH -- the test is reported skipped with that reason rather than failing, because a missing
/// external tool is a property of the machine, not a defect in the code under test.
///
/// xUnit.net v2 has no runtime skip: <c>Xunit.Sdk.SkipException.ForSkip</c> is documented as v3
/// only, and v2.9.3 exposes no <c>Assert.Skip</c>. So the check runs when the attribute is
/// constructed at discovery time and sets <see cref="FactAttribute.Skip"/>. The test body still
/// fails with a message naming git if the tool vanishes between discovery and execution.
/// </summary>
public sealed class RequiresGitAttribute : FactAttribute
{
    public RequiresGitAttribute()
    {
        if (!GitTool.TryRun(".", "--version", out _, out _, out _))
        {
            Skip = "git is required (git ls-files) and could not be run from PATH.";
        }
    }
}
