using CsMesh.Analysis;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A member element of a deconstruction target is a write; a local or a discard in the same
/// position is not. Before this the member element read as an ordinary read, so a property set
/// only through deconstruction was missing from --writes.
/// </summary>
public sealed class DeconstructionWriteTests
{
    private const string Source = """
        namespace Demo;
        public class D { public int P { get; set; } public int F; }
        public class U
        {
            public void Deconstruct(D o)
            {
                var a = 0;
                (a, o.P) = (1, 2);
                (a, o.F) = (3, 4);
                (a, _) = (5, 6);
            }
        }
        """;

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }
        public Graph Graph { get; }

        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "csmesh-decon-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            File.WriteAllText(Path.Combine(Root, "src", "U.cs"), Source);
            Graph = Indexer.Build(Root);
        }

        public Edge Edge(string targetShort) =>
            Assert.Single(Graph.Edges, e =>
                e.Kind == EdgeKind.Call &&
                Graph.ById(e.From)?.Short == "U.Deconstruct" &&
                Graph.ById(e.To)?.Short == targetShort);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public void Member_elements_of_a_deconstruction_are_writes()
    {
        using var sandbox = new Sandbox();
        Assert.Equal(EdgeRole.Write, sandbox.Edge("D.P").Role);
        Assert.Equal(EdgeRole.Write, sandbox.Edge("D.F").Role);
    }

    [Fact]
    public void Locals_and_discards_record_nothing()
    {
        using var sandbox = new Sandbox();
        var owner = sandbox.Graph.Nodes.Single(n => n.Short == "U.Deconstruct");
        var edges = sandbox.Graph.Edges.Where(e => e.From == owner.Id && e.Kind == EdgeKind.Call).ToList();
        Assert.Equal(2, edges.Count);
    }
}
