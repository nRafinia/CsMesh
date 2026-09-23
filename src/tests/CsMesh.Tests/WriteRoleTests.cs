using CsMesh.Analysis;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Write roles for the plainest forms: assignment, compound assignment and ++/--, on a property
/// and a field, written bare or through a receiver. Before roles, a write and a read were the same
/// edge, so "where is this written" could not be answered; and a bare field write -- every
/// constructor assignment to a readonly field -- produced no edge at all.
/// </summary>
public sealed class WriteRoleTests
{
    private const string Source = """
        namespace Demo;
        public class W
        {
            public int P { get; set; }
            public int F;
            public int Init { get; init; }
            public readonly int Ro;
            public W() { Ro = 1; Init = 2; F = 3; }
            public void BareWrite() { P = 1; }
            public void MemberWrite(W o) { o.P = 1; o.F = 1; }
            public void CompoundBare() { P += 1; F += 1; }
            public void CompoundMember(W o) { o.P += 1; o.F += 1; }
            public void IncDecBare() { P++; F--; }
            public void IncDecMember(W o) { o.P++; o.F--; }
            public void ReadOnly() { var a = this.P; }
            public void ReadThenWrite()
            {
                var a = this.P;
                this.P = a + 1;
            }
        }
        """;

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }
        public Graph Graph { get; }

        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "csmesh-role-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            File.WriteAllText(Path.Combine(Root, "src", "W.cs"), Source);
            Graph = Indexer.Build(Root);
        }

        public Edge? Edge(string ownerShort, string targetShort) => Graph.Edges.FirstOrDefault(e =>
            e.Kind == EdgeKind.Call &&
            Graph.ById(e.From)?.Short == ownerShort &&
            Graph.ById(e.To)?.Short == targetShort);

        public Edge SingleEdgeTo(string targetShort) =>
            Assert.Single(Graph.Edges, e =>
                e.Kind == EdgeKind.Call && Graph.ById(e.To)?.Short == targetShort);

        public int LineOf(string text)
        {
            var lines = Source.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains(text, StringComparison.Ordinal)) return i + 1;
            throw new InvalidOperationException($"no source line contains '{text}'");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public void A_bare_property_write_is_a_write()
    {
        using var sandbox = new Sandbox();
        Assert.Equal(EdgeRole.Write, sandbox.Edge("W.BareWrite", "W.P")!.Role);
    }

    [Fact]
    public void Member_writes_to_a_property_and_a_field_are_writes()
    {
        using var sandbox = new Sandbox();
        Assert.Equal(EdgeRole.Write, sandbox.Edge("W.MemberWrite", "W.P")!.Role);
        Assert.Equal(EdgeRole.Write, sandbox.Edge("W.MemberWrite", "W.F")!.Role);
    }

    [Fact]
    public void Compound_assignment_is_read_write()
    {
        using var sandbox = new Sandbox();
        foreach (var owner in new[] { "W.CompoundBare", "W.CompoundMember" })
        {
            Assert.Equal(EdgeRole.Read | EdgeRole.Write, sandbox.Edge(owner, "W.P")!.Role);
            Assert.Equal(EdgeRole.Read | EdgeRole.Write, sandbox.Edge(owner, "W.F")!.Role);
        }
    }

    [Fact]
    public void Increment_and_decrement_are_read_write()
    {
        using var sandbox = new Sandbox();
        foreach (var owner in new[] { "W.IncDecBare", "W.IncDecMember" })
        {
            Assert.Equal(EdgeRole.Read | EdgeRole.Write, sandbox.Edge(owner, "W.P")!.Role);
            Assert.Equal(EdgeRole.Read | EdgeRole.Write, sandbox.Edge(owner, "W.F")!.Role);
        }
    }

    [Fact]
    public void A_read_only_access_is_a_read_with_no_site()
    {
        using var sandbox = new Sandbox();
        var edge = sandbox.Edge("W.ReadOnly", "W.P")!;
        Assert.Equal(EdgeRole.Read, edge.Role);
        Assert.Null(edge.Site);
    }

    [Fact]
    public void A_read_and_a_write_to_one_member_are_one_edge_with_the_write_site()
    {
        using var sandbox = new Sandbox();

        var edges = sandbox.Graph.Edges.Where(e =>
            e.Kind == EdgeKind.Call &&
            sandbox.Graph.ById(e.From)?.Short == "W.ReadThenWrite" &&
            sandbox.Graph.ById(e.To)?.Short == "W.P").ToList();

        var edge = Assert.Single(edges);
        Assert.Equal(EdgeRole.Read | EdgeRole.Write, edge.Role);
        Assert.Equal($"src/W.cs:{sandbox.LineOf("this.P = a + 1;")}", edge.Site!.Replace('\\', '/'));
    }

    [Fact]
    public void Constructor_writes_to_readonly_and_init_only_members_are_writes()
    {
        using var sandbox = new Sandbox();

        foreach (var target in new[] { "W.Ro", "W.Init" })
        {
            var edge = sandbox.SingleEdgeTo(target);
            Assert.True((edge.Role & EdgeRole.Write) != 0, $"{target} should be a write, was {edge.Role}");
        }
    }
}
