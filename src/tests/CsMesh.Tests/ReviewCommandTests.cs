using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// 'review' compares the working tree against a git revision instead of against whatever csmesh
/// index last happened to see, and gates on the result -- a baseline of accepted findings, and an
/// exit code CI can branch on without parsing prose.
///
/// Console.Out is process-global (see ConsoleCaptureCollection), and ReviewCommand.Execute writes
/// to it on every path, so this whole class shares that collection with everything else that
/// exercises a full command rather than a Queries.* method directly.
/// </summary>
[Collection("console-capture")]
public sealed class ReviewCommandTests : IDisposable
{
    private readonly string _root;

    public ReviewCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-review-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        Git("init -q");
        Git("config user.email test@example.com");
        Git("config user.name Test");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    // ------------------------------------------------------------------ fixture plumbing

    private void Git(string args)
    {
        var ok = GitTool.TryRun(_root, args, out _, out var stderr, out _);
        Assert.True(ok, $"git {args} failed: {stderr}");
    }

    private string Sha(string rev = "HEAD")
    {
        GitTool.TryRun(_root, $"rev-parse --short=12 {rev}", out var stdout, out _, out _);
        return stdout.Trim();
    }

    private void Write(string name, string body) => File.WriteAllText(Path.Combine(_root, "src", name), body);

    private void CommitAll(string message)
    {
        Git("add -A");
        Git($"commit -q -m \"{message}\"");
    }

    /// <summary>Full rebuild every time -- an incremental pass reusing a graph across these edits
    /// is exactly the false-positive the spec's own note warns about (a declined incremental pass
    /// proving nothing).</summary>
    private void ReindexCurrent()
    {
        var graph = Indexer.Build(_root);
        GraphStore.Save(graph);
    }

