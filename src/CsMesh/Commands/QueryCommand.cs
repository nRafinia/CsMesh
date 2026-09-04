using System.Text.Json;
using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;

namespace CsMesh.Commands;

public static class QueryCommand
{
    public static int Execute(string root, Options opt, string kind)
    {
        var json = opt.Flag("json");
        var budget = opt.Int("budget", DefaultBudget(kind));
        var writer = new BudgetWriter(budget);
        var result = new QueryResult { Command = kind, Query = opt.Positional.FirstOrDefault() };

        var graph = GraphStore.Load(root, out var problem);
        if (graph == null)
        {
            var message = $"no usable index ({problem}). run: csmesh index";
            if (json) return EmitJson(result, writer, Exit.NoIndex, message);
            Console.Error.WriteLine(message);
            return Exit.NoIndex;
        }

        var dirty = GraphStore.DirtyFiles(graph);
        var dirtySet = dirty.ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.StaleFiles = dirty.Count;

        Telemetry.Telemetry.Current.Nodes = graph.Nodes.Count;
        Telemetry.Telemetry.Current.Edges = graph.Edges.Count;
        Dbg.Log($"index built {graph.BuiltAt:u} commit={graph.BuiltFromCommit} dirty={dirty.Count}");

        // Narrowing by subtree is what saves the budget on a large solution: an agent working in
        // one project should not pay for the other seven.
        var under = opt.Value("under");

        var depth = opt.Int("depth", kind switch
        {
            "blast" => 3,
            "context" => 3,
            "path" => 12,
            "diff" => 3,
            "silence" => 12,
            _ => 6
        });

        if (dirty.Count > 0)
        {
            var note = $"# index is {dirty.Count} file(s) behind working tree; rows from those files are marked [STALE]. run: csmesh index";
            result.Notes.Add(note);
            if (!json) writer.Force(note);
        }

        int exitCode;

        if (kind == "entrypoints")
        {
            exitCode = Queries.Entrypoints(graph, opt.Positional.FirstOrDefault(), under, writer, dirtySet);
        }
        else if (kind == "map")
        {
            exitCode = Queries.Map(graph, under, writer, dirtySet);
        }
        else if (kind == "silence")
        {
            if (opt.Positional.Count == 0)
            {
                var message = "usage: csmesh silence <symbol> [<target>]";
                if (json) return EmitJson(result, writer, Exit.Usage, message);
                Console.Error.WriteLine(message);
                return Exit.Usage;
            }

            var origin = Single(graph, opt.Positional[0], writer, result, json, dirtySet, out var originExit);
            if (origin == null) return originExit;

            Models.Node? destination = null;
            if (opt.Positional.Count > 1)
            {
                destination = Single(graph, opt.Positional[1], writer, result, json, dirtySet, out var destinationExit);
                if (destination == null) return destinationExit;
                result.Query = $"{opt.Positional[0]} -> {opt.Positional[1]}";
            }

            exitCode = Queries.Silence(graph, origin, destination, depth, writer, dirtySet);
        }
        else if (kind == "changes")
        {
            var previous = GraphStore.LoadPrevious(root);
            if (previous == null)
            {
                var message = "no previous index to compare against. Run 'csmesh index' twice, "
                              + "with your change in between.";
                if (json) return EmitJson(result, writer, Exit.NoIndex, message);
                Console.Error.WriteLine(message);
                return Exit.NoIndex;
            }

            exitCode = Queries.Changes(graph, previous, opt.Flag("calls"), writer, dirtySet);
        }
        else if (kind == "unresolved")
        {
            exitCode = Queries.Unresolved(graph, opt.Value("kind"), under, writer, dirtySet);
        }
        else if (kind == "diff")
        {
            // Default matches what a person means by "what did I just change": working tree
            // against the last commit. A ref or range as a positional argument overrides it.
            var range = opt.Positional.FirstOrDefault()
                        ?? (opt.Flag("staged") ? "--staged" : "HEAD");

            result.Query = range;
            exitCode = Queries.Diff(graph, root, range, depth, writer, dirtySet);
        }
        else if (kind == "cycles")
        {
            var scope = opt.Flag("project") ? "project"
                : opt.Flag("namespace") ? "namespace"
                : "type";
            exitCode = Queries.Cycles(graph, scope, under, writer, dirtySet);
        }
        else if (kind == "path")
        {
            if (opt.Positional.Count < 2)
            {
                var message = "usage: csmesh path <FromType.Member> <ToType.Member>";
                if (json) return EmitJson(result, writer, Exit.Usage, message);
                Console.Error.WriteLine(message);
                return Exit.Usage;
            }

            result.Query = $"{opt.Positional[0]} -> {opt.Positional[1]}";

            var origin = Single(graph, opt.Positional[0], writer, result, json, dirtySet, out var originExit);
            if (origin == null) return originExit;

            var destination = Single(graph, opt.Positional[1], writer, result, json, dirtySet, out var destinationExit);
            if (destination == null) return destinationExit;

            exitCode = Queries.Path(graph, origin, destination, depth, writer, dirtySet);
        }
        else
        {
            var query = opt.Positional.FirstOrDefault();
            if (query == null)
            {
                var message = $"usage: csmesh {kind} <symbol>";
                if (json) return EmitJson(result, writer, Exit.Usage, message);
                Console.Error.WriteLine(message);
                return Exit.Usage;
            }

            var candidates = graph.Resolve(query);
            if (candidates.Count == 0) return NotFound(graph, query, writer, result, json, dirtySet);

            var wanted = kind == "impl"
                ? candidates.Where(c => c.Kind is "interface" or "type").ToList()
                : candidates;

            if (wanted.Count == 0) wanted = candidates;
            if (wanted.Count > 1) return Ambiguous(query, wanted, writer, result, json, dirtySet);

            var node = wanted[0];
            exitCode = kind switch
            {
                // The suggestion on overflow is a command the caller can paste, not advice.
                "trace" => Queries.Trace(graph, node, depth, writer, dirtySet,
                                         $"csmesh trace {query} --budget {budget}"),
                "impl" => Queries.Impl(graph, node, writer, dirtySet),
                "blast" => Queries.BlastRadius(graph, node, depth, writer, dirtySet),
                "context" => Queries.Context(graph, node, depth, writer, dirtySet),
                _ => Exit.Usage
            };
        }

        Telemetry.Telemetry.Current.FilesReferenced = writer.DistinctFiles;

        if (json) return EmitJson(result, writer, exitCode, null);

        writer.Flush();
        Dbg.Log($"emitted {writer.Tokens} tokens (budget {budget}), exit {exitCode}");
        return exitCode;
    }

