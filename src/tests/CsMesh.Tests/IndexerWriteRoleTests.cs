using CsMesh.Analysis;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// An indexer gets a node, and "obj[i] = x" is a write on it. Before the node existed the element
/// access had nothing to point at, so an indexer written only through its setter was absent from
/// --writes; an array element still records nothing because it has no symbol.
/// </summary>
public sealed class IndexerWriteRoleTests
{
    private const string Source = """
        namespace Demo;
        public class Table
        {
            private readonly int[] _a = new int[4];
            public int this[int i] { get => _a[i]; set => _a[i] = value; }
        }
        public class Use
        {
            public void Write(Table t) { t[0] = 1; }
            public void Compound(Table t) { t[1] += 1; }
            public void Read(Table t) { var x = t[2]; }
        }
        """;

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }
        public Graph Graph { get; }
        public Node IndexerNode { get; }

        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "csmesh-indexer-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            File.WriteAllText(Path.Combine(Root, "src", "T.cs"), Source);
            Graph = CsMesh.Analysis.Indexer.Build(Root);
            IndexerNode = Graph.Nodes.Single(n => n.Kind == "indexer");
        }

        public Edge Edge(string ownerShort) =>
            Assert.Single(Graph.Edges, e =>
                e.Kind == EdgeKind.Call &&
                Graph.ById(e.From)?.Short == ownerShort &&
                e.To == IndexerNode.Id);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public void An_indexer_gets_a_node()
    {
        using var sandbox = new Sandbox();
        Assert.Equal("Table.this[]", sandbox.IndexerNode.Short);
    }

    [Fact]
    public void Indexer_assignment_compound_and_read_roles()
    {
        using var sandbox = new Sandbox();
        Assert.Equal(EdgeRole.Write, sandbox.Edge("Use.Write").Role);
        Assert.Equal(EdgeRole.Read | EdgeRole.Write, sandbox.Edge("Use.Compound").Role);
        Assert.Equal(EdgeRole.Read, sandbox.Edge("Use.Read").Role);
    }
}
