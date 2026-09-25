using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;

namespace CsMesh.Commands;

/// <summary>
/// Parses <c>csmesh export</c>'s options and hands the work to
/// <see cref="Queries.RenderExport"/>. No rendering lives here; this is the CLI surface and the
/// budget/exit-code contract around it (ADR 0004).
/// </summary>
public static class ExportCommand
{
    /// <summary>
    /// The default cap for export, fixed by ADR 0004. The project level fits it; the namespace and
    /// neighbourhood levels do not, which is the whole reason <c>--out</c> exists.
    /// </summary>
    public const int DefaultBudget = 1500;

    public static int Execute(string root, Options opt)
    {
        if (!TryReadOptions(opt, out var format, out var level, out var direction, out var depth, out var error))
        {
            Console.Error.WriteLine(error);
            return Exit.Usage;
        }

        var graph = GraphStore.Load(root, out var problem);
        if (graph == null)
        {
            Console.Error.WriteLine($"no usable index ({problem}). run: csmesh index");
            return Exit.NoIndex;
        }

        var budget = opt.Int("budget", DefaultBudget);

        Node? start = null;
        if (level == "neighbourhood")
        {
            // Exactly the resolution trace uses, selector included: exit 1 and 3 mean here what
            // they mean there, and the candidate list is the same text.
            var resolver = new BudgetWriter(budget, BudgetWriter.CompletionMarkerReserve);
            var resolution = new QueryResult();
            start = QueryCommand.Single(graph, opt.Positional[0], opt.Value("project"),
                resolver, resolution, json: false, [], out var resolveExit);
            if (start == null)
            {
                resolver.Flush();
                return resolveExit;
            }
        }

        var request = new Queries.ExportRequest(
            format, level, start, depth, direction,
            opt.Flag("include-tests"), opt.Flag("all-edges"));

        var result = Queries.RenderExport(graph, request);

        if (opt.Value("out") is { Length: > 0 } outPath)
        {
            if (!TryResolveOutPath(root, outPath, out var fullPath, out var pathError))
            {
                Console.Error.WriteLine(pathError);
                return Exit.Usage;
            }

            return WriteFile(result, fullPath, outPath, format, level, budget);
        }

        return WriteStdout(result, budget, level);
    }