    private static int DefaultBudget(string kind) => kind switch
    {
        "impl" => 300,
        "blast" => 800,
        "context" => 800,
        "cycles" => 800,
        "unresolved" => 600,
        "changes" => 800,
        "diff" => 800,
        "silence" => 700,
        "map" => 700,
        "path" => 400,
        _ => 600
    };

    /// <summary>
    /// Resolves one query string to exactly one node, emitting the same not-found / ambiguous
    /// answers the single-symbol commands give. Returns null when the caller should stop, with the
    /// exit code already decided.
    /// </summary>
    private static Models.Node? Single(
        Models.Graph graph,
        string query,
        BudgetWriter writer,
        QueryResult result,
        bool json,
        HashSet<string> dirty,
        out int exitCode)
    {
        var candidates = graph.Resolve(query);
        if (candidates.Count == 0)
        {
            exitCode = NotFound(graph, query, writer, result, json, dirty);
            return null;
        }

        if (candidates.Count > 1)
        {
            exitCode = Ambiguous(query, candidates, writer, result, json, dirty);
            return null;
        }

        exitCode = Exit.Ok;
        return candidates[0];
    }

    private static int NotFound(
        Models.Graph graph,
        string query,
        BudgetWriter writer,
        QueryResult result,
        bool json,
        HashSet<string> dirty)
    {
        var leaf = query.Split('.').Last();

        // Before claiming the symbol does not exist, check whether it exists somewhere this
        // indexer does not read. "not found" and "not declared here" are different answers, and
        // only one of them means the caller mistyped something.
        var external = graph.ExternalTypes
            .FirstOrDefault(x => string.Equals(x.Name, leaf, StringComparison.OrdinalIgnoreCase));

        if (external != null)
        {
            writer.Force($"{external.Name} is not declared in this repository.");
            writer.Force($"It comes from {external.Assembly}; csmesh indexes source, not assemblies.");

            if (external.Sites.Count > 0)
            {
                writer.Force($"used at {external.Sites.Count} site(s):");
                foreach (var site in external.Sites)
                {
                    writer.Add($"  {site}");
                    result.Rows.Add(new QueryRow
                    {
                        Symbol = external.Name,
                        Kind = "external",
                        Relation = "usage",
                        Note = external.Assembly,
                        File = site.Split(':').First(),
                        Line = int.TryParse(site.Split(':').Last(), out var n) ? n : 0
                    });
                }
            }

            if (json) return EmitJson(result, writer, Exit.NotFound, null, keepRows: true);

            writer.Flush();
            return Exit.NotFound;
        }

        writer.Force($"not found: {query}");
        var near = graph.Nodes
            .Where(n => n.Short.Contains(leaf, StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();

        if (near.Count > 0)
        {
            writer.Force("did you mean: " + string.Join(", ", near.Select(n => n.Short)));
            foreach (var n in near)
            {
                result.Rows.Add(new QueryRow
                {
                    Symbol = n.Short,
                    Kind = n.Kind,
                    Relation = "suggestion",
                    File = n.File.Length > 0 ? n.File : null,
                    Line = n.Line,
                    Stale = n.File.Length > 0 && dirty.Contains(n.File)
                });
            }
        }

        if (json) return EmitJson(result, writer, Exit.NotFound, null, keepRows: true);

        writer.Flush();
        return Exit.NotFound;
    }

    private static int Ambiguous(
        string query,
        List<Models.Node> candidates,
        BudgetWriter writer,
        QueryResult result,
        bool json,
        HashSet<string> dirty)
    {
        writer.Force($"ambiguous: {candidates.Count} matches for '{query}'");

        foreach (var candidate in candidates.Take(12))
        {
            // Budget-guarded: a bare member name in a large solution can match hundreds of symbols.
            if (!writer.Add($"  {candidate.Name}  ({candidate.Kind})  {candidate.File}:{candidate.Line}")) break;

            result.Rows.Add(new QueryRow
            {
                Symbol = candidate.Short,
                Kind = candidate.Kind,
                Relation = "candidate",
                Note = candidate.Name,
                File = candidate.File.Length > 0 ? candidate.File : null,
                Line = candidate.Line,
                Stale = candidate.File.Length > 0 && dirty.Contains(candidate.File)
            });
        }

        writer.Force("re-run with a qualified Type.Member name.");

        if (json) return EmitJson(result, writer, Exit.Ambiguous, null, keepRows: true);

        writer.Flush();
        return Exit.Ambiguous;
    }

    private static int EmitJson(
        QueryResult result,
        BudgetWriter writer,
        int exitCode,
        string? note,
        bool keepRows = false)
    {
        if (note != null) result.Notes.Add(note);
        if (!keepRows) result.Rows.AddRange(writer.Rows);

        // Everything the terminal would have shown, including the parts that are prose rather
        // than rows. A caller that branches on Rows keeps working; one that needs the reasoning
        // no longer has to run the command twice without --json to get it.
        result.Text.AddRange(writer.Lines);

        result.Exit = exitCode;
        result.Truncated = writer.Overflowed;

        Console.WriteLine(JsonSerializer.Serialize(result, AppJsonContext.Default.QueryResult));
        Telemetry.Telemetry.Current.OutTokens = writer.Tokens;
        return exitCode;
    }
}
