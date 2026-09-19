using CsMesh.Commands;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// User-facing text must not promise something the command no longer does. `map` was documented as
/// "deliberately one screen"; at the 850-token default a large enough solution exhausts the budget
/// and says so, which makes that promise false exactly where an agent would lean on it.
/// </summary>
public sealed class HelpTextTests
{
    [Fact]
    public void Map_help_describes_a_bounded_summary_rather_than_a_single_screen()
    {
        Assert.DoesNotContain("Deliberately one screen", HelpCommand.MapHelp, StringComparison.Ordinal);
        Assert.Contains("top N", HelpCommand.MapHelp, StringComparison.Ordinal);
        Assert.Contains("INCOMPLETE", HelpCommand.MapHelp, StringComparison.Ordinal);
    }

    /// <summary>
    /// The number in the help is the cap the writer is actually given. It had drifted before: a
    /// documented 700 while the code already ran at 850 reads as a promise the tool is not keeping.
    /// </summary>
    [Fact]
    public void Map_help_names_the_budget_the_command_is_held_to()
    {
        Assert.Contains("default: 850", HelpCommand.MapHelp, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every command's documented default, checked against the value the writer is built with. The
    /// two drifted apart for map, where, unresolved and entrypoints over separate budget changes,
    /// and a documented number that is not the real one is read as a promise the tool is not
    /// keeping -- an agent sizes its query from it.
    /// </summary>
    [Theory]
    [InlineData("trace", HelpCommand.TraceHelp)]
    [InlineData("impl", HelpCommand.ImplHelp)]
    [InlineData("blast", HelpCommand.BlastRadiusHelp)]
    [InlineData("entrypoints", HelpCommand.EntrypointsHelp)]
    [InlineData("context", HelpCommand.ContextHelp)]
    [InlineData("path", HelpCommand.PathHelp)]
    [InlineData("cycles", HelpCommand.CyclesHelp)]
    [InlineData("unresolved", HelpCommand.UnresolvedHelp)]
    [InlineData("diff", HelpCommand.DiffHelp)]
    [InlineData("changes", HelpCommand.ChangesHelp)]
    [InlineData("where", HelpCommand.WhereHelp)]
    [InlineData("map", HelpCommand.MapHelp)]
    [InlineData("silence", HelpCommand.SilenceHelp)]
    public void Documented_budget_matches_the_code_default(string kind, string help)
    {
        var budget = QueryCommand.WriterFor(kind, new Options([])).Budget;

        Assert.Contains($"default: {budget}", help, StringComparison.Ordinal);
    }
}

