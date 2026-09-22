using CsMesh.Analysis;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Object initializer members and `with` members are writes. Before roles they were recorded as
/// ordinary member-access edges with no read/write distinction, and a field member inside an
/// initializer was not recorded at all because only the property form was handled.
/// </summary>
public sealed class InitializerWriteRoleTests
{
    private const string Source = """
        namespace Demo;
        public record D(int P)
        {
            public int F;
        }
        public class Use
        {
            public D Make() => new D(0) { P = 1, F = 2 };
            public D Copy(D d) => d with { P = 3, F = 4 };
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
            File.WriteAllText(Path.Combine(Root, "src", "D.cs"), Source);
            Graph = Indexer.Build(Root);
        }

        public Edge Edge(string ownerShort, string targetShort) =>
            Assert.Single(Graph.Edges, e =>
                e.Kind == EdgeKind.Call &&
                Graph.ById(e.From)?.Short == ownerShort &&
                Graph.ById(e.To)?.Short == targetShort);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public void Object_initializer_property_and_field_members_are_writes()
    {
        using var sandbox = new Sandbox();
        Assert.Equal(EdgeRole.Write, sandbox.Edge("Use.Make", "D.P").Role);
        Assert.Equal(EdgeRole.Write, sandbox.Edge("Use.Make", "D.F").Role);
    }

    [Fact]
    public void With_expression_property_and_field_members_are_writes()
    {
        using var sandbox = new Sandbox();
        Assert.Equal(EdgeRole.Write, sandbox.Edge("Use.Copy", "D.P").Role);
        Assert.Equal(EdgeRole.Write, sandbox.Edge("Use.Copy", "D.F").Role);
    }
}
