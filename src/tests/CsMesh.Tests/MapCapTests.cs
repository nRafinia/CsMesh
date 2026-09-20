using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A capped map names what its section caps withheld in a closing line, and that line is the whole
/// point of the bound: without it a capped map reads as the whole map. Written as a content note it
/// was the first thing dropped when the sections filled the content cap -- reachable at any budget
/// where the sections just fit. The sweep is the honest check; a few round budgets miss the window
/// between "the sections fit" and "the footer no longer fits with them".
/// </summary>
public sealed class MapCapTests
{
    private static BudgetWriter Writer(int budget) => new(budget, BudgetWriter.CompletionMarkerReserve);

    private static string Text(BudgetWriter w) => string.Join("\n", w.Lines);

    /// <summary>
    /// More projects, entrypoint files, busiest methods and most-read data than the section caps
    /// select, so map always ends in its capped branch (capped.Count &gt; 0) while still exiting 0.
    /// </summary>
    private static Graph Capped()
    {
        var graph = new Graph { Root = "/tmp" };
        var id = 0;
        int Next() => id++;

        var projects = Enumerable.Range(0, 12).Select(i => "Proj" + i).ToList();
        foreach (var p in projects) graph.ProjectReferences[p] = [];

        foreach (var p in projects)
        {
            graph.Nodes.Add(new Node
            {
                Id = Next(), Name = $"{p}.Service", Short = "Service", Kind = "type",
                File = $"src/{p}/Service.cs", Project = p
            });
        }

        // 7 entrypoint files: ENTRYPOINTS caps at 5.
        for (var i = 0; i < 7; i++)
        {
            graph.Nodes.Add(new Node
            {
                Id = Next(), Name = $"Api.Controller{i}.Get", Short = $"Controller{i}.Get", Kind = "method",
                File = $"src/Api/Controller{i}.cs", Project = projects[i % projects.Count],
                Tags = [$"http:GET /c{i}"]
            });
        }

        // 8 methods with direct callers: BUSIEST caps at 5.
        for (var i = 0; i < 8; i++)
        {
            var target = new Node
            {
                Id = Next(), Name = $"Core.Busy{i}.Run", Short = $"Busy{i}.Run", Kind = "method",
                File = $"src/Core/Busy{i}.cs", Project = projects[i % projects.Count]
            };
            graph.Nodes.Add(target);

            for (var c = 0; c < 3; c++)
            {
                var caller = new Node
                {
                    Id = Next(), Name = $"Callers.C{i}_{c}.Go", Short = $"C{i}_{c}.Go", Kind = "method",
                    File = $"src/Callers/C{i}_{c}.cs", Project = projects[(i + c) % projects.Count]
                };
                graph.Nodes.Add(caller);
                graph.Edges.Add(new Edge { From = caller.Id, To = target.Id, Kind = EdgeKind.Call });
            }
        }

        // 6 properties with readers: MOST READ caps at 3.
        for (var i = 0; i < 6; i++)
        {
            var property = new Node
            {
                Id = Next(), Name = $"Data.Prop{i}", Short = $"Prop{i}", Kind = "property",
                File = $"src/Data/Prop{i}.cs", Project = projects[i % projects.Count]
            };
            graph.Nodes.Add(property);

            for (var c = 0; c < 3; c++)
            {
                var reader = new Node
                {
                    Id = Next(), Name = $"Readers.R{i}_{c}.Read", Short = $"R{i}_{c}.Read", Kind = "method",
                    File = $"src/Readers/R{i}_{c}.cs", Project = projects[(i + c) % projects.Count]
                };
                graph.Nodes.Add(reader);
                graph.Edges.Add(new Edge { From = reader.Id, To = property.Id, Kind = EdgeKind.Call });
            }
        }

        graph.Freeze();
        return graph;
    }

    [Fact]
    public void The_withheld_footer_survives_a_budget_that_barely_fits_the_sections()
    {
        var graph = Capped();

        for (var budget = 60; budget <= 1400; budget += 5)
        {
            var w = Writer(budget);
            var exit = Queries.Map(graph, null, w, []);

            if (exit != Exit.Ok) continue;

            Assert.Contains("# map is a summary; withheld", Text(w), StringComparison.Ordinal);
            Assert.True(w.Tokens <= budget, $"the footer pushed {w.Tokens} past the {budget} budget");
        }
    }

    /// <summary>
    /// The footer has to appear on a capped map regardless of budget, and this graph caps every
    /// section -- so a budget that fits at all must show it. A run that never reaches exit 0 would
    /// make the sweep above vacuous.
    /// </summary>
    [Fact]
    public void The_fixture_actually_caps_at_a_budget_that_fits()
    {
        var w = Writer(4000);
        var exit = Queries.Map(Capped(), null, w, []);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("# map is a summary; withheld", Text(w), StringComparison.Ordinal);
        Assert.DoesNotContain("INCOMPLETE", Text(w), StringComparison.Ordinal);
    }
}
