using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A read-only query heals changed files by default, so an agent that edits and then asks never
/// reads a stale answer it has no reason to distrust. Explicit healing used to be the only way to
/// get a current graph, and the transcript was 383 stale answers and no heals: the flag existed,
/// the skill mentioned it, and nobody passed it. Refusal is now the opt-out.
/// </summary>
[Collection("console-capture")]
public sealed class DefaultHealTests
{
    internal static string Run(string root, string kind, out int exit, params string[] args)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            exit = QueryCommand.Execute(root, new Options(args), kind);
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Nothing in this source is a cross-file construct, so the incremental pass can actually take
    /// it: an interface, an abstract class or a registration would decline the heal and this class
    /// of test would pass without a heal ever running.
    /// </summary>
    internal const string SimpleSource = """
        namespace Demo;
        public sealed class Thing { public void Go() { } }
        public sealed class Caller { public void Run(Thing t) => t.Go(); }
        """;

    [Fact]
    public void A_default_query_heals_changed_files_before_answering()
    {
        using var box = new HealSandbox();
        box.Edit("\n// edited after the index; the incremental pass must rebind this file\n");

        var text = Run(box.Root, "trace", out var exit, "Demo.Caller.Run");

        Assert.Equal(Exit.Ok, exit);
        Assert.DoesNotContain("[STALE]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("behind working tree", text, StringComparison.Ordinal);

        var onDisk = GraphStore.Load(box.Root, out var problem);
        Assert.Null(problem);
        Assert.NotNull(onDisk);
        Assert.Empty(GraphStore.DirtyFiles(onDisk!));
    }

    /// <summary>
    /// --no-heal is the opt-out: the graph on disk is left exactly as it was, and the answer says
    /// the rows it serves may be behind.
    /// </summary>
    [Fact]
    public void No_heal_answers_from_the_stale_graph_and_leaves_it_alone()
    {
        using var box = new HealSandbox();
        box.Edit("\n// edited after the index\n");

        var text = Run(box.Root, "trace", out _, "Demo.Caller.Run", "--no-heal");

        Assert.Contains("[STALE]", text, StringComparison.Ordinal);
        Assert.Contains("behind working tree", text, StringComparison.Ordinal);
        Assert.DoesNotContain("heal skipped", text, StringComparison.Ordinal);

        var onDisk = GraphStore.Load(box.Root, out _)!;
        Assert.NotEmpty(GraphStore.DirtyFiles(onDisk));
    }

    /// <summary>
    /// A heal the incremental pass refuses is not a failure the answer hides: the stale note says
    /// the rows may be behind, and a second reserved note says the heal was skipped and why, so the
    /// caller knows a full <c>csmesh index</c> is the remedy rather than a retry.
    /// </summary>
    [Fact]
    public void A_declined_heal_answers_stale_and_names_the_reason()
    {
        using var box = new HealSandbox();
        box.Edit("\npublic interface IExtra { }\n");

        var text = Run(box.Root, "trace", out _, "Demo.Caller.Run");

        Assert.Contains("behind working tree", text, StringComparison.Ordinal);
        Assert.Contains("heal skipped: a changed file binds across files", text, StringComparison.Ordinal);
        Assert.Contains("run: csmesh index", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A default heal that loses the write race has answered nothing wrong -- the graph it was
    /// going to patch is the one it answers from -- so it reports the skip and exits as an unhealed
    /// answer would. Exit 75 is reserved for the explicit form below.
    ///
    /// The graph is pinned with a handle that permits reads but not delete: csmesh's own reader
    /// asks for FileShare.Delete, so this stands in for an editor, backup agent or scanner that
    /// does not, which is the ordinary Windows way a replace fails.
    /// </summary>
    [Fact]
    public void A_default_heal_under_contention_answers_stale_with_the_busy_note()
    {
        if (!OperatingSystem.IsWindows()) return; // rename() does not consult open handles on Unix

        using var box = new HealSandbox();
        box.Edit("\n// edited while another handle holds the graph\n");

        string text;
        int exit;

        using (new FileStream(GraphStore.PathFor(box.Root), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            text = Run(box.Root, "trace", out exit, "Demo.Caller.Run");
        }

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("behind working tree", text, StringComparison.Ordinal);
        Assert.Contains("heal skipped: index busy", text, StringComparison.Ordinal);
        Assert.Contains("[STALE]", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// --heal asks for the write itself, so contention keeps the meaning exit 75 always had for it:
    /// the write did not happen, nothing is broken, retry shortly. Only the implicit heal is
    /// allowed to downgrade contention to a note.
    /// </summary>
    [Fact]
    public void An_explicit_heal_under_contention_exits_75()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var box = new HealSandbox();
        box.Edit("\n// edited while another handle holds the graph\n");

        int exit;
        var original = Console.Error;
        try
        {
            Console.SetError(new StringWriter());
            using (new FileStream(GraphStore.PathFor(box.Root), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                exit = CliRunner.RunGuarded(["trace"],
                    _ => QueryCommand.Execute(box.Root, new Options(["Demo.Caller.Run", "--heal"]), "trace"));
            }
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(Exit.Contended, exit);
    }

    /// <summary>
    /// The implicit heal used to wait out the lock (5 s) and then write unsynchronised beside the
    /// holder, so a query in one terminal could clobber an index in another. It now takes the lock
    /// only if it is free: a held lock means no write, and the answer falls back to the stale graph
    /// with the busy note, still exit 0 because nothing is wrong with the query.
    ///
    /// Pinned with a handle held across the whole query -- the lock file, not graph.json, so this
    /// exercises acquisition and not the rename.
    /// </summary>
    [Fact]
    public void A_default_heal_with_the_lock_held_answers_stale_without_writing()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare.None is only a lock on Windows

        using var box = new HealSandbox();
        box.Edit("\n// edited while another process holds the write lock\n");

        var graphPath = GraphStore.PathFor(box.Root);
        var originalBytes = File.ReadAllBytes(graphPath);

        string text;
        int exit;
        using (new FileStream(Path.Combine(GraphStore.DirFor(box.Root), "lock"),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            text = Run(box.Root, "trace", out exit, "Demo.Caller.Run");
        }

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("heal skipped: index busy", text, StringComparison.Ordinal);
        Assert.Contains("[STALE]", text, StringComparison.Ordinal);
        Assert.Equal(originalBytes, File.ReadAllBytes(graphPath));
    }

    /// <summary>
    /// --heal asked for the write, so it keeps waiting for the lock. When the wait runs out it now
    /// raises the same contention exception the rename path does, so the acquisition timeout is
    /// exit 75 too -- the contract the flag always promised but the storage layer only honoured on
    /// a rename.
    /// </summary>
    [Fact]
    public void An_explicit_heal_with_the_lock_held_exits_75()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var box = new HealSandbox();
        box.Edit("\n// edited while another process holds the write lock\n");

        int exit;
        var original = Console.Error;
        try
        {
            Console.SetError(new StringWriter());
            using (new FileStream(Path.Combine(GraphStore.DirFor(box.Root), "lock"),
                       FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                exit = CliRunner.RunGuarded(["trace"],
                    _ => QueryCommand.Execute(box.Root, new Options(["Demo.Caller.Run", "--heal"]), "trace"));
            }
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(Exit.Contended, exit);
    }
}

/// <summary>
/// CSMESH_AUTO_INDEX=0 turns the default heal off, the environment-variable twin of --no-heal. It
/// lives in the env-mutation collection because the variable is process-global.
/// </summary>
[Collection("env-mutation")]
public sealed class DefaultHealEnvTests
{
    [Fact]
    public void Auto_index_zero_disables_the_default_heal()
    {
        var previous = Environment.GetEnvironmentVariable("CSMESH_AUTO_INDEX");
        try
        {
            Environment.SetEnvironmentVariable("CSMESH_AUTO_INDEX", "0");

            using var box = new HealSandbox();
            box.Edit("\n// edited after the index\n");

            var text = DefaultHealTests.Run(box.Root, "trace", out _, "Demo.Caller.Run");

            Assert.Contains("[STALE]", text, StringComparison.Ordinal);
            Assert.Contains("behind working tree", text, StringComparison.Ordinal);

            var onDisk = GraphStore.Load(box.Root, out _)!;
            Assert.NotEmpty(GraphStore.DirtyFiles(onDisk));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CSMESH_AUTO_INDEX", previous);
        }
    }
}

/// <summary>
/// An indexed throwaway tree that can be edited after the index, so DirtyFiles has something to
/// report and the default heal has something to do.
/// </summary>
internal sealed class HealSandbox : IDisposable
{
    public string Root { get; }

    public HealSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "csmesh-heal-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(Root, "src"));
        File.WriteAllText(Path.Combine(Root, "src", "Thing.cs"), DefaultHealTests.SimpleSource);
        GraphStore.Save(Indexer.Build(Root));
    }

    public void Edit(string append) => File.AppendAllText(Path.Combine(Root, "src", "Thing.cs"), append);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
    }
}
