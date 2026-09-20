using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The staleness note is the whole signal for a change the index never saw: a dirty file can move a
/// binding entirely, and no per-row [STALE] tag can mark a row that was never built. It therefore
/// goes through the opening-note reserve rather than the droppable path it shared with the
/// version-gap note -- a note about an invisible change must not lose a budget contest with visible
/// rows that are themselves incomplete.
///
/// These run the whole command so they exercise the QueryCommand path, not just the writer.
/// </summary>
[Collection("console-capture")]
public sealed class StalenessNoteTests : IDisposable
{
    private readonly string _root;

    public StalenessNoteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-stalenote-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        Write("Abstractions.cs",
            "namespace Shared { public interface IServiceCollection { } public interface IThing { void Do(); } }");
        Write("Things.cs",
            """
            namespace Impl
            {
                using Shared;
                public sealed class ThingA : IThing { public void Do() { } }
                public sealed class ThingB : IThing { public void Do() { } }
            }
            """);
        WriteRegistration("ThingA");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    private void Write(string name, string body) => File.WriteAllText(Path.Combine(_root, "src", name), body);

    private void WriteRegistration(string implementation) => Write("Registration.cs",
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
        """);

    /// <summary>Full index plus Save, which rotates the current graph into the previous slot.</summary>
    private void IndexAndSave() => GraphStore.Save(Indexer.Build(_root));

    private string MarkWorkingTreeDirty()
    {
        var path = Path.Combine(_root, "src", "Things.cs");
        File.AppendAllText(path, "\n// edited after the last index; its edges are invisible until re-indexed\n");
        return path;
    }

    private string Run(string kind, out int exit, params string[] args)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            exit = QueryCommand.Execute(_root, new Options(args), kind);
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }

    [Fact]
    public void A_starved_budget_keeps_the_staleness_note_and_the_marker()
    {
        IndexAndSave();                 // previous
        WriteRegistration("ThingB");
        IndexAndSave();                 // current: a visible DiBinding move for 'changes' to report
        MarkWorkingTreeDirty();

        var text = Run("changes", out _, "--budget", "60");

        Assert.Contains("behind working tree", text, StringComparison.Ordinal);
        Assert.Contains("INCOMPLETE", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_index_emits_no_staleness_note()
    {
        IndexAndSave();

        var text = Run("map", out _, "--budget", "600");

        Assert.DoesNotContain("behind working tree", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case no tag can cover. Two indexes of the same shape, then a structural edit that was
    /// never indexed: 'changes' sees nothing to report, and the staleness note is the only thing
    /// that says a real change is sitting just outside the graph.
    /// </summary>
    [Fact]
    public void A_change_that_never_got_indexed_produces_no_row_and_the_note_carries_it()
    {
        IndexAndSave();
        IndexAndSave();          // previous and current are structurally identical
        WriteRegistration("ThingB");  // a moved binding -- structural, and never indexed
        // The binding move alone is the same byte length and can land inside the mtime tolerance,
        // so lengthen the file too: the point is a real edit the index has not seen.
        File.AppendAllText(Path.Combine(_root, "src", "Registration.cs"), "\n// never indexed\n");

        var text = Run("changes", out var exit);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("no structural change", text, StringComparison.Ordinal);
        Assert.Contains("behind working tree", text, StringComparison.Ordinal);
        Assert.DoesNotContain("edge(s) removed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DiBinding", text, StringComparison.Ordinal);
    }
}
