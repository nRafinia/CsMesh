using CsMesh.Analysis;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Dispatch shapes beyond MediatR's. The failure mode throughout is silence: a message published
/// through a form the indexer does not read produces no edge and no unresolved entry, so a trace
/// looks complete while missing the fan-out entirely.
/// </summary>
public sealed class DispatchShapeTests : IDisposable
{
    private readonly string _root;

    public DispatchShapeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-dispatch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
    }

    private Graph Build(string source)
    {
        File.WriteAllText(Path.Combine(_root, "src", "Bus.cs"), source);
        var graph = Indexer.Build(_root);
        graph.Freeze();
        return graph;
    }

    private static bool Dispatches(Graph g, string from, string to)
    {
        var source = g.Nodes.FirstOrDefault(n => n.Short == from);
        var target = g.Nodes.FirstOrDefault(n => n.Short == to);
        if (source == null || target == null) return false;

        return g.Edges.Any(e => e.From == source.Id && e.To == target.Id && e.Kind == EdgeKind.Mediatr);
    }

    /// <summary>
    /// MassTransit's message-initializer form. The argument is an anonymous type, so reading the
    /// argument's type yields the anonymous type and the dispatch resolves to nothing -- silently,
    /// because an anonymous type is not declared in source and the source-only guard just returns.
    /// The generic argument names the message outright and has to win.
    /// </summary>
    [Fact]
    public void AGenericArgumentNamesTheMessageEvenWhenThePayloadIsAnonymous()
    {
        var graph = Build("""
            namespace Demo;

            public interface IConsumer<T> { }
            public interface IBus { void Publish<T>(object values); }

            public record OrderSubmitted(int Id);

            public class OrderSubmittedConsumer : IConsumer<OrderSubmitted>
            {
                public void Consume(OrderSubmitted message) { }
            }

            public class Submitter
            {
                private readonly IBus _bus = null!;
                public void Go(int id) => _bus.Publish<OrderSubmitted>(new { Id = id });
            }
            """);

        Assert.True(Dispatches(graph, "Submitter.Go", "OrderSubmittedConsumer.Consume"),
            "Publish<T> with an initializer payload produced no edge");
    }

    /// <summary>Wolverine's request-response verb. Same guard, so the name costs nothing.</summary>
    [Fact]
    public void InvokeAsyncIsRecognisedAsDispatch()
    {
        var graph = Build("""
            namespace Demo;

            public interface IRequestHandler<T> { }
            public interface IMessageBus { System.Threading.Tasks.Task InvokeAsync(object message); }

            public record ShipOrder(int Id);

            public class ShipOrderHandler : IRequestHandler<ShipOrder>
            {
                public void Handle(ShipOrder command) { }
            }

            public class Shipper
            {
                private readonly IMessageBus _bus = null!;
                public void Go() => _bus.InvokeAsync(new ShipOrder(1));
            }
            """);

        Assert.True(Dispatches(graph, "Shipper.Go", "ShipOrderHandler.Handle"),
            "InvokeAsync produced no edge");
    }

    /// <summary>
    /// The name alone was never the signal, and widening the name list must not change that.
    /// HttpClient.SendAsync carries an HttpRequestMessage, which is not declared here.
    /// </summary>
    [Fact]
    public void AnHttpSendIsStillNotADispatch()
    {
        var graph = Build("""
            namespace Demo;

            public class HttpRequestMessage { }
            public class HttpClient { public void SendAsync(HttpRequestMessage request) { } }

            public class Caller
            {
                private readonly HttpClient _http = null!;
                public void Go() => _http.SendAsync(new HttpRequestMessage());
            }
            """);

        Assert.DoesNotContain(graph.Edges, e => e.Kind == EdgeKind.Mediatr);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }
}

/// <summary>
/// A DbSet declaration is the only place a context and an entity are named together.
/// </summary>
public sealed class DbContextEntityTests : IDisposable
{
    private readonly string _root;
    private readonly Graph _graph;

    public DbContextEntityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-efcore-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        File.WriteAllText(Path.Combine(_root, "src", "Data.cs"), """
            namespace Demo;

            public class DbSet<T> { }
            public class DbContext { }

            public class Order { public int Id { get; set; } }
            public class Customer { public int Id { get; set; } }
            public class Unmapped { public int Id { get; set; } }

            public class ShopDbContext : DbContext
            {
                public DbSet<Order> Orders { get; set; } = null!;
                public DbSet<Customer> Customers { get; set; } = null!;
                public string ConnectionName { get; set; } = "";
            }
            """);

        _graph = Indexer.Build(_root);
        _graph.Freeze();
    }

    private bool HasEntityEdge(string context, string entity)
    {
        var from = _graph.Nodes.FirstOrDefault(n => n.Short == context);
        var to = _graph.Nodes.FirstOrDefault(n => n.Short == entity);
        if (from == null || to == null) return false;

        return _graph.Edges.Any(e => e.From == from.Id && e.To == to.Id && e.Note == "entity");
    }

    [Fact]
    public void EachDbSetLinksTheContextToItsEntity()
    {
        Assert.True(HasEntityEdge("ShopDbContext", "Order"));
        Assert.True(HasEntityEdge("ShopDbContext", "Customer"));
    }

    /// <summary>A type the context never exposes must not acquire an edge.</summary>
    [Fact]
    public void UnrelatedTypesGetNoEntityEdge()
    {
        Assert.False(HasEntityEdge("ShopDbContext", "Unmapped"));
    }

    /// <summary>An ordinary property is not an entity, or the edge means nothing.</summary>
    [Fact]
    public void NonDbSetPropertiesProduceNoEntityEdge()
    {
        Assert.DoesNotContain(_graph.Edges, e =>
            e.Note == "entity" &&
            _graph.ById(e.To)?.Short == "String");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }
}
