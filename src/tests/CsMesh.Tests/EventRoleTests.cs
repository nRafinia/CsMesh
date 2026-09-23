using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// An event declaration gets a node, and += / -= record Subscribe, never Write. An event
/// subscription attaches a handler; counting it as a write would put every subscriber under a
/// --writes query for the event, which is a different question.
/// </summary>
public sealed class EventRoleTests
{
    private const string Source = """
        namespace Demo;
        public class Publisher
        {
            public event System.EventHandler? Changed;
            private void H(object? s, System.EventArgs e) { }
            public void SubscribeBare() { Changed += H; }
            public void UnsubscribeBare() { Changed -= H; }
        }
        public class Subscriber
        {
            public void Subscribe(Publisher p) { p.Changed += OnChanged; }
            public void Unsubscribe(Publisher p) { p.Changed -= OnChanged; }
            private void OnChanged(object? s, System.EventArgs e) { }
        }
        public class Explicit
        {
            private System.EventHandler? _h;
            public event System.EventHandler? E { add { _h += value; } remove { _h -= value; } }
        }
        """;

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }
        public Graph Graph { get; }

        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "csmesh-event-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            File.WriteAllText(Path.Combine(Root, "src", "E.cs"), Source);
            Graph = Indexer.Build(Root);
        }

        public Node Event(string shortName) => Graph.Nodes.Single(n => n.Short == shortName);

        public Edge Edge(string ownerShort, Node target) =>
            Assert.Single(Graph.Edges, e =>
                e.Kind == EdgeKind.Call &&
                Graph.ById(e.From)?.Short == ownerShort &&
                e.To == target.Id);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public void Field_like_and_explicit_events_get_nodes()
    {
        using var sandbox = new Sandbox();
        Assert.Equal("event", sandbox.Event("Publisher.Changed").Kind);
        Assert.Equal("event", sandbox.Event("Explicit.E").Kind);
    }

    [Fact]
    public void Subscription_records_subscribe_on_member_access_and_bare_forms()
    {
        using var sandbox = new Sandbox();
        var changed = sandbox.Event("Publisher.Changed");

        Assert.Equal(EdgeRole.Subscribe, sandbox.Edge("Subscriber.Subscribe", changed).Role);
        Assert.Equal(EdgeRole.Subscribe, sandbox.Edge("Subscriber.Unsubscribe", changed).Role);
        Assert.Equal(EdgeRole.Subscribe, sandbox.Edge("Publisher.SubscribeBare", changed).Role);
    }

    [Fact]
    public void An_event_subscription_is_absent_from_writes()
    {
        using var sandbox = new Sandbox();
        var changed = sandbox.Event("Publisher.Changed");
        var writer = new BudgetWriter(4000);

        var exit = Queries.BlastRadius(sandbox.Graph, changed, 3, writer, [], writes: true);
        var text = string.Join("\n", writer.Lines);

        Assert.Equal(Exit.Ok, exit);
        Assert.DoesNotContain("Subscriber.Subscribe", text);
        Assert.DoesNotContain("Subscriber.Unsubscribe", text);
        Assert.DoesNotContain("Publisher.SubscribeBare", text);
    }
}