    /// <summary>
    /// A file destination must stay inside the repository and under an existing directory: a
    /// typo that would drop a diagram beside the checkout, or into a directory that is not there,
    /// is a usage error rather than a write that half-succeeds. The check is on the resolved path,
    /// so <c>..</c> cannot walk out.
    /// </summary>
    internal static bool TryResolveOutPath(string root, string outPath, out string fullPath, out string? error)
    {
        fullPath = "";
        error = null;

        try
        {
            var rootFull = Path.GetFullPath(root);
            fullPath = Path.GetFullPath(outPath, rootFull);

            var relative = Path.GetRelativePath(rootFull, fullPath);
            if (relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
            {
                error = $"--out '{outPath}' is outside the repository root";
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"--out '{outPath}' is not a usable path: {ex.Message}";
            return false;
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            error = $"--out '{outPath}' has no existing parent directory";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Writes the complete render to a file by temp-plus-rename, as <see cref="GraphStore"/> writes
    /// the graph, so a reader never sees a half-written diagram; stdout gets only a budgeted
    /// summary, because the file is an artifact rather than answer text (ADR 0004). Any I/O failure
    /// is exit 70: the summary was not produced, and that is an internal fault, not a bad option.
    /// </summary>
    internal static int WriteFile(
        Queries.ExportResult result, string fullPath, string givenPath, string format, string level, int budget)
    {
        var temp = fullPath + ".tmp-" + Environment.ProcessId + "-" + Environment.CurrentManagedThreadId;

        try
        {
            File.WriteAllLines(temp, result.Lines);
            File.Move(temp, fullPath, overwrite: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception cleanup) { Dbg.Log($"could not clean export temp file: {cleanup.Message}"); }

            Console.Error.WriteLine($"csmesh: could not write '{givenPath}': {ex.Message}");
            return Exit.Internal;
        }

        var writer = new BudgetWriter(budget, BudgetWriter.CompletionMarkerReserve);
        writer.Force($"{level} {format} -> {givenPath}");
        writer.Force($"nodes: {result.Nodes}, edges: {result.Edges}");

        var withheld = new List<string>();
        if (result.TestNodesWithheld > 0 || result.TestEdgesWithheld > 0)
            withheld.Add($"test code {result.TestNodesWithheld} node(s), {result.TestEdgesWithheld} edge(s)");
        if (result.TypeUseEdgesWithheld > 0)
            withheld.Add($"TypeUse {result.TypeUseEdgesWithheld} edge(s)");
        if (result.InternalEdgesWithheld > 0)
            withheld.Add($"internal {result.InternalEdgesWithheld} edge(s)");
        if (result.SyntheticNodesWithheld > 0 || result.SyntheticEdgesWithheld > 0)
            withheld.Add($"synthetic {result.SyntheticNodesWithheld} node(s), {result.SyntheticEdgesWithheld} edge(s)");
        if (result.NodesBeyondDepth > 0 || result.EdgesBeyondDepth > 0)
            withheld.Add($"beyond depth {result.NodesBeyondDepth} node(s), {result.EdgesBeyondDepth} edge(s)");
        if (withheld.Count > 0) writer.Force("withheld: " + string.Join("; ", withheld));

        writer.Flush();
        return Exit.Ok;
    }

    /// <summary>
    /// The shared option contract. Returns false with the stderr line a usage error prints, so the
    /// same parsing backs the CLI and the MCP forwarding path (which builds an Options from its
    /// arguments and calls this command, not a second implementation).
    /// </summary>
    internal static bool TryReadOptions(
        Options opt, out string format, out string level, out string direction, out int depth, out string? error)
    {
        format = opt.Get("format", "mermaid");
        direction = opt.Get("direction", "both");
        depth = opt.Int("depth", 1);
        error = null;

        if (format is not ("mermaid" or "dot"))
        {
            error = $"unknown --format '{format}'. use: mermaid or dot";
            level = "";
            return false;
        }

        if (opt.Value("level") is { } explicitLevel)
        {
            if (explicitLevel is not ("project" or "namespace" or "neighbourhood"))
            {
                error = $"unknown --level '{explicitLevel}'. use: project, namespace or neighbourhood";
                level = "";
                return false;
            }

            level = explicitLevel;
        }
        else
        {
            level = opt.Positional.Count > 0 ? "neighbourhood" : "project";
        }

        if (direction is not ("in" or "out" or "both"))
        {
            error = $"unknown --direction '{direction}'. use: in, out or both";
            return false;
        }

        if (depth < 0)
        {
            error = "--depth must be zero or greater";
            return false;
        }

        if (level != "neighbourhood" && (opt.Flag("depth") || opt.Flag("direction")))
        {
            error = "--depth and --direction apply only to --level neighbourhood";
            return false;
        }

        if (level == "neighbourhood" && opt.Positional.Count == 0)
        {
            error = "--level neighbourhood needs a <symbol> argument";
            return false;
        }

        if (level != "neighbourhood" && opt.Positional.Count > 0)
        {
            error = $"a <symbol> argument selects --level neighbourhood; remove it or drop --level {level}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Streams the render through the budget. Overflow is not a silent truncation: the closing
    /// marker sets the exit code and names both ways to get the whole picture (ADR 0004).
    /// </summary>
    internal static int WriteStdout(Queries.ExportResult result, int budget, string level)
    {
        var exit = Write(result, budget, level, out var writer);
        writer.Flush();
        return exit;
    }

    /// <summary>
    /// The budget arithmetic, separated from <see cref="BudgetWriter.Flush"/> so a test can read the
    /// emitted lines without redirecting the console.
    /// </summary>
    internal static int Write(Queries.ExportResult result, int budget, string level, out BudgetWriter writer)
    {
        writer = new BudgetWriter(budget, BudgetWriter.CompletionMarkerReserve);

        foreach (var line in result.Lines)
        {
            if (!writer.Add(line)) break;
        }

        if (!writer.Overflowed) return Exit.Ok;

        var remedies = "--out <file> for the whole render, or a coarser --level";
        if (level == "neighbourhood") remedies += "; a smaller --depth also fits";
        writer.AddMarker(Queries.IncompleteMarker(writer, remedies));
        return Exit.OverBudget;
    }
}