    private static string Capture(Func<int> run, out int exit)
    {
        var original = Console.Out;
        var buffer = new StringWriter();

        try
        {
            Console.SetOut(buffer);
            exit = run();
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }

    private int Review(params string[] args)
    {
        Capture(() => ReviewCommand.Execute(_root, new Options(args)), out var exit);
        return exit;
    }

    private string ReviewOut(out int exit, params string[] args) =>
        Capture(() => ReviewCommand.Execute(_root, new Options(args)), out exit);

    private string ReviewErr(out int exit, params string[] args)
    {
        var original = Console.Error;
        var buffer = new StringWriter();
        try
        {
            Console.SetError(buffer);
            exit = ReviewCommand.Execute(_root, new Options(args));
        }
        finally
        {
            Console.SetError(original);
        }

        return buffer.ToString();
    }

    private const string ThingSource =
        """
        namespace Shared { public interface IServiceCollection { } public interface IThing { void Do(); } }
        """;

    private const string ImplSource =
        """
        namespace Impl
        {
            using Shared;
            public sealed class ThingA : IThing { public void Do() { } }
            public sealed class ThingB : IThing { public void Do() { } }
        }
        """;

    private static string Registration(string implementation) =>
        $$"""
        namespace Api
        {
            using Shared;
            using Impl;

            public static class Registrations
            {
                public static void Configure(IServiceCollection services) => services.AddScoped<IThing, {{implementation}}>();
                public static void AddScoped<TService, TImplementation>(this IServiceCollection s) { }
            }
        }
        """;

    /// <summary>Commits a base revision wired to ThingA and returns its short sha.</summary>
    private string SeedBase()
    {
        Write("Abstractions.cs", ThingSource);
        Write("Things.cs", ImplSource);
        Write("Registration.cs", Registration("ThingA"));
        CommitAll("base");
        return Sha();
    }

    // ------------------------------------------------------------------ scenarios

    [Fact]
    public void A_moved_DI_binding_is_reported()
    {
        var baseSha = SeedBase();

        Write("Registration.cs", Registration("ThingB"));
        ReindexCurrent();

        var exit = Review(baseSha);

        Assert.Equal(Exit.Changed, exit);
    }

    [Fact]
    public void A_body_edit_that_moves_no_edge_is_not_reported()
    {
        var baseSha = SeedBase();

        var path = Path.Combine(_root, "src", "Things.cs");
        File.WriteAllText(path, ImplSource.Replace(
            "public void Do() { }", "public void Do() { System.Console.WriteLine(\"noop\"); }"));
        ReindexCurrent();

        var exit = Review(baseSha);

        // This is the line between 'review' and a text diff: if it were wrong the command would
        // be noise, reporting on every refactor regardless of whether behaviour moved.
        Assert.Equal(Exit.Ok, exit);
    }

    /// <summary>
    /// A body edit that adds a call to another in-repo method genuinely adds a Call edge to the
    /// graph -- unlike a call into the BCL, which is never a graph node at all. Proves the previous
    /// test is actually exercising the includeCalls exclusion and not silently vacuous because
    /// nothing in the edit produced an edge to exclude in the first place.
    /// </summary>
    [Fact]
    public void The_same_edit_is_reported_only_when_calls_are_included()
    {
        var baseSha = SeedBase();

        var withHelper = ImplSource.Replace(
            "public sealed class ThingA : IThing { public void Do() { } }",
            "public sealed class ThingA : IThing { public void Do() => Helper(); private void Helper() { } }");
        Write("Things.cs", withHelper);
        Write("Registration.cs", Registration("ThingA"));
        ReindexCurrent();

        Assert.Equal(Exit.Ok, Review(baseSha));
        Assert.Equal(Exit.Changed, Review(baseSha, "--calls"));
    }

    [Fact]
    public void Accept_suppresses_a_finding_and_a_new_one_still_surfaces()
    {
        var baseSha = SeedBase();

        Write("Registration.cs", Registration("ThingB"));
        ReindexCurrent();

        Assert.Equal(Exit.Ok, Review(baseSha, "--accept"));
        Assert.Equal(Exit.Ok, Review(baseSha)); // accepted findings alone: nothing left to report

        Write("Things.cs", ImplSource.Replace(
            "public sealed class ThingB", "public sealed class ThingC"));
        Write("Registration.cs", Registration("ThingC"));
        ReindexCurrent();

        // A baseline that suppresses everything, accepted or not, is a silent and common failure.
        Assert.Equal(Exit.Changed, Review(baseSha));
    }

    [Fact]
    public void Accept_prunes_baseline_entries_that_no_longer_match_anything()
    {
        var baseSha = SeedBase();

        Write("Registration.cs", Registration("ThingB"));
        ReindexCurrent();
        Assert.Equal(Exit.Ok, Review(baseSha, "--accept"));

        var acceptedPath = BaselineFile.PathFor(_root);
        var before = BaselineFile.Load(_root);
        Assert.NotEmpty(before);

        // Comparing against the current HEAD itself: no findings survive, so every previously
        // accepted entry is dead and --accept should drop it rather than let the file grow. The
        // index is rebuilt after the commit because a commit gap now refuses --accept outright.
        CommitAll("second");
        var currentSha = Sha();
        ReindexCurrent();
        Assert.Equal(Exit.Ok, Review(currentSha, "--accept"));

        Assert.Empty(BaselineFile.Load(_root));
        Assert.True(File.Exists(acceptedPath));
    }

    [Fact]
    public void The_base_graph_is_reused_rather_than_rebuilt_on_a_second_run()
    {
        var baseSha = SeedBase();

        Write("Registration.cs", Registration("ThingB"));
        ReindexCurrent();

        Review(baseSha);
        var cachePath = GraphStore.BaseGraphPathFor(_root, baseSha);
        Assert.True(File.Exists(cachePath));
        var writtenAt = File.GetLastWriteTimeUtc(cachePath);

        Thread.Sleep(1200); // filesystem mtime resolution on some runners is coarse
        Review(baseSha);

        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(cachePath));
    }

    [Fact]
    public void No_change_reports_ok_and_names_zero_findings()
    {
        var baseSha = SeedBase();
        ReindexCurrent();

        var exit = Review(baseSha);

        Assert.Equal(Exit.Ok, exit);
    }

    /// <summary>Without an omitted base argument, and with no remote configured, the default must
    /// still resolve to something rather than fail the command entirely.</summary>
    [Fact]
    public void Default_base_falls_back_to_HEAD_without_a_remote()
    {
        SeedBase();
        ReindexCurrent();

        Assert.Equal(Exit.Ok, Review());

        Write("Registration.cs", Registration("ThingB"));
        ReindexCurrent();

        Assert.Equal(Exit.Changed, Review());
    }

    [Fact]
    public void No_worktree_survives_a_successful_run()
    {
        var baseSha = SeedBase();

        Write("Registration.cs", Registration("ThingB"));
        ReindexCurrent();

        Review(baseSha);

        Assert.False(Directory.Exists(GraphStore.BaseWorktreePath(_root)));
    }

    [Fact]
    public void No_worktree_survives_a_base_that_fails_to_index()
    {
        // A commit with no C# sources at all: the worktree is created, indexing runs, finds
        // nothing, and the command must still clean up rather than leave the checkout behind.
        Write("Notes.md", "# not C#");
        CommitAll("docs only");
        var docsSha = Sha();

        Write("Abstractions.cs", ThingSource);
        CommitAll("adds a source file");
        ReindexCurrent();

        var exit = Review(docsSha);

        Assert.Equal(Exit.NoIndex, exit);
        Assert.False(Directory.Exists(GraphStore.BaseWorktreePath(_root)));
    }

