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
    public void Dependency_direction_follows_declared_references_not_symbol_edges()
    {
        // An interface edge runs abstraction -> implementation: right for "what runs", backwards
        // for "what needs what". Reading the map off symbol edges produced "Core depends on Hub",
        // the reverse of the architecture, and it looked plausible enough to ship.
        var graph = new Models.Graph
        {
            Root = "/tmp",
            ProjectReferences = new Dictionary<string, List<string>>
            {
                ["Hub"] = ["Core"],
                ["Core"] = []
            },
            Nodes =
            [
                new Models.Node { Id = 0, Name = "Core.IStore", Short = "IStore", Kind = "interface", File = "Core/A.cs", Project = "Core" },
                new Models.Node { Id = 1, Name = "Hub.Controller", Short = "Controller", Kind = "type", File = "Hub/B.cs", Project = "Hub" }
            ],
            Edges = [new Models.Edge { From = 0, To = 1, Kind = Models.EdgeKind.Interface }]
        };

        graph.Freeze();

        var w = Writer();
        Queries.Map(graph, null, w, []);
        var text = Text(w);

        Assert.Contains("Hub", text);
        Assert.Matches(@"Hub\s+\d+ type\(s\).*-> Core", text);
        Assert.DoesNotMatch(@"Core\s+\d+ type\(s\).*-> Hub", text);
    }

    [Fact]
    public void A_heading_is_never_printed_without_a_row_under_it()
    {
        // The failure only appears when the budget runs out between a title and its first row,
        // and picking a few round numbers missed that window entirely -- a negative control passed
        // against a version with the bug in it. Sweeping every budget is the only honest check.
        for (var budget = 5; budget <= 400; budget++)
        {
            var w = Writer(budget);
            Queries.Map(_f.Graph, null, w, []);

            var lines = w.Lines;
            for (var i = 0; i < lines.Count; i++)
            {
                if (!IsHeading(lines[i])) continue;

                Assert.True(i + 1 < lines.Count && lines[i + 1].StartsWith("  ", StringComparison.Ordinal),
                    $"'{lines[i]}' was printed with nothing under it at budget {budget}");
            }
        }
    }

    private static bool IsHeading(string line) =>
        line.Length > 0
        && char.IsLetter(line[0])
        && !line.StartsWith(" ", StringComparison.Ordinal)
        && !line.Contains("file(s),", StringComparison.Ordinal)
        && line.ToUpperInvariant().StartsWith(line.Split(' ')[0], StringComparison.Ordinal)
        && line.Split(' ')[0].ToUpperInvariant() == line.Split(' ')[0];

    [Fact]
    public void Test_projects_are_listed_apart_from_the_application()
    {
        // A topological ordering puts test projects on top, because everything is below them,
        // which buries the project carrying half the codebase at the bottom of the screen.
        var graph = new Models.Graph
        {
            Root = "/tmp",
            ProjectReferences = new Dictionary<string, List<string>> { ["App.Tests"] = ["App"], ["App"] = [] },
            Nodes =
            [
                new Models.Node { Id = 0, Name = "App.Service", Short = "Service", Kind = "type", File = "App/A.cs", Project = "App" },
                new Models.Node { Id = 1, Name = "App.Tests.ServiceTests", Short = "ServiceTests", Kind = "type", File = "Tests/B.cs", Project = "App.Tests", Tags = ["test"] }
            ]
        };

        graph.Freeze();

        var w = Writer();
        Queries.Map(graph, null, w, []);
        var text = Text(w);

        Assert.Contains("TEST PROJECTS", text);
        Assert.True(text.IndexOf("PROJECTS  deepest", StringComparison.Ordinal)
                    < text.IndexOf("TEST PROJECTS", StringComparison.Ordinal));
    }

    [Fact]
    public void A_shared_name_prefix_is_not_repeated_on_every_line()
    {
        var graph = new Models.Graph
        {
            Root = "/tmp",
            ProjectReferences = new Dictionary<string, List<string>> { ["Acme.Api"] = ["Acme.Core"], ["Acme.Core"] = [] },
            Nodes =
            [
                new Models.Node { Id = 0, Name = "Acme.Core.T", Short = "T", Kind = "type", File = "Core/A.cs", Project = "Acme.Core" },
                new Models.Node { Id = 1, Name = "Acme.Api.U", Short = "U", Kind = "type", File = "Api/B.cs", Project = "Acme.Api" }
            ]
        };

        graph.Freeze();

        var w = Writer();
        Queries.Map(graph, null, w, []);

        var rows = w.Lines.Where(l => l.StartsWith("  ") && l.Contains("type(s)")).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.DoesNotContain("Acme.", r));
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