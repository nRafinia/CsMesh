using System.Text.Json;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The index folder is created in whatever repository csmesh runs in, so it must be kept out of git
/// where it is created -- not by a separate <c>install</c> that <c>index</c> never runs. These pin
/// the write to <c>.git/info/exclude</c>: once, in the right directory, without ever failing the
/// command, and without touching stdout.
/// </summary>
[Collection("console-capture")]
public sealed class CsMeshDirTests : IDisposable
{
    private readonly string _root;

    public CsMeshDirTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-dir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    private static string EnsureNote(string root)
    {
        var original = Console.Error;
        var buffer = new StringWriter();
        try
        {
            Console.SetError(buffer);
            CsMeshDir.Ensure(root);
        }
        finally
        {
            Console.SetError(original);
        }

        return buffer.ToString();
    }

    private string Exclude => Path.Combine(_root, ".git", "info", "exclude");

    [Fact]
    public void A_plain_repo_gets_the_line_once()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        var first = EnsureNote(_root);
        Assert.Contains("added .csmesh/ to", first, StringComparison.Ordinal);
        Assert.True(File.Exists(Exclude));
        Assert.Single(File.ReadAllLines(Exclude), line => line.Trim() == ".csmesh/");

        var second = EnsureNote(_root);
        Assert.Equal("", second);
    }

    /// <summary>
    /// A linked worktree's <c>.git</c> is a file pointing at a per-worktree directory that names
    /// the shared common directory. The exclude belongs in the common one, where the clone reads it.
    /// </summary>
    [Fact]
    public void A_worktree_gitdir_writes_to_the_common_directory()
    {
        var common = Path.Combine(_root, "main", ".git");
        var worktreeGit = Path.Combine(common, "worktrees", "wt");
        Directory.CreateDirectory(worktreeGit);
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..");

        var worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGit}");

        var note = EnsureNote(worktree);

        var exclude = Path.Combine(common, "info", "exclude");
        Assert.True(File.Exists(exclude));
        Assert.Contains(".csmesh/", File.ReadAllText(exclude), StringComparison.Ordinal);
        Assert.Contains("added .csmesh/ to", note, StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_anchored_entry_is_left_untouched()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "info"));
        var before = "keep\n/.csmesh/\n";
        File.WriteAllText(Exclude, before);

        var note = EnsureNote(_root);

        Assert.Equal("", note);
        Assert.Equal(before, File.ReadAllText(Exclude));
    }

    [Fact]
    public void A_file_without_a_trailing_newline_gets_one_before_the_entry()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "info"));
        File.WriteAllText(Exclude, "# comment\nkeep");

        EnsureNote(_root);

        Assert.Equal("# comment\nkeep\n.csmesh/\n", File.ReadAllText(Exclude));
    }

    [Fact]
    public void The_existing_line_ending_style_is_kept()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "info"));
        File.WriteAllText(Exclude, "# comment\r\nkeep\r\n");

        EnsureNote(_root);

        Assert.Equal("# comment\r\nkeep\r\n.csmesh/\r\n", File.ReadAllText(Exclude));
    }

    [Fact]
    public void Without_a_git_directory_nothing_is_written()
    {
        var note = EnsureNote(_root);

        Assert.Equal("", note);
        Assert.False(Directory.Exists(Path.Combine(_root, ".git")));
    }

    /// <summary>
    /// A git directory the process cannot write to is not a reason for the command to fail. This is
    /// the one case where the feature must be invisible: the index still runs and still exits 0.
    /// </summary>
    [Fact]
    public void A_read_only_exclude_does_not_fail_the_index()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "info"));
        File.WriteAllText(Exclude, "keep\n");
        File.SetAttributes(Exclude, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(_root, "A.cs"), "public class A { }");

        try
        {
            var exit = IndexCommand.Execute(_root, new Options([]));
            Assert.Equal(Exit.Ok, exit);
        }
        finally
        {
            File.SetAttributes(Exclude, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Json_mode_keeps_stdout_to_a_single_frame()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, "A.cs"), "public class A { }");

        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            IndexCommand.Execute(_root, new Options(["--json"]));
        }
        finally
        {
            Console.SetOut(original);
        }

        // The note goes to stderr, so the JSON frame is all that reaches stdout; one stray WriteLine
        // would corrupt the stream the MCP transport multiplexes.
        var lines = buffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);

        using var document = JsonDocument.Parse(lines[0]);
        Assert.Equal("index", document.RootElement.GetProperty("command").GetString());
    }
}
