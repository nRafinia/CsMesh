using CsMesh.Analysis;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A ref/out argument writes the member it is passed, and an in argument does not. Before roles
/// these were all the same read edge, so an out parameter that initializes a field was invisible
/// to --writes and a caller could not find who set it.
/// </summary>
public sealed class RefOutRoleTests
{
    private const string Source = """
        namespace Demo;
        public class W
        {
            public int P { get; set; }
            public int F;
            public void RefMember(W o) => Touch(ref o.F);
            public void OutMember(W o) => Init(out o.F);
            public void InMember(W o) => Use(in o.F);
            public void RefBare() => Touch(ref F);
            public void OutBare() => Init(out F);
            private static void Touch(ref int x) { }
            private static void Init(out int x) { x = 0; }
            private static void Use(in int x) { }
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

        public Edge Edge(string ownerShort) =>
            Assert.Single(Graph.Edges, e =>
                e.Kind == EdgeKind.Call &&
                Graph.ById(e.From)?.Short == ownerShort &&
                Graph.ById(e.To)?.Short == "W.F");

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public void A_ref_argument_reads_and_writes()
    {
        using var sandbox = new Sandbox();
        Assert.Equal(EdgeRole.Read | EdgeRole.Write, sandbox.Edge("W.RefMember").Role);
        Assert.Equal(EdgeRole.Read | EdgeRole.Write, sandbox.Edge("W.RefBare").Role);
    }

    [Fact]
    public void An_out_argument_writes_only()
    {
        using var sandbox = new Sandbox();
        Assert.Equal(EdgeRole.Write, sandbox.Edge("W.OutMember").Role);
        Assert.Equal(EdgeRole.Write, sandbox.Edge("W.OutBare").Role);
    }

    [Fact]
    public void An_in_argument_is_not_a_write()
    {
        using var sandbox = new Sandbox();
        var role = sandbox.Edge("W.InMember").Role;
        Assert.NotNull(role);
        Assert.Equal(EdgeRole.Read, role);
    }
}
