using CsMesh.Commands;
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
}
