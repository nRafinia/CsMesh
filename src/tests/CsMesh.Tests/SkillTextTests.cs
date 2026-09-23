using CsMesh.Analysis;
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
    /// <summary>
    /// The documented budgets are what agents pass; six of the exit=2 rows came from the skill's own
    /// "600 for trace" sitting just under the tail of real trace output.
    /// </summary>
    [Fact]
    public void The_skill_recommends_raised_budgets_for_trace_and_context()
    {
        Assert.Contains("700 for `trace`", SkillText.Markdown, StringComparison.Ordinal);
        Assert.Contains("900 for `context`", SkillText.Markdown, StringComparison.Ordinal);
        Assert.Contains("700 `trace`", SkillText.Rules, StringComparison.Ordinal);
        Assert.Contains("900 `context`", SkillText.Rules, StringComparison.Ordinal);
    }

    [Fact]
    public void The_skill_names_the_reverse_edge_for_who_calls_this()
    {
        Assert.Contains("Who calls this? Where is it invoked from?", SkillText.Markdown, StringComparison.Ordinal);
        Assert.Contains("csmesh blast-radius Type.Member --depth 1", SkillText.Markdown, StringComparison.Ordinal);
        Assert.Contains("csmesh blast-radius Type.Member --depth 1", SkillText.Rules, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exit 3 is no longer only "you typed a bare member name": assembly-qualified keys make a name
    /// that repeats across projects ambiguous, and the candidate list prints each project so
    /// --project can pick one. An agent that reads only the rules must be told both.
    /// </summary>
    [Fact]
    public void The_exit_3_remedy_names_the_project_filter_and_the_candidate_list()
    {
        Assert.Contains("--project", SkillText.Markdown, StringComparison.Ordinal);
        Assert.Contains("--project", SkillText.Rules, StringComparison.Ordinal);
        Assert.Contains("candidate list", SkillText.Markdown, StringComparison.Ordinal);
        Assert.Contains("candidate list", SkillText.Rules, StringComparison.Ordinal);
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

    /// <summary>
    /// The three renderings are the same body for three audiences, so a new flag has to land in all
    /// of them or one assistant never hears about it.
    /// </summary>
    [Fact]
    public void The_skill_teaches_the_writes_flag()
    {
        foreach (var text in new[] { SkillText.Markdown, SkillText.Rules, SkillText.CursorMdc })
        {
            Assert.Contains("--writes", text, StringComparison.Ordinal);
            Assert.Contains("blast-radius Type.Prop --writes --budget 800", text, StringComparison.Ordinal);
            Assert.Contains("via-interface", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The skill is written for other repositories; naming one of csmesh's own types is the self-
    /// reference an agent then searches for in a solution that does not contain it. The list comes
    /// from this repository's own index (never a hand list). Names shorter than six characters are
    /// left out: they collide with ordinary English and CLI vocabulary (`Exit`, `Reach`).
    /// </summary>
    [Fact]
    public void The_skill_leaks_no_csmesh_internal_type_name()
    {
        var internalTypes = InternalTypeShortNames.Value;
        Assert.NotEmpty(internalTypes);

        foreach (var text in new[] { SkillText.Markdown, SkillText.Rules, SkillText.CursorMdc })
        foreach (var name in internalTypes)
            Assert.DoesNotContain(name, text, StringComparison.Ordinal);
    }

    private static readonly Lazy<HashSet<string>> InternalTypeShortNames = new(() =>
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "CsMesh.slnx"))) root = root.Parent;
        Assert.NotNull(root);

        var graph = Indexer.Build(Path.Combine(root!.FullName, "src", "CsMesh"));
        return graph.Nodes
            .Where(n => n.Kind is "type" or "interface" or "enum" or "delegate")
            .Select(n => n.Short)
            .Where(shortName => shortName.Length >= 6)
            .ToHashSet(StringComparer.Ordinal);
    });
}
