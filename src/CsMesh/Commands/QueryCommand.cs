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
        var writer = WriterFor(kind, opt);
        var budget = writer.Budget;
        var result = new QueryResult { Command = kind, Query = opt.Positional.FirstOrDefault() };
        var hints = new List<string>();

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

        // Assembly-qualified keys make a name that repeats across projects return exit 3 instead of
        // a silently wrong single answer, so the caller needs a way to pick one. --project names
        // the project a candidate belongs to; the exit-3 listing prints the same value.
        var projectFilter = opt.Value("project");

        var depth = opt.Int("depth", kind switch
        {
            "blast" => 3,
            "context" => 3,
            "path" => 12,
            "diff" => 3,
            "silence" => 12,
            _ => 6
        });

        // Healing is the default and refusal is the opt-out: a read-only query that edits nothing
        // but the index is cheap next to the round trip that reports the index is behind, and an
        // agent that has to remember --heal is the one that ends up reading stale answers without
        // knowing it. --no-heal and CSMESH_AUTO_INDEX=0 leave the graph exactly as it is; --heal
        // and CSMESH_AUTO_INDEX=1 keep their old meaning and also mark the heal explicit, which is
        // the path where write contention still surfaces as exit 75.
        var healExplicit = opt.Flag("heal") || Environment.GetEnvironmentVariable("CSMESH_AUTO_INDEX") == "1";
        var healDisabled = opt.Flag("no-heal") || Environment.GetEnvironmentVariable("CSMESH_AUTO_INDEX") == "0";

        var healReason = (string?)null;
        var healBusy = false;

        if (dirty.Count > 0 && !healDisabled)
        {
            try
            {
                if (Indexer.BuildIncremental(graph, dirty, out var decline, message => Dbg.Log(message)) is { } healed)
                {
                    GraphStore.SaveInPlace(healed);
                    graph = healed;
                    dirty = GraphStore.DirtyFiles(graph);
                    dirtySet = dirty.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    result.StaleFiles = dirty.Count;
                    Dbg.Log($"healed index in place; {dirty.Count} file(s) still behind");
                }
                else
                {
                    healReason = decline;
                }
            }
            // A heal that loses the write race has still answered nothing wrong: the graph it was
            // going to patch is the one it now answers from, so it reports the delay and exits as
            // an unhealed answer would. An explicit --heal asked for the write itself, so
            // contention stays the exit-75 contract the write path already had.
            catch (LockContentedException) when (!healExplicit)
            {
                healBusy = true;
            }
        }

        if (dirty.Count > 0)
        {
            // Reserved, not droppable: a stale graph can hide a whole change, and no per-row [STALE]
            // tag can mark a row that was never built. See the commit that moved this off AddNote.
            var note = $"# index is {dirty.Count} file(s) behind working tree; rows from those files are marked [STALE]. run: csmesh index";
            result.Notes.Add(note);
            if (!json) writer.AddOpeningNote(note);

            // A second reserved note says why the default heal did not run. Without it the stale
            // note reads as the tool having tried and failed with no way to tell a held lock from
            // an edit the incremental path refuses; the remedies differ and only one is a full index.
            var skip = healBusy
                ? "# heal skipped: index busy; run: csmesh index"
                : healReason is null ? null : $"# heal skipped: {healReason}; run: csmesh index";

            if (skip is not null)
            {
                result.Notes.Add(skip);
                if (!json) writer.AddOpeningNote(skip);
            }
        }

        // A version gap is invisible in the rows themselves -- every line looks as confident as
        // any other -- so it has to be said out loud. The graph is still answered from, because
        // the old binary's answers are usually right and a hard refusal after every upgrade would
        // be worse than a warned one. It goes through the opening-note reserve rather than the
        // droppable note path: missing detections make the rows themselves untrustworthy, so this
        // note must not lose a budget contest with them.
        if (GraphStore.BuiltByOtherVersion(graph))
        {
            var note = $"# {GraphStore.VersionGap(graph)}; detections added since then are missing. run: csmesh index --full";
            result.Notes.Add(note);
            if (!json) writer.AddOpeningNote(note);
        }

        // The notes above are spent before the query starts -- the version-gap note out of its own
        // pool, the staleness note out of content -- so the query's real allowance is the cap minus
        // this. Recorded so two otherwise identical queries can be told apart by whether the index
        // happened to be stale, which the log previously could not do.
        CsMesh.Telemetry.Telemetry.Current.ReservedTokens = writer.Tokens;

        int exitCode;

        if (kind == "entrypoints")
        {
            exitCode = Queries.Entrypoints(graph, opt.Positional.FirstOrDefault(), under, writer, dirtySet);
        }
        else if (kind == "where")
        {
            if (opt.Positional.Count == 0)
            {
                var message = "usage: csmesh where <term> [<term> ...]";
                if (json) return EmitJson(result, writer, Exit.Usage, message);
                Console.Error.WriteLine(message);
                return Exit.Usage;
            }

            result.Query = string.Join(" ", opt.Positional);
            exitCode = Queries.Where(graph, opt.Positional.ToArray(), under, writer, dirtySet, opt.Flag("unranked"));
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

            // silence is the one command that, handed a selector naming no overload, lists the
            // overloads that do exist so the caller can pick one.
            var origin = Single(graph, opt.Positional[0], projectFilter, writer, result, json, dirtySet,
                                out var originExit, overloadsWhenNoMatch: true);
            if (origin == null) return originExit;

            Models.Node? destination = null;
            if (opt.Positional.Count > 1)
            {
                destination = Single(graph, opt.Positional[1], projectFilter, writer, result, json, dirtySet, out var destinationExit);
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

            var origin = Single(graph, opt.Positional[0], projectFilter, writer, result, json, dirtySet, out var originExit);
            if (origin == null) return originExit;

            var destination = Single(graph, opt.Positional[1], projectFilter, writer, result, json, dirtySet, out var destinationExit);
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

            // A selector names one overload of a member the name alone leaves ambiguous; a plain
            // name resolves exactly as before.
            var selection = SymbolSelector.Analyze(graph, query);
            if (selection.Status == SymbolSelector.SelectorStatus.SyntaxError)
                return SelectorUsage(result, writer, json, selection.Error!);

            var candidates = selection.Matches;
            if (candidates.Count == 0) return NotFound(graph, query, writer, result, json, dirtySet);

            var wanted = kind == "impl"
                ? candidates.Where(c => c.Kind is "interface" or "type").ToList()
                : candidates;

            if (wanted.Count == 0) wanted = candidates;

            if (ApplyProject(wanted, projectFilter) is not { } scoped)
            {
                return NoProjectMatch(query, projectFilter!, writer, result, json);
            }

            wanted = scoped;
            if (wanted.Count > 1) return Ambiguous(query, wanted, writer, result, json, dirtySet);

            var node = wanted[0];
            exitCode = kind switch
            {
                // The suggestion on overflow is a command the caller can paste, not advice.
                "trace" => Queries.Trace(graph, node, depth, writer, dirtySet,
                                         $"csmesh trace {query} --budget {budget}"),
                "impl" => Queries.Impl(graph, node, writer, dirtySet),
                "blast" => Queries.BlastRadius(graph, node, depth, writer, dirtySet, opt.Flag("writes"), hints),
                "context" => Queries.Context(graph, node, depth, writer, dirtySet),
                _ => Exit.Usage
            };
        }

        Telemetry.Telemetry.Current.FilesReferenced = writer.DistinctFiles;

        // A hint is text-mode prose or a JSON field, never both: printing it in text and leaving it
        // out of Text keeps the JSON consumer from reading the same sentence twice.
        if (hints.Count > 0)
        {
            result.Hint = hints[0];
            if (!json) writer.Force(hints[0]);
        }

        if (json) return EmitJson(result, writer, exitCode, null);

        writer.Flush();
        Dbg.Log($"emitted {writer.Tokens} tokens (budget {budget}), exit {exitCode}");
        return exitCode;
    }

    /// <summary>
    /// The budget a query runs under, resolved once and given to both the writer that enforces it
    /// and telemetry that records it. These used to be resolved separately -- the writer from the
    /// per-kind default, the log from a flat 600 in CliRunner -- so every command whose default is
    /// not 600 (impl 600, path 500, map 850, silence 300) logged a cap it was never held to.
    /// </summary>
    internal static BudgetWriter WriterFor(string kind, Options opt)
    {
        var budget = opt.Int("budget", DefaultBudget(kind));
        CsMesh.Telemetry.Telemetry.Current.Budget = budget;
        return new BudgetWriter(budget, BudgetWriter.CompletionMarkerReserve);
    }

    private static int DefaultBudget(string kind) => kind switch
    {
        "impl" => 600,
        "blast" => 800,
        "context" => 900,
        "cycles" => 800,
        "unresolved" => 700,
        "entrypoints" => 800,
        "changes" => 800,
        "diff" => 800,
        "silence" => 300,
        "map" => 850,
        "path" => 500,
        "where" => 600,
        _ => 600
    };

    /// <summary>
    /// Resolves one query string to exactly one node, emitting the same not-found / ambiguous
    /// answers the single-symbol commands give. Returns null when the caller should stop, with the
    /// exit code already decided.
    /// </summary>
    internal static Models.Node? Single(
        Models.Graph graph,
        string query,
        string? projectFilter,
        BudgetWriter writer,
        QueryResult result,
        bool json,
        HashSet<string> dirty,
        out int exitCode,
        bool overloadsWhenNoMatch = false)
    {
        var selection = SymbolSelector.Analyze(graph, query);
        if (selection.Status == SymbolSelector.SelectorStatus.SyntaxError)
        {
            exitCode = SelectorUsage(result, writer, json, selection.Error!);
            return null;
        }

        var candidates = selection.Matches;
        if (candidates.Count == 0)
        {
            if (overloadsWhenNoMatch && selection.Status == SymbolSelector.SelectorStatus.NoOverloadMatch)
            {
                Queries.Overloads(graph, selection.NamePart, selection.NameMatches, writer, dirty);
                if (json) exitCode = EmitJson(result, writer, Exit.NotFound, null);
                else { writer.Flush(); exitCode = Exit.NotFound; }
                return null;
            }

            exitCode = NotFound(graph, query, writer, result, json, dirty);
            return null;
        }

        if (ApplyProject(candidates, projectFilter) is not { } scoped)
        {
            exitCode = NoProjectMatch(query, projectFilter!, writer, result, json);
            return null;
        }

        if (scoped.Count > 1)
        {
            exitCode = Ambiguous(query, scoped, writer, result, json, dirty);
            return null;
        }

        exitCode = Exit.Ok;
        return scoped[0];
    }

    /// <summary>
    /// The candidates in one project, or null when the filter was given and nothing matched. A
    /// filter is matched against the project's root-relative path exactly or as a trailing segment,
    /// so "Api" and "src/Api" both select one project. Null is distinct from "no filter" so a
    /// mistyped project reports a miss rather than silently answering from every project.
    /// </summary>
    private static List<Models.Node>? ApplyProject(List<Models.Node> candidates, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return candidates;

        var matched = candidates.Where(c => ProjectMatches(c.Project, filter)).ToList();
        return matched.Count > 0 ? matched : null;
    }

    private static bool ProjectMatches(string project, string filter)
    {
        var normalized = filter.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0) return true;

        return project.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
               project.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static int NoProjectMatch(
        string query, string project, BudgetWriter writer, QueryResult result, bool json)
    {
        writer.Force($"no match for '{query}' in project '{project}'.");

        if (json) return EmitJson(result, writer, Exit.NotFound, null, keepRows: true);
        writer.Flush();
        return Exit.NotFound;
    }

    /// <summary>
    /// The exit-64 answer for a malformed selector, on the same path the other usage errors take:
    /// stderr in text mode, the envelope's note under --json.
    /// </summary>
    private static int SelectorUsage(QueryResult result, BudgetWriter writer, bool json, string message)
    {
        if (json) return EmitJson(result, writer, Exit.Usage, message);
        Console.Error.WriteLine(message);
        return Exit.Usage;
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
                    var line = int.TryParse(site.Split(':').Last(), out var n) ? n : 0;
                    writer.Add($"  {site}");
                    result.Rows.Add(new QueryRow
                    {
                        Symbol = external.Name,
                        Kind = "external",
                        Relation = "usage",
                        Note = external.Assembly,
                        File = site.Split(':').First(),
                        Line = line,
                        EndLine = line
                    });
                }
            }

            if (json) return EmitJson(result, writer, Exit.NotFound, null, keepRows: true);

            writer.Flush();
            return Exit.NotFound;
        }

        writer.Force($"not found: {query}");

        var verdict = OutOfGraph.Classify(query, graph);

        // Input that cannot be an identifier is answered before anything else. Token matching
        // always finds something, and a plausible-looking symbol suggestion in response to a
        // route or an env var is worse than silence: it reads as an answer.
        if (verdict is { Decisive: true })
        {
            writer.Force(verdict.Reason);
            result.Notes.Add(verdict.Reason);

            foreach (var next in verdict.Next)
            {
                writer.Force($"  next: {next}");
                result.Notes.Add($"next: {next}");
            }

            if (json) return EmitJson(result, writer, Exit.NotFound, null, keepRows: true);
            writer.Flush();
            return Exit.NotFound;
        }

        // Otherwise near misses come first. When the caller padded a real name --
        // DeleteCredentialAsync for DeleteAsync -- the correct answer is one line away.
        var near = SymbolSuggest.For(graph, query);

        if (near.Count > 0)
        {
            writer.Force("did you mean: " + string.Join(", ", near.Select(h => $"{h.Node.Short} [{h.Why}]")));
            foreach (var hit in near)
            {
                var n = hit.Node;
                result.Rows.Add(new QueryRow
                {
                    Symbol = n.Short,
                    Kind = n.Kind,
                    Relation = "suggestion",
                    Note = hit.Why,
                    File = n.File.Length > 0 ? n.File : null,
                    Line = n.Line,
                    EndLine = n.EndLine > n.Line ? n.EndLine : n.Line,
                    Stale = n.File.Length > 0 && dirty.Contains(n.File)
                });
            }
        }
        else if (verdict != null)
        {
            // Nothing in the graph looks like this, so the useful question is no longer "which
            // symbol did you mean" but "which tool answers this at all".
            writer.Force(verdict.Reason);
            result.Notes.Add(verdict.Reason);

            foreach (var next in verdict.Next)
            {
                writer.Force($"  next: {next}");
                result.Notes.Add($"next: {next}");
            }
        }
        else
        {
            writer.Force("Names, namespaces and route templates were all searched.");
            writer.Force("String literals, config values and non-.cs files are not in the graph.");
            writer.Force("For those, grep is the right tool. For a symbol you expected here: csmesh unresolved");
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

        // Selectors are computed over the whole candidate set, not the twelve that fit, so a
        // printed one stays unique against every candidate and not just the visible ones.
        var selectors = SymbolSelector.SelectorsFor(candidates);

        for (var i = 0; i < candidates.Count && i < 12; i++)
        {
            var candidate = candidates[i];
            var selector = selectors[i];

            // Budget-guarded: a bare member name in a large solution can match hundreds of symbols.
            // The project leads the location so a repeated name -- every linked type, every
            // top-level Program -- can be told apart and pasted into --project.
            var project = candidate.Project.Length > 0 ? $"{candidate.Project}  " : "";
            if (!writer.Add($"  {selector}  ({candidate.Kind})  {project}{candidate.File}:{Queries.LineSpan(candidate)}"))
                break;

            result.Rows.Add(new QueryRow
            {
                Symbol = candidate.Short,
                Kind = candidate.Kind,
                Relation = "candidate",
                Note = candidate.Name,
                Selector = selector,
                Project = candidate.Project.Length > 0 ? candidate.Project : null,
                File = candidate.File.Length > 0 ? candidate.File : null,
                Line = candidate.Line,
                EndLine = candidate.EndLine > candidate.Line ? candidate.EndLine : candidate.Line,
                Stale = candidate.File.Length > 0 && dirty.Contains(candidate.File)
            });
        }

        // Every candidate one member's overloads -- same name, same project -- means --project
        // cannot separate them, so the footer points at a selector instead.
        var sameName = candidates.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() == 1;
        var sameProject = candidates.Select(c => c.Project).Distinct(StringComparer.Ordinal).Count() == 1;
        writer.Force(sameName && sameProject && SymbolSelector.HasList(selectors[0])
            ? $"pick one with its selector, quoted: \"{selectors[0]}\""
            : "pick one with --project <name> (shown above), or re-run with a qualified Type.Member name.");

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
        Telemetry.Telemetry.Current.WouldBeTokens = writer.WouldBeTokens;
        return exitCode;
    }
}