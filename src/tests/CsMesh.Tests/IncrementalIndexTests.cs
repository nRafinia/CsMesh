using CsMesh.Analysis;
using CsMesh.Models;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// An incremental pass is only worth having if it is indistinguishable from a full one. These
/// check the three ways it can quietly fail to be: an id that moved, an edge appended twice, and
/// a dispatch that lost its handler because the map it matches against was rebuilt half empty.
/// </summary>
public sealed class IncrementalIndexTests(GraphFixture fixture) : IClassFixture<GraphFixture>
{
    /// <summary>
    /// A throwaway copy of the fixture sources on disk, so each test can edit a file without the
    /// others seeing it.
    /// </summary>
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }

        public Sandbox(string sourceRoot)
        {
            Root = Path.Combine(Path.GetTempPath(), "csmesh-inc-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);

            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
            {
                var target = Path.Combine(Root, Path.GetRelativePath(sourceRoot, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        public Graph Index() => Indexer.Build(Root);

        public void Touch(string name, string append)
        {
            var path = Directory.EnumerateFiles(Root, name, SearchOption.AllDirectories).Single();
            File.AppendAllText(path, append);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    private Sandbox NewSandbox() => new(fixture.Root);

    private static List<string> Dirty(Graph g) => GraphStore.DirtyFiles(g);

    [Fact]
    public void A_symbol_that_survives_an_edit_keeps_its_id()
    {
        using var box = NewSandbox();
        var before = box.Index();
        // Keyed by Key, not Name: Name drops parameter types, so overloads collide. That is the
        // whole reason Key exists.
        var ids = before.Nodes.ToDictionary(n => n.Key, n => n.Id, StringComparer.Ordinal);

        // Kinds.cs declares no interface, no handler and no registration, so the guard lets it
        // through. Anything in the CrossFileConstructs list would fall back to a full index and
        // this test would pass without testing anything.
        box.Touch("Kinds.cs", "\n// edited\n");

        var after = Indexer.BuildIncremental(before, Dirty(before));
        Assert.NotNull(after);

        var moved = after!.Nodes
            .Where(n => ids.TryGetValue(n.Key, out var old) && old != n.Id)
            .Select(n => n.Short)
            .ToList();

        Assert.Empty(moved);
    }

    [Fact]
    public void An_incremental_pass_does_not_append_an_edge_twice()
    {
        using var box = NewSandbox();
        var before = box.Index();
        var expected = before.Edges.Count;

        box.Touch("Kinds.cs", "\n// edited\n");

        var after = Indexer.BuildIncremental(before, Dirty(before));
        Assert.NotNull(after);

        var distinct = after!.Edges
            .Select(e => (e.From, e.To, e.Kind))
            .Distinct()
            .Count();

        Assert.Equal(after.Edges.Count, distinct);
        Assert.Equal(expected, after.Edges.Count);
    }

    [Fact]
    public void Editing_a_dispatch_site_keeps_the_edge_to_its_handler()
    {
        using var box = NewSandbox();
        var before = box.Index();

        var controller = before.Nodes.Single(n => n.Name == "Api.OrderController.Post");
        var mediatrBefore = before.Out(controller.Id).Count(e => e.Kind == EdgeKind.Mediatr);
        Assert.True(mediatrBefore > 0, "fixture should link Post to a handler before the edit");

        // Api.cs holds the _mediator.Send() call. The handler it dispatches to lives in
        // Handlers.cs, which this pass will not rebind -- so the only thing that can match them
        // is the dispatch table carried over from the last full index.
        box.Touch("Api.cs", "\n// edited\n");

        var after = Indexer.BuildIncremental(before, Dirty(before));
        Assert.NotNull(after);

        var again = after!.Nodes.Single(n => n.Name == "Api.OrderController.Post");
        Assert.Equal(mediatrBefore, after.Out(again.Id).Count(e => e.Kind == EdgeKind.Mediatr));
    }

    [Fact]
    public void An_edit_that_binds_across_files_falls_back_to_a_full_index()
    {
        using var box = NewSandbox();
        var before = box.Index();

        // Registration.cs contains TryAddScoped, which binds implementations declared elsewhere.
        box.Touch("Registration.cs", "\n// edited\n");

        Assert.Null(Indexer.BuildIncremental(before, Dirty(before)));
    }

    [Fact]
    public void A_deleted_file_leaves_no_edge_pointing_at_it()
    {
        using var box = NewSandbox();
        var before = box.Index();

        var path = Directory.EnumerateFiles(box.Root, "Loop.cs", SearchOption.AllDirectories).Single();
        File.Delete(path);

        var after = Indexer.BuildIncremental(before, Dirty(before));
        Assert.NotNull(after);

        var alive = after!.Nodes.Select(n => n.Id).ToHashSet();
        Assert.All(after.Edges, e =>
        {
            Assert.Contains(e.From, alive);
            Assert.Contains(e.To, alive);
        });

        Assert.DoesNotContain(after.Nodes, n => n.Name.StartsWith("Loop.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_graph_built_with_a_wider_scope_is_not_reused_for_a_narrower_one()
    {
        using var box = NewSandbox();

        var wide = Indexer.Build(box.Root, includeAllProjects: true);
        var narrow = Indexer.Build(box.Root);

        // Nothing on disk changed between the two, so the only thing that can tell them apart is
        // the recorded scope. Without it a plain 'csmesh index' reports an --all graph as current
        // and answers from files the caller just asked to leave out -- and keeps agreeing with
        // itself forever, because no file ever becomes dirty.
        Assert.True(wide.IndexedAllProjects);
        Assert.False(narrow.IndexedAllProjects);
    }
    
    [Fact]
    public void Every_node_carries_a_symbol_key()
    {
        using var box = NewSandbox();
        var graph = box.Index();

        // Without this an incremental pass has no identity to recycle and declines outright.
        Assert.All(graph.Nodes, n => Assert.NotEqual(string.Empty, n.Key));
        Assert.Equal(graph.Nodes.Count, graph.Nodes.Select(n => n.Key).Distinct(StringComparer.Ordinal).Count());
    }
}