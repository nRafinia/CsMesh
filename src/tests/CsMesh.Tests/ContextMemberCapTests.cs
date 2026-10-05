using System.Text;
using System.Text.Json;
using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// context prints at most 20 members. Reaching 21 used to say nothing, so a reader saw 20 of 34 and
/// believed it was all of them. The cap is a count, not a budget, so the answer stays complete at
/// exit 0; the line it now prints names the withheld number, and --json carries it as a field so a
/// JSON consumer is not told less than the terminal was.
/// </summary>
[Collection("console-capture")]
public sealed class ContextMemberCapTests
{
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }

        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "csmesh-membercap-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(Root, "src"));

            var source = new StringBuilder("namespace Demo;\npublic sealed class Big\n{\n");
            for (var i = 1; i <= 25; i++) source.AppendLine($"    public int M{i:00}() => {i};");
            source.AppendLine("}");

            File.WriteAllText(Path.Combine(Root, "src", "Big.cs"), source.ToString());
            GraphStore.Save(Indexer.Build(Root));
        }

        public (int Exit, string Output) Run(params string[] args)
        {
            var original = Console.Out;
            var buffer = new StringWriter();
            try
            {
                Console.SetOut(buffer);
                return (QueryCommand.Execute(Root, new Options(args), "context"), buffer.ToString());
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public void A_type_over_the_cap_says_how_many_members_were_withheld()
    {
        using var sandbox = new Sandbox();

        var (exit, text) = sandbox.Run("Big");

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("... 5 more member(s)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_withheld_count_is_a_json_field()
    {
        using var sandbox = new Sandbox();

        var (exit, raw) = sandbox.Run("Big", "--json");

        Assert.Equal(Exit.Ok, exit);
        var result = JsonSerializer.Deserialize(raw, AppJsonContext.Default.QueryResult)!;
        Assert.Equal(5, result.WithheldMembers);
    }
}