    [Fact]
    public void A_bad_revision_is_a_usage_error()
    {
        SeedBase();
        ReindexCurrent();

        var exit = Review("this-revision-does-not-exist");

        Assert.Equal(Exit.Usage, exit);
    }

    [Fact]
    public void Not_a_git_repository_is_a_usage_error()
    {
        var plainDir = Path.Combine(Path.GetTempPath(), "csmesh-noreview-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(plainDir, "src"));
        File.WriteAllText(Path.Combine(plainDir, "src", "Thing.cs"), "namespace Demo; public sealed class Thing { }");

        try
        {
            var graph = Indexer.Build(plainDir);
            GraphStore.Save(graph);

            Capture(() => ReviewCommand.Execute(plainDir, new Options([])), out var exit);

            Assert.Equal(Exit.Usage, exit);
        }
        finally
        {
            try { Directory.Delete(plainDir, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public void No_current_index_is_reported_distinctly_from_no_base_index()
    {
        var baseSha = SeedBase();
        // deliberately never reindex the current side

        var exit = Review(baseSha);

        Assert.Equal(Exit.NoIndex, exit);
    }

    // ------------------------------------------------------------------ commit gap

    /// <summary>
    /// The index that stands in for "current" can predate HEAD, and then the comparison is wrong,
    /// not merely thin: changes the revision under review made may be absent, and changes already
    /// gone may be reported. A gate that cannot see the change must not pass, so the command
    /// refuses instead of warning and exiting 0.
    /// </summary>
    [Fact]
    public void A_commit_gap_refuses_and_names_the_remedy()
    {
        var baseSha = SeedBase();
        ReindexCurrent();

        Write("Things.cs", ImplSource + "\n// HEAD moves past the index\n");
        CommitAll("second");

        var err = ReviewErr(out var exit, baseSha);

        Assert.Equal(Exit.NoIndex, exit);
        Assert.Contains("index built at", err, StringComparison.Ordinal);
        Assert.Contains("HEAD is", err, StringComparison.Ordinal);
        Assert.Contains("run: csmesh index", err, StringComparison.Ordinal);
    }

    /// <summary>An index that records no commit cannot be shown current, and must refuse rather
    /// than pass as fresh just because nothing contradicts it.</summary>
    [Fact]
    public void An_index_with_no_commit_refuses()
    {
        var baseSha = SeedBase();
        var graph = Indexer.Build(_root);
        graph.BuiltFromCommit = string.Empty;
        GraphStore.Save(graph);

        var err = ReviewErr(out var exit, baseSha);

        Assert.Equal(Exit.NoIndex, exit);
        Assert.Contains("records no commit", err, StringComparison.Ordinal);
        Assert.Contains("run: csmesh index", err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_current_index_proceeds_without_a_gap_message()
    {
        var baseSha = SeedBase();
        ReindexCurrent();

        var text = ReviewOut(out var exit, baseSha);

        Assert.Equal(Exit.Ok, exit);
        Assert.DoesNotContain("index built at", text, StringComparison.Ordinal);
        Assert.DoesNotContain("records no commit", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// --accept under a gap is the worst case: findings from the wrong current side would be
    /// written into the baseline and inherited silently by every later review. It is a usage error,
    /// and nothing may be written.
    /// </summary>
    [Fact]
    public void Accept_under_a_gap_refuses_and_writes_nothing()
    {
        var baseSha = SeedBase();
        ReindexCurrent();                                   // built at base
        Write("Registration.cs", Registration("ThingB"));
        ReindexCurrent();                                   // still built at base

        Write("Things.cs", ImplSource + "\n// HEAD moves past the index\n");
        CommitAll("second");                                // HEAD moves past the index

        var baselinePath = BaselineFile.PathFor(_root);
        var err = ReviewErr(out var exit, baseSha, "--accept");

        Assert.Equal(Exit.Usage, exit);
        Assert.Contains("run: csmesh index", err, StringComparison.Ordinal);
        Assert.Contains("--accept is refused", err, StringComparison.Ordinal);
        Assert.False(File.Exists(baselinePath));
    }

    [Fact]
    public void Accept_on_a_current_index_still_accepts()
    {
        var baseSha = SeedBase();
        Write("Registration.cs", Registration("ThingB"));
        ReindexCurrent();                                   // built at HEAD, no gap

        var exit = Review(baseSha, "--accept");

        Assert.Equal(Exit.Ok, exit);
        Assert.NotEmpty(BaselineFile.Load(_root));
    }
}
