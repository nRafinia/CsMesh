using CsMesh.Telemetry;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// opencode exports OPENCODE and OPENCODE_PID, not OPENCODE_SESSION, so checking only the session
/// label reported every opencode shell as unknown-automation. Detection is driven through an
/// injected reader here, so the test does not mutate the process environment.
/// </summary>
public sealed class CallerDetectorTests
{
    [Fact]
    public void Opencode_marks_the_caller_as_opencode()
    {
        var (caller, via) = CallerDetector.Detect(name => name == "OPENCODE" ? "1" : null);

        Assert.Equal("opencode", caller);
        Assert.Equal("env:OPENCODE", via);
    }

    [Fact]
    public void Opencode_pid_also_marks_the_caller()
    {
        var (caller, via) = CallerDetector.Detect(name => name == "OPENCODE_PID" ? "1234" : null);

        Assert.Equal("opencode", caller);
        Assert.Equal("env:OPENCODE_PID", via);
    }

    [Fact]
    public void An_explicit_caller_still_overrides_detection()
    {
        var (caller, _) = CallerDetector.Detect(name => name switch
        {
            "CSMESH_CALLER" => "my-agent",
            "OPENCODE" => "1",
            _ => null
        });

        Assert.Equal("my-agent", caller);
    }
}
