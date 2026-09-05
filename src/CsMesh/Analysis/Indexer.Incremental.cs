using CsMesh.Common;
using CsMesh.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CsMesh.Analysis;

/// <summary>
/// Rebinds only the files that moved, and leaves the rest of the graph where it is.
///
/// The tool was strongest in the middle of an agent session and weakest at both ends. This is the
/// far end: three edits in, every answer carries [STALE] and the only remedy on offer was a full
/// re-index of the solution. An agent will not pay that on turn five, so it goes back to grep, and
/// the token budget the index was protecting is spent anyway.
///
/// What made a partial rebuild impossible was node identity, not the passes. Ids were assigned
/// from the node count, so inserting one declaration renumbered every node after it. Persisting
/// <see cref="Node.Key"/> removes that: a symbol that survives an edit is recreated under its
/// original id, so every edge reaching it from an untouched file is still correct afterwards and
/// the untouched files never need to be looked at again.
///
/// Parsing is still whole-solution -- a semantic model for one file needs every other file's
/// declarations in the compilation, and Roslyn cannot serialise a syntax tree. Parsing is not the
/// expensive half. Binding is, and binding now happens for the edited files only.
/// </summary>
public static partial class Indexer
{
    /// <summary>
    /// Past this many edited files the saving is gone and the risk of a subtly wrong patch is not.
    /// </summary>
    public const int MaxIncrementalFiles = 40;

    /// <summary>
    /// Constructs whose meaning is decided somewhere other than the file they appear in.
    ///
    /// Pass 1 accumulates state from every file and passes 2 and 3 read it: which types implement
    /// which base, which handler consumes which request, which pairs the container bound. Restrict
    /// pass 1 to the edited files and parts of that state are only half there.
    ///
    /// The two dispatch tables are now persisted and seeded back, so an edited file that calls
    /// Send() still finds its handler. The rest are not persisted, and each entry below names a
    /// case they cannot cover:
    ///
    ///   - A handler declared in an edited file has to be matched against Send() sites in files
    ///     this pass will not rebind.
    ///   - A registration in an edited file binds implementations declared elsewhere.
    ///   - An interface or abstract class declared in an edited file has its implementor edges
    ///     removed along with it, and the implementors live in files that will not be rescanned,
    ///     so nothing puts those edges back.
    ///
    /// Rather than reason about which instances of these are safe, decline the whole class and run
    /// a full index. A slower correct answer beats a fast one that quietly drops an edge -- and
    /// this is a substring scan over a handful of files the caller just edited, so the check costs
    /// nothing worth measuring.
    /// </summary>
    private static readonly string[] CrossFileConstructs =
    {
        // Declares something other files bind against.
        "interface ", "abstract class", "abstract record",

        // Declares a handler that Send() sites elsewhere would have to be matched to.
        "IRequestHandler", "INotificationHandler", "ICommandHandler", "IQueryHandler",
        "IConsumer", "IHandleMessages", "IHostedService", "BackgroundService",

        // Registers implementations declared elsewhere.
        "AddScoped", "AddSingleton", "AddTransient", "AddHostedService",
        "AddKeyedScoped", "AddKeyedSingleton", "AddKeyedTransient",
        "TryAddScoped", "TryAddSingleton", "TryAddTransient", "TryAddEnumerable",
        "AddMediatR", "RegisterServicesFrom", "AddValidatorsFromAssembly", "AddAutoMapper",
        ".Scan("
    };

