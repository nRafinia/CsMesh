using System.Text;
using System.Text.RegularExpressions;
using CsMesh.Analysis;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// On overflow blast-radius said "nearly complete" whenever the refused row pushed it only a little
/// past the budget, and suggested a budget measured from that one refusal -- a number that still
/// overflowed when re-run. It now states rows shown of the reached total and names a budget that
/// seats every row. "nearly complete" survives only for the one case it was true: the answer fit
/// the requested budget and only the completion-marker reserve cut it.
/// </summary>
public sealed class BlastRadiusMarkerTests : IDisposable
{
    private readonly string _root;

    public BlastRadiusMarkerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-marker-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        var source = new StringBuilder();
        source.AppendLine("namespace Demo;");
        source.AppendLine("public static class Hub");
        source.AppendLine("{");
        source.AppendLine("    public static int A() => 1;");
        source.AppendLine("    public static int B() => 2;");
        source.AppendLine("    public static int C() => 3;");
        source.AppendLine("}");

        // 95 distinct production callers, split across the three members: enough that the full
        // answer overruns a small budget, which is the shape the old marker mis-described.
        for (var i = 1; i <= 95; i++)
        {
            var member = (char)('A' + i % 3);
            source.AppendLine($"public class Caller{i:000} {{ public int Go() => Hub.{member}(); }}");
        }

        File.WriteAllText(Path.Combine(_root, "src", "Callers.cs"), source.ToString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void Overflow_names_rows_shown_of_total_and_a_budget_that_fits()
    {
        var graph = Indexer.Build(_root);
        graph.Freeze();
        var hub = graph.Nodes.Single(n => n.Short == "Hub");

        var w = new BudgetWriter(120, BudgetWriter.CompletionMarkerReserve);
        var exit = Queries.BlastRadius(graph, hub, 1, w, []);

        Assert.Equal(Exit.OverBudget, exit);

        var marker = w.Lines.Last(l => l.StartsWith("INCOMPLETE", StringComparison.Ordinal));
        Assert.Contains("of 95 reached member(s) shown", marker, StringComparison.Ordinal);

        var fit = int.Parse(Regex.Match(marker, @"raise --budget to (\d+)").Groups[1].Value);

        // The suggested budget is the whole answer, not the first refusal's lower bound: re-running
        // at exactly it completes, which the old "raise --budget to N" did not.
        var w2 = new BudgetWriter(fit, BudgetWriter.CompletionMarkerReserve);
        var exit2 = Queries.BlastRadius(graph, hub, 1, w2, []);

        Assert.Equal(Exit.Ok, exit2);
        Assert.DoesNotContain(w2.Lines, l => l.StartsWith("INCOMPLETE", StringComparison.Ordinal));
    }

    [Fact]
    public void A_marker_that_only_needed_room_still_says_nearly_complete()
    {
        // No content row was refused: the answer fit, so only the closing line lacked room. That is
        // the one case the "nearly complete" wording was ever true for, and it must survive.
        var w = new BudgetWriter(100, BudgetWriter.CompletionMarkerReserve);

        Assert.True(w.Add("a line of content"));
        Assert.False(w.Overflowed);

        var marker = Queries.IncompleteMarker(w);

        Assert.Contains("nearly complete", marker, StringComparison.Ordinal);
    }
}
