using CsMesh.Commands;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A host that exports no conversation id (opencode exports only a process id) must not be reported
/// as "0 sessions". Zero is a fact about the host; printing it bare reads as "no sessions happened".
///
/// The label is a pure function rather than an assertion on captured Console.Out: swapping the
/// process-wide writer races every other test that captures it, which is a flaky failure, not a
/// regression signal.
/// </summary>
public sealed class UsageSessionLabelTests
{
    [Fact]
    public void A_host_with_no_session_id_says_so_instead_of_zero()
    {
        var line = UsageCommand.SessionLine(0);

        Assert.Contains("exposes no session id", line, StringComparison.Ordinal);
        Assert.DoesNotContain("distinct sessions 0", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_session_count_is_printed_as_a_number()
    {
        Assert.Equal("  distinct sessions 3", UsageCommand.SessionLine(3));
    }
}
