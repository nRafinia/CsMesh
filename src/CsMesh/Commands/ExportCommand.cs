using CsMesh.Analysis;
using CsMesh.Common;
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

        if (level != "project")
        {
            Console.Error.WriteLine($"--level {level} is not implemented");
            return Exit.Usage;
        }

        var graph = GraphStore.Load(root, out var problem);
        if (graph == null)
        {
            Console.Error.WriteLine($"no usable index ({problem}). run: csmesh index");
            return Exit.NoIndex;
        }

        var request = new Queries.ExportRequest(
            format, level, null, depth, direction,
            opt.Flag("include-tests"), opt.Flag("all-edges"));

        var result = Queries.RenderExport(graph, request);

        return WriteStdout(result, opt.Int("budget", DefaultBudget), level);
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
