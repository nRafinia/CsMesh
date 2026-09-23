using CsMesh.Analysis;
using CsMesh.Models;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// v14 added <see cref="Edge.Role"/>. A v13 graph carries no roles, so a --writes query would
/// read every member access as a read and silently under-answer; the version guard exists to
/// reject that graph instead of half-reading it. Role is optional and only set on member-access
/// edges, so a graph with no writes must not grow a "role" key, and one that has a role must get
/// it back exactly.
/// </summary>
public sealed class EdgeRoleFormatTests
{
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }

        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "csmesh-role-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            File.WriteAllText(Path.Combine(Root, "src", "Thing.cs"),
                """
                namespace Demo;
                public sealed class Thing { public int Value { get; set; } public void Go() { } }
                public sealed class Caller { public void Run(Thing t) { t.Go(); } }
                """);
        }

        public Graph Index() => Indexer.Build(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public void A_v13_graph_is_rejected()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();
        graph.FormatVersion = 13;
        GraphStore.Save(graph);

        Assert.Null(GraphStore.Load(sandbox.Root, out var problem));
        Assert.Contains("v13", problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_role_survives_a_save_and_load()
    {
        using var sandbox = new Sandbox();
        var graph = sandbox.Index();
        graph.Edges[0].Role = EdgeRole.Read | EdgeRole.Write;
        GraphStore.Save(graph);

        var reloaded = GraphStore.Load(sandbox.Root, out var problem);

        Assert.Null(problem);
        Assert.NotNull(reloaded);
        Assert.Equal(EdgeRole.Read | EdgeRole.Write, reloaded!.Edges[0].Role);
    }

    [Fact]
    public void A_graph_without_roles_does_not_serialize_a_role_key()
    {
        using var sandbox = new Sandbox();
        GraphStore.Save(sandbox.Index());

        var json = File.ReadAllText(GraphStore.PathFor(sandbox.Root));

        Assert.DoesNotContain("\"role\"", json, StringComparison.Ordinal);
    }
}
