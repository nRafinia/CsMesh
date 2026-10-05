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
/// CALLED BY answers "who calls this type", and a consumer of the type's methods is exactly who that
/// is. Seeding only the type node showed the constructors while the callers of the members -- the
/// code a change to the type actually breaks -- were missing. The set is blast-radius at depth 1:
/// every member is a seed, the target's own wiring is not a consumer, and a caller that reaches two
/// members is one row.
///
/// Like MEMBERS, the section is capped at a fixed count rather than priced against the budget. A type
/// called from hundreds of places must not starve CALLS, IMPLEMENTATION, IMPACT and FILES, and the
/// cap is a sample, not an overflow: the answer stays complete at exit 0 and names what it withheld.
/// </summary>
[Collection("console-capture")]
public sealed class ContextCalledByTests : IDisposable
{
    private readonly string _root;
    private readonly Graph _graph;

    public ContextCalledByTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-calledby-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        var source = new StringBuilder("""
            namespace Demo;

            public sealed class Target
            {
                public int First() => 1;
                public int Second() => 2;

                // A member calling another member is the type's own wiring, not a consumer.
                public int UsesFirst() => First();
            }

            public sealed class External
            {
                // Two members of Target, one caller: it must appear once, not twice.
                public int Run(Target target) => target.First() + target.Second();
            }

            // An interface is the type shape whose 'context' carries a CALLS section: the Interface
            // edge to its implementation is outgoing, where a class node only uses its members.
            public interface IBig { int Ping(); }

            public sealed class Big : IBig { public int Ping() => 1; }

            public sealed class BigCallers
            {
            """);

        // More callers than the cap, each of them through the interface reference so the edges land
        // on IBig.Ping rather than on the implementation's copy.
        for (var i = 0; i < 30; i++) source.AppendLine($"    public static int Call{i:00}(IBig b) => b.Ping();");
        source.AppendLine("}");

        File.WriteAllText(Path.Combine(_root, "src", "Types.cs"), source.ToString());
        _graph = Indexer.Build(_root);
        GraphStore.Save(_graph);
    }

    [Fact]
    public void A_caller_of_two_members_appears_once_and_a_self_call_does_not()
    {
        var target = _graph.Nodes.Single(n => n.Name == "Demo.Target");
        var external = _graph.Nodes.Single(n => n.Name == "Demo.External.Run");
        var self = _graph.Nodes.Single(n => n.Name == "Demo.Target.UsesFirst");

        var callers = Queries.DirectCallerNodes(_graph, target);

        Assert.Single(callers, c => c.Key == external.Key);
        Assert.DoesNotContain(callers, c => c.Key == self.Key);
    }

    [Fact]
    public void The_caller_cap_keeps_the_answer_complete_and_names_the_rest()
    {
        var raw = Run("IBig", "--json");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var text = string.Join("\n", root.GetProperty("text").EnumerateArray().Select(e => e.GetString()));

        // Hitting the cap is not an overflow: the answer is complete, the sections behind CALLED BY
        // survive, and no INCOMPLETE line is written.
        Assert.Equal(Exit.Ok, root.GetProperty("exit").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.DoesNotContain("INCOMPLETE", text, StringComparison.Ordinal);

        Assert.Contains("CALLS", text, StringComparison.Ordinal);
        Assert.Contains("FILES", text, StringComparison.Ordinal);

        var callerRows = root.GetProperty("rows").EnumerateArray()
            .Count(r => r.GetProperty("relation").GetString() == "caller");
        Assert.Equal(12, callerRows);

        Assert.Equal(18, root.GetProperty("withheld_callers").GetInt32());
        Assert.Contains("... 18 more caller(s): csmesh blast-radius IBig --depth 1", text, StringComparison.Ordinal);
    }

    private string Run(params string[] args)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            QueryCommand.Execute(_root, new Options(args), "context");
            return buffer.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir; best effort */ }
    }
}
