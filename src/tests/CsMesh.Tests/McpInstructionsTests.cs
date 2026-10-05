using System.Text.Json;
using CsMesh.Common;
using CsMesh.Mcp;
using CsMesh.Skill;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The MCP instructions are resident in a session's context for its whole life, so they must carry
/// the two rules an MCP-only session would otherwise never see -- the "prefer csmesh over grep"
/// directive and the reach-for-the-command table -- and they must carry them by reference from
/// <see cref="SkillText"/> rather than as a copy that drifts. These drive the real initialize frame,
/// so a wiring change that stops sending the sections fails here.
/// </summary>
[Collection("console-capture")]
public sealed class McpInstructionsTests
{
    /// <summary>Sends one initialize frame through the server and returns the instructions it sent.</summary>
    private static string InitializeInstructions()
    {
        var stdin = Console.In;
        var stdout = Console.Out;
        var buffer = new StringWriter();

        try
        {
            Console.SetIn(new StringReader(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""" + "\n"));
            Console.SetOut(buffer);
            McpServer.Run(Path.GetTempPath());
        }
        finally
        {
            Console.SetIn(stdin);
            Console.SetOut(stdout);
        }

        var line = buffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("result").GetProperty("instructions").GetString()!;
    }

    // SkillBlock.Render normalizes CRLF and CR to LF; the ceiling has to mean the same thing on a
    // Windows CRLF checkout and on Linux CI, so the instructions are normalized the same way.
    private static string Lf(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n");

    [Fact]
    public void Instructions_contain_the_referenced_rules_sections_verbatim()
    {
        var text = Lf(InitializeInstructions());

        Assert.Contains(Lf(SkillText.RulesGrepDirective), text, StringComparison.Ordinal);
        Assert.Contains(Lf(SkillText.RulesMatchTable), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_stay_under_the_resident_context_ceiling()
    {
        var tokens = BudgetWriter.Estimate(Lf(InitializeInstructions()));

        Assert.True(
            tokens <= McpServer.InstructionsTokenCeiling,
            $"MCP instructions are {tokens} tokens, over the {McpServer.InstructionsTokenCeiling}-token ceiling.");
    }
}
