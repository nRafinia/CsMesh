using CsMesh.Analysis;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

public sealed class MapTests : IClassFixture<GraphFixture>
{
    private readonly GraphFixture _f;

    public MapTests(GraphFixture fixture) => _f = fixture;

    private static BudgetWriter Writer(int budget = 4000) => new(budget);

    private static string Text(BudgetWriter w) => string.Join("\n", w.Lines);

    [Fact]
    public void Map_leads_with_the_size_of_what_it_indexed()
    {
        var w = Writer();
        var exit = Queries.Map(_f.Graph, null, w, []);

        Assert.Equal(Exit.Ok, exit);
        Assert.Matches(@"\d+ file\(s\), \d+ symbol\(s\), \d+ edge\(s\)", w.Lines[0]);
    }

    [Fact]
    public void Map_separates_behaviour_hotspots_from_data_hotspots()
    {
        // A property with twenty readers and a method with twenty callers are both hotspots, but
        // for different kinds of change. One list would bury the methods under getters.
        var w = Writer();
        Queries.Map(_f.Graph, null, w, []);
        var text = Text(w);

        Assert.Contains("BUSIEST", text);
        Assert.Contains("MOST READ", text);
    }

    [Fact]
    public void Map_excludes_test_code_from_the_ranking()
    {
        var w = Writer();
        Queries.Map(_f.Graph, null, w, []);

        Assert.DoesNotContain("OrderStoreTests", Text(w));
    }

    [Fact]
    public void Map_under_a_subtree_counts_only_that_subtree()
    {
        var all = Writer();
        Queries.Map(_f.Graph, null, all, []);

        var scoped = Writer();
        var exit = Queries.Map(_f.Graph, "src/nothing-here", scoped, []);

        Assert.Equal(Exit.NotFound, exit);
        Assert.NotEqual(all.Lines[0], scoped.Lines[0]);
    }

    [Fact]
    public void Map_reports_no_project_section_when_no_csproj_exists()
    {
        // The fixture is bare .cs files. Inventing a project name would be a guess.
        var w = Writer();
        Queries.Map(_f.Graph, null, w, []);

        Assert.DoesNotContain("PROJECTS", Text(w));
    }

    [Fact]
    public void Prose_answers_are_not_lost_when_a_caller_asks_for_structure()
    {
        // silence, map and changes answer mostly in prose. A JSON consumer reading only Rows
        // would get an exit code and nothing to act on, so the rendered lines travel with it.
        var w = Writer();
        Queries.Map(_f.Graph, null, w, []);

        Assert.NotEmpty(w.Lines);
        Assert.True(w.Lines.Count > w.Rows.Count,
            "most of a map is grouping and headings, not rows; both have to reach the caller");
    }

    [Fact]
    public void Under_filter_narrows_entrypoints_without_changing_their_meaning()
    {
        var all = Writer();
        Queries.Entrypoints(_f.Graph, null, null, all, []);

        var none = Writer();
        var exit = Queries.Entrypoints(_f.Graph, null, "src/no-such-directory", none, []);

        Assert.Equal(Exit.NotFound, exit);
        Assert.Contains("no entrypoints matched", Text(none));
        Assert.NotEqual(Text(all), Text(none));
    }
}
