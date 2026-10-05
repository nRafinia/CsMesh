using System.Text.Json;
using CsMesh.Common;
using CsMesh.Mcp;
using CsMesh.Skill;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The MCP instructions and the tool catalogue are both resident in a session's context and both are
/// paid for once per session, so their combined size is what matters. These drive the real frames --
/// a wiring change that stops sending the rules sections, or a schema description that grows back,
/// fails here.
/// </summary>
[Collection("console-capture")]
public sealed class McpInstructionsTests
{
    /// <summary>Runs frames through the server the way a client does and returns the replies.</summary>
    private static List<JsonElement> Exchange(params string[] frames)
    {
        var stdin = Console.In;
        var stdout = Console.Out;
        var buffer = new StringWriter();

        try
        {
            Console.SetIn(new StringReader(string.Join("\n", frames) + "\n"));
            Console.SetOut(buffer);
            McpServer.Run(Path.GetTempPath());
        }
        finally
        {
            Console.SetIn(stdin);
            Console.SetOut(stdout);
        }

        return buffer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
    }

    private static string InitializeInstructions() =>
        Exchange("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")[0]
            .GetProperty("result").GetProperty("instructions").GetString()!;

    // SkillBlock.Render normalizes CRLF and CR to LF; the ceilings have to mean the same thing on a
    // Windows CRLF checkout and on Linux CI, so the text is normalized the same way.
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

    [Fact]
    public void Instructions_and_the_tool_catalogue_stay_under_the_combined_ceiling()
    {
        var replies = Exchange(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");

        var instructions = replies[0].GetProperty("result").GetProperty("instructions").GetString()!;
        var catalogue = replies[1].GetProperty("result").GetRawText();

        var tokens = BudgetWriter.Estimate(Lf(instructions)) + BudgetWriter.Estimate(Lf(catalogue));

        Assert.True(
            tokens <= McpServer.CatalogueTokenCeiling,
            $"instructions + tools/list are {tokens} tokens, over the {McpServer.CatalogueTokenCeiling}-token ceiling.");
    }
}
