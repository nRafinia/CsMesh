using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// `unresolved` printed every group and up to twelve rows under each, so a solution with thousands
/// of unbound sites ran the budget out instead of answering. It is a sample by construction, so it
/// now shows a bounded number of groups and rows and names what it withheld, exactly as map does.
/// </summary>
public sealed class UnresolvedCapTests
{
    private static BudgetWriter Writer(int budget = 4000) => new(budget, BudgetWriter.CompletionMarkerReserve);

    private static string Text(BudgetWriter w) => string.Join("\n", w.Lines);

    /// <summary>Six groups of ten sites: more than both the group and the per-group row caps.</summary>
    private static Graph ManySites()
    {
        var graph = new Graph { Root = "/tmp" };
        var reasons = new[]
        {
            "call/no-candidate-symbol", "call/ambiguous-overload",
            "di/ambiguous-type-name", "mediatr/no-handler",
            "type/unbound-type", "call/ambiguous-request-name"
        };

        foreach (var group in reasons)
        {
            var (kind, reason) = (group.Split('/')[0], group.Split('/')[1]);
            for (var i = 0; i < 10; i++)
            {
                graph.Unresolved.Add(new UnresolvedSite
                {
                    Kind = kind,
                    Reason = reason,
                    File = $"src/File{i}.cs",
                    Line = i + 1,
                    Expression = $"Call{i}()"
                });
            }
        }

        return graph;
    }

    /// <summary>
    /// The index caps the locations it keeps per kind while the reason counts are complete. Calling
    /// the sampled locations "shown" let a reader take them for all of them; the header has to say
    /// sample and leave the row accounting to the footer.
    /// </summary>
    [Fact]
    public void A_sampled_index_is_not_reported_as_if_every_location_were_shown()
    {
        var graph = new Graph { Root = "/tmp" };
        graph.UnresolvedByReason["call/no-candidate-symbol"] = 100;
        graph.Unresolved.Add(new UnresolvedSite
        {
            Kind = "call", Reason = "no-candidate-symbol",
            File = "src/Only.cs", Line = 1, Expression = "Only()"
        });

        var w = Writer();
        Queries.Unresolved(graph, null, null, w, []);

        var text = Text(w);
        Assert.Contains("100 unresolved site(s) in total", text, StringComparison.Ordinal);
        Assert.Contains("with locations in the sample", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1 shown", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bounded_sample_exits_zero_and_names_what_it_withheld()
    {
        var w = Writer();

        var exit = Queries.Unresolved(ManySites(), null, null, w, []);

        var text = Text(w);
        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("unresolved is a sample; withheld", text, StringComparison.Ordinal);
        Assert.Contains("row(s)", text, StringComparison.Ordinal);
        Assert.Contains("--kind ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("INCOMPLETE", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The footer is the whole point of the bound: without it a capped answer reads as complete.
    /// Written as a content note it was the first thing dropped when the rows filled the content
    /// cap -- reachable at the default on a real repo -- so it goes through the reserve as the
    /// closing line.
    ///
    /// Every integer from 40 to 1400, no step: a blank separator costs one token (Estimate("") == 1),
    /// so the window in which it is refused while the footer still fits is exactly one token wide.
    /// Any step above one can straddle it -- the earlier step of five skipped budget 247, which is
    /// where the footer was lost -- so step one is the only spacing that cannot miss it.
    /// </summary>
    [Fact]
    public void The_withheld_footer_survives_a_budget_that_barely_fits_the_rows()
    {
        for (var budget = 40; budget <= 1400; budget++)
        {
            var w = Writer(budget);
            var exit = Queries.Unresolved(ManySites(), null, null, w, []);

            if (exit != Exit.Ok) continue;

            Assert.Contains("unresolved is a sample; withheld", Text(w), StringComparison.Ordinal);
            Assert.True(w.Tokens <= budget, $"the footer pushed {w.Tokens} past the {budget} budget");
        }
    }

    [Fact]
    public void The_budget_running_out_is_still_an_incomplete_answer()
    {
        var w = Writer(100);

        var exit = Queries.Unresolved(ManySites(), null, null, w, []);

        Assert.Equal(Exit.OverBudget, exit);
        Assert.Contains("INCOMPLETE", Text(w), StringComparison.Ordinal);
    }
}
