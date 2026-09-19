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

    [Fact]
    public void The_budget_running_out_is_still_an_incomplete_answer()
    {
        var w = Writer(100);

        var exit = Queries.Unresolved(ManySites(), null, null, w, []);

        Assert.Equal(Exit.OverBudget, exit);
        Assert.Contains("INCOMPLETE", Text(w), StringComparison.Ordinal);
    }
}
