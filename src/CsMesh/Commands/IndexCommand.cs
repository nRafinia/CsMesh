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

        if (Dbg.On)
        {
            foreach (var group in graph.Edges.GroupBy(e => e.Kind).OrderByDescending(x => x.Count()))
            {
                Dbg.Log($"edges {group.Key}: {group.Count()}");
            }

            var tagged = graph.Nodes.Where(n => n.Tags.Count > 0).Take(15);
            foreach (var node in tagged)
            {
                Dbg.Log($"tag {node.Short}: {string.Join(",", node.Tags)}");
            }
        }

        return Exit.Ok;
    }
}
