using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// On overflow, the emitted size and the size the answer wanted are different numbers. Before this,
/// only the emitted size was recorded, so an exit=2 row read as "under budget" whenever the refused
/// line is what pushed it over, and the amount over was unrecoverable from usage.jsonl.
/// </summary>
public sealed class BudgetWriterOverflowTests
{
    private static string Line(int chars) => new('x', chars);

    [Fact]
    public void An_overflow_records_the_would_be_size_and_how_far_over()
    {
        var w = new BudgetWriter(20);
        Assert.True(w.Add(Line(20)));   // Estimate = ceil(21/4) = 6  -> 6
        Assert.True(w.Add(Line(20)));   //                          -> 12
        Assert.True(w.Add(Line(20)));   //                          -> 18
        Assert.False(w.Add(Line(20)));  // 18 + 6 = 24 > 20, refused

        Assert.True(w.Overflowed);
        Assert.Equal(18, w.Tokens);
        Assert.Equal(24, w.WouldBeTokens);
        Assert.Equal(4, w.OverBudgetBy);
    }

    [Fact]
    public void Forced_warning_lines_after_the_refusal_do_not_change_the_would_be_size()
    {
        var w = new BudgetWriter(20);
        w.Add(Line(20));
        w.Add(Line(20));
        w.Add(Line(20));
        w.Add(Line(20)); // refused
        var wouldBe = w.WouldBeTokens;
        var over = w.OverBudgetBy;

        w.Force("OVER BUDGET prose"); // the warning the query prints after the refusal

        Assert.Equal(wouldBe, w.WouldBeTokens);
        Assert.Equal(over, w.OverBudgetBy);
        Assert.True(w.Tokens > 18, "the forced warning does inflate the emitted total");
    }

    [Fact]
    public void A_clean_run_reports_would_be_equal_to_emitted_and_zero_over()
    {
        var w = new BudgetWriter(1000);
        Assert.True(w.Add(Line(40)));

        Assert.False(w.Overflowed);
        Assert.Equal(w.Tokens, w.WouldBeTokens);
        Assert.Equal(0, w.OverBudgetBy);
    }
}
