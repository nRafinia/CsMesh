using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Storage;

namespace CsMesh.Commands;

public static class IndexCommand
{
    public static int Execute(string root, Options opt)
    {
        var startTime = DateTime.UtcNow;
        var graph = Indexer.Build(root, message => Dbg.Log(message));
        GraphStore.Save(graph);

        var elapsedSeconds = (DateTime.UtcNow - startTime).TotalSeconds;
        Console.WriteLine($"indexed {graph.Files.Count} files -> {graph.Nodes.Count} nodes, {graph.Edges.Count} edges in {elapsedSeconds:F1}s");

        // Unbound call sites are missing edges, not cosmetic warnings. Say so at index time
        // rather than letting a thin answer look like a complete one.
        if (graph.UnresolvedCallSites > 0)
        {
            Console.WriteLine($"warning: {graph.UnresolvedCallSites} call site(s) could not be bound against {graph.ReferenceCount} references.");
            Console.WriteLine("         Build the solution first (dotnet build) so bin/ assemblies are available; some edges are missing.");
        }

        if (Dbg.On)
        {
            foreach (var group in graph.Edges.GroupBy(e => e.Kind).OrderByDescending(x => x.Count()))
            {
                Dbg.Log($"edges {group.Key}: {group.Count()}");
            }

            foreach (var node in graph.Nodes.Where(n => n.Tags.Count > 0).Take(15))
            {
                Dbg.Log($"tag {node.Short}: {string.Join(",", node.Tags)}");
            }
        }

        return Exit.Ok;
    }
}
