using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Two writers can share a short display name -- Writer.Save declared in two namespaces. The
/// --writes rows then print the containing-type-qualified name for both, because two identical
/// "Writer.Save" rows tell the caller nothing about which one to open.
/// </summary>
public sealed class BlastRadiusWritesNameTests
{
    private const string Source = """
        namespace Demo { public class Target { public int P { get; set; } } }
        namespace N1 { public class Writer { public void Save(Demo.Target t) { t.P = 1; } } }
        namespace N2 { public class Writer { public void Save(Demo.Target t) { t.P = 2; } } }
        """;

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }
        public Graph Graph { get; }

        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "csmesh-writer-name-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            File.WriteAllText(Path.Combine(Root, "src", "W.cs"), Source);
            Graph = Indexer.Build(Root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public void Same_named_writers_are_shown_qualified_and_the_header_counts_writers()
    {
        using var sandbox = new Sandbox();
        var target = sandbox.Graph.Nodes.Single(n => n.Short == "Target.P");
        var writer = new BudgetWriter(4000);

        var exit = Queries.BlastRadius(sandbox.Graph, target, 3, writer, [], writes: true);
        var text = string.Join("\n", writer.Lines);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("writer(s)", text);
        Assert.Contains("N1.Writer.Save", text);
        Assert.Contains("N2.Writer.Save", text);
    }
}
