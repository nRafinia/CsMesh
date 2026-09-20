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

    /// <summary>
    /// The pathological case: forced notes and headers consume the content cap, and the marker must
    /// still appear. Before the truncating AddMarker, it returned false and a truncated answer could
    /// come back unmarked -- the same failure map had, reintroduced through a different door.
    /// </summary>
    [Fact]
    public void An_incomplete_answer_is_marked_even_when_notes_and_headers_consume_the_cap()
    {
        var w = new BudgetWriter(90, BudgetWriter.CompletionMarkerReserve);

        Assert.True(w.AddNote(new string('n', 120)));   // ~30 tokens against a 50-token content cap
        w.Force(new string('h', 120));                   // forced header, past the cap
        Assert.False(w.Add("content that does not fit"));

        Assert.True(w.AddMarker("INCOMPLETE: " + new string('x', 300)));

        Assert.Contains(w.Lines, line => line.StartsWith("INCOMPLETE", StringComparison.Ordinal));
        Assert.True(w.Tokens <= w.Budget);
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

    /// <summary>
    /// The opening-note reserve has to hold when content, note and marker all want a budget that
    /// cannot seat them. Written as a droppable note the version-gap line yielded to the rows;
    /// forced, it would eat the marker's room. It draws from its own pool, so the content is what
    /// gives way and both the note and the marker survive.
    /// </summary>
    [Fact]
    public void An_opening_note_and_the_marker_both_survive_a_starved_budget()
    {
        // At 80 with a 40-token marker reserve the content cap is 40, so a droppable 51-token note
        // would be refused and vanish. Through the reserve it is written, the content yields, and
        // the marker still fits in what is left.
        var w = new BudgetWriter(80, BudgetWriter.CompletionMarkerReserve);
        var note = new string('n', 200);   // ~51 tokens, inside the 60-token opening pool

        Assert.True(w.AddOpeningNote(note));
        Assert.False(w.Add(Line(100)));    // content is what gives way, not the note
        Assert.True(w.AddMarker("INCOMPLETE: " + new string('x', 400)));

        Assert.Contains(w.Lines, l => l.StartsWith("nnn", StringComparison.Ordinal));
        Assert.Contains(w.Lines, l => l.StartsWith("INCOMPLETE", StringComparison.Ordinal));
        Assert.True(w.Tokens <= w.Budget, $"the closing line pushed {w.Tokens} past the {w.Budget} budget");
    }

    /// <summary>
    /// The common path pays nothing for a note it never writes, and a note that is written shrinks
    /// the content cap by exactly its cost -- the shrink is visible rather than a mystery.
    /// </summary>
    [Fact]
    public void No_note_reserves_nothing_and_a_note_shrinks_the_content_cap_by_its_cost()
    {
        var plain = new BudgetWriter(100, BudgetWriter.CompletionMarkerReserve);
        Assert.Equal(0, plain.OpeningReserve);
        Assert.Equal(60, plain.Remaining);

        var noted = new BudgetWriter(100, BudgetWriter.CompletionMarkerReserve);
        var note = new string('n', 40);
        Assert.True(noted.AddOpeningNote(note));

        Assert.Equal(BudgetWriter.Estimate(note), noted.OpeningReserve);
        Assert.Equal(plain.Remaining - noted.OpeningReserve, noted.Remaining);
    }
}