    /// <summary>
    /// Patches <paramref name="previous"/> in place and returns it, or null when the edit is one
    /// this path will not attempt. Null is not a failure: it means run a full index.
    /// </summary>
    public static Graph? BuildIncremental(Graph previous, IReadOnlyList<string> dirty, Action<string>? progress = null)
    {
        var root = previous.Root;

        if (dirty.Count == 0) return previous;

        if (dirty.Count > MaxIncrementalFiles)
        {
            Dbg.Log($"incremental declined: {dirty.Count} file(s) changed, cap is {MaxIncrementalFiles}");
            return null;
        }

        // A graph written before keys were persisted has no identity to recycle, so every node it
        // holds would be recreated under a fresh id and every edge would dangle.
        if (previous.Nodes.Count > 0 && previous.Nodes.All(n => n.Key.Length == 0))
        {
            Dbg.Log("incremental declined: graph predates stable node keys");
            return null;
        }

        foreach (var relative in dirty)
        {
            var full = Path.Combine(root, relative);
            if (!File.Exists(full)) continue;

            string text;
            try { text = File.ReadAllText(full); }
            catch (Exception ex) { Dbg.Log($"incremental declined: cannot read {relative} ({ex.Message})"); return null; }

            foreach (var marker in CrossFileConstructs)
            {
                if (!text.Contains(marker, StringComparison.Ordinal)) continue;
                Dbg.Log($"incremental declined: {relative} contains '{marker.Trim()}', which binds across files");
                return null;
            }
        }

        var scope = previous.IndexedAllProjects ? ProjectScope.Everything(root) : ProjectScope.Discover(root);
        var files = EnumerateSourceFiles(root, scope).ToList();
        progress?.Invoke($"incremental: parsing {files.Count} file(s), rebinding {dirty.Count}");

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = new SyntaxTree?[files.Count];
        var stamps = new FileStamp?[files.Count];

        // Parsing is embarrassingly parallel and is now the largest remaining cost, since binding
        // has been cut down to the edited files.
        Parallel.For(0, files.Count, i =>
        {
            var file = files[i];
            string text;
            try { text = File.ReadAllText(file); }
            catch { return; }

            trees[i] = CSharpSyntaxTree.ParseText(text, parseOptions, path: file);

            var info = new FileInfo(file);
            stamps[i] = new FileStamp
            {
                Path = Path.GetRelativePath(root, file),
                Ticks = info.LastWriteTimeUtc.Ticks,
                Size = info.Length
            };
        });

        var syntax = trees.Where(t => t != null).Select(t => t!).ToList();
        var freshStamps = stamps.Where(s => s != null).Select(s => s!).ToList();

        var globalUsings = GlobalUsingFiles(root).ToList();
        if (globalUsings.Count == 0) globalUsings.Add(ImplicitUsings);

        for (var i = 0; i < globalUsings.Count; i++)
        {
            syntax.Add(CSharpSyntaxTree.ParseText(globalUsings[i], parseOptions, path: $"<global-usings-{i}>"));
        }

        var references = ReferenceSet(root, scope, out _);

        var compilation = CSharpCompilation.Create(
            "csmesh.index",
            syntax,
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, allowUnsafe: true));

        var dirtySet = dirty
            .Select(d => d.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Retire the nodes declared in edited files, remembering their ids. Anything still declared
        // after the edit comes back under the same id; anything deleted or renamed does not, and
        // the sweep at the end removes the edges that pointed at it.
        var recycle = new Dictionary<string, int>(StringComparer.Ordinal);
        var survivors = new List<Node>(previous.Nodes.Count);

        foreach (var node in previous.Nodes)
        {
            var owned = node.File.Length > 0 && dirtySet.Contains(node.File.Replace('\\', '/'));
            if (!owned) { survivors.Add(node); continue; }
            if (node.Key.Length > 0) recycle[node.Key] = node.Id;
        }

        var retired = recycle.Values.ToHashSet();

        // Only outbound edges are dropped. An edge is written by the file its source lives in, so
        // a call from an untouched file into an edited one is not this pass's to re-derive -- and
        // re-deriving it would mean binding the untouched file, which is the cost being avoided.
        // Because ids are recycled, those inbound edges still point at the right symbol.
        previous.Nodes = survivors;
        previous.Edges.RemoveAll(e => retired.Contains(e.From));
        previous.InvalidateLookups();

        var builder = new Builder(previous, compilation, new ProjectLocator(root));
        builder.Recycle = recycle;
        builder.OnlyFiles = dirtySet;
        builder.Seed();

        builder.Pass1_Declarations(progress);
        builder.Pass2_Bodies(progress);
        builder.Pass3_Indirection(progress);

        var alive = previous.Nodes.Select(n => n.Id).ToHashSet();
        var dangling = previous.Edges.RemoveAll(e => !alive.Contains(e.From) || !alive.Contains(e.To));
        if (dangling > 0) Dbg.Log($"incremental: dropped {dangling} edge(s) into deleted symbols");

        builder.ExportDispatchTables();

        previous.Files = freshStamps;
        previous.Dirs = DirectoryStamps(root, files);
        previous.BuiltAt = DateTimeOffset.UtcNow;
        previous.BuiltFromCommit = RepositoryLocator.GitHead(root);
        previous.IncrementalRefreshes++;

        // Reference counts, unresolved totals and compiler diagnostics describe a whole-solution
        // bind. This pass bound a handful of files, so recomputing them would replace a real
        // number with a fragment of one. They stay as the last full index left them, and the
        // refresh counter is how 'doctor' knows which index they came from.
        previous.Freeze();
        return previous;
    }

    /// <summary>
    /// Directory write times, used as the cheap gate that decides whether a query has to walk the
    /// tree looking for files the index has never seen.
    /// </summary>
    private static List<DirStamp> DirectoryStamps(string root, IEnumerable<string> files)
    {
        var dirs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var dir = Path.GetDirectoryName(file);
            if (dir == null) continue;

            var relative = Path.GetRelativePath(root, dir);
            if (dirs.ContainsKey(relative)) continue;

            try { dirs[relative] = Directory.GetLastWriteTimeUtc(dir).Ticks; } catch { /* unreadable */ }
        }

        if (!dirs.ContainsKey("."))
        {
            try { dirs["."] = Directory.GetLastWriteTimeUtc(root).Ticks; } catch { /* unreadable */ }
        }

        return dirs.Select(kv => new DirStamp { Path = kv.Key, Ticks = kv.Value }).ToList();
    }
}