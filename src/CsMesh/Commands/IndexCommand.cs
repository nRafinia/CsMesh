using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Storage;

namespace CsMesh.Commands;

public static class IndexCommand
{
    public static int Execute(string root, Options opt)
    {
        var startTime = DateTime.UtcNow;
        var graph = Indexer.Build(root, message => Dbg.Log(message), opt.Flag("all"));
        GraphStore.Save(graph);

        var elapsedSeconds = (DateTime.UtcNow - startTime).TotalSeconds;
        Console.WriteLine($"indexed {graph.Files.Count} files -> {graph.Nodes.Count} nodes, {graph.Edges.Count} edges in {elapsedSeconds:F1}s");

        if (graph.SkippedProjects.Count > 0)
        {
            Console.WriteLine($"skipped {graph.SkippedProjects.Count} project(s): {graph.SkippedProjectsReason}");
            foreach (var project in graph.SkippedProjects.Take(5))
            {
                Console.WriteLine($"  {project}");
            }

            if (graph.SkippedProjects.Count > 5)
            {
                Console.WriteLine($"  ... and {graph.SkippedProjects.Count - 5} more; see csmesh doctor");
            }

            Console.WriteLine("  index them anyway with: csmesh index --all");
        }

        // Unbound call sites are missing edges, not cosmetic warnings. Say so at index time
        // rather than letting a thin answer look like a complete one.
        if (graph.UnresolvedCallSites > 0)
        {
            Console.WriteLine($"warning: {graph.UnresolvedCallSites} call site(s) could not be bound against {graph.ReferenceCount} references.");

            // Naming the build as the cause when bin/ already holds hundreds of assemblies is a
            // wrong diagnosis stated with confidence, and it sent one investigation down the
            // wrong path for a while.
            Console.WriteLine(graph.OutputReferences == 0
                ? "         Nothing was loaded from bin/. Run 'dotnet build', then index again."
                : "         Run 'csmesh doctor' for what the compiler said about them.");
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