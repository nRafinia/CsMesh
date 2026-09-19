using CsMesh.Skill;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The installed rules and the documented ones drifted apart once, and nothing noticed: an agent
/// reads whatever 'skill --install' wrote, not the file in the repository. These keep the two
/// honest and check that the rules still name the reflex they exist to interrupt.
/// </summary>
public sealed class SkillTextTests
{
    private static string SkillFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SKILL.md"))) dir = dir.Parent;

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "SKILL.md"));
    }

    [Fact]
    public void The_installed_skill_is_the_documented_skill()
    {
        Assert.Equal(SkillFile().TrimEnd('\r', '\n'), SkillText.Markdown.TrimEnd('\r', '\n'));
    }

    [Fact]
    public void Cursor_rules_carry_the_same_body_as_the_other_assistants()
    {
        // Cursor takes front matter and everything else takes the bare body. One source, so a rule
        // added for one assistant cannot go missing for the rest.
        Assert.EndsWith(SkillText.Rules.TrimEnd(), SkillText.CursorMdc.TrimEnd(), StringComparison.Ordinal);
        Assert.StartsWith("---", SkillText.CursorMdc.TrimStart(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("subagent")]
    [InlineData("csmesh context")]
    [InlineData("csmesh map")]
    [InlineData("csmesh silence")]
    [InlineData("csmesh diff")]
    [InlineData("csmesh changes")]
    [InlineData("csmesh review")]
    public void The_compact_rules_name_every_command_an_agent_would_otherwise_skip(string phrase)
    {
        // The compact form is what lands in AGENTS.md, and it is what an agent actually reads.
        // A command missing from here does not exist as far as the session is concerned.
        Assert.Contains(phrase, SkillText.Rules, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_rules_interrupt_the_reflex_rather_than_describing_a_condition()
    {
        // An agent deciding what to do next is not asking itself "is this my second file?". It is
        // reaching for a subagent or a search. The rules have to match on that reach.
        Assert.Contains("you are about to", SkillText.Rules, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not fall back to grep", SkillText.Rules, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Confidence_below_the_threshold_is_described_as_a_lead_not_a_fact()
    {
        Assert.Contains("0.80", SkillText.Rules, StringComparison.Ordinal);
        Assert.Contains("lead, not a fact", SkillText.Rules, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The reverse-edge question is the one an agent answers with trace or grep; the skill has to
    /// name blast-radius --depth 1 as the command, in both the full skill and the compact rules.
    /// </summary>
    [Fact]
    public void The_skill_names_the_reverse_edge_for_who_calls_this()
    {
        Assert.Contains("Who calls this? Where is it invoked from?", SkillText.Markdown, StringComparison.Ordinal);
        Assert.Contains("csmesh blast-radius Type.Member --depth 1", SkillText.Markdown, StringComparison.Ordinal);
        Assert.Contains("csmesh blast-radius Type.Member --depth 1", SkillText.Rules, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exit 2 is a size problem, not an absence: the remedy is the depth the message names, then
    /// --under, then --depth 1, and silence belongs to a narrowed query that then exits 1.
    /// </summary>
    [Fact]
    public void The_exit_2_remedy_is_a_sequence_and_never_silence()
    {
        Assert.Contains("the depth the message names", SkillText.Markdown, StringComparison.Ordinal);
        Assert.Contains("never to exit 2 itself", SkillText.Markdown, StringComparison.Ordinal);
        Assert.Contains("the depth the message names", SkillText.Rules, StringComparison.Ordinal);
        Assert.Contains("never for exit 2 itself", SkillText.Rules, StringComparison.Ordinal);
    }
}