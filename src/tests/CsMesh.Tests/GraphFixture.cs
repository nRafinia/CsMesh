using CsMesh.Analysis;
using CsMesh.Models;

namespace CsMesh.Tests;

/// <summary>
/// A deliberately hostile miniature solution: two commands with the same class name in different
/// namespaces, three implementations of one interface with only two registered, every registration
/// form the indexer claims to understand, and a three-type dependency loop.
///
/// The sources are written to a temp directory rather than committed as .cs files, so indexing the
/// real repository never picks them up.
/// </summary>
public sealed class GraphFixture : IDisposable
{
    public string Root { get; }
    public Graph Graph { get; }

    public GraphFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "csmesh-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(Root, "src"));

        foreach (var (name, body) in Sources)
        {
            File.WriteAllText(Path.Combine(Root, "src", name), body);
        }

        Graph = Indexer.Build(Root);
        Graph.Freeze();
    }

    public Node Node(string fullName) =>
        Graph.Nodes.FirstOrDefault(n => n.Name == fullName)
        ?? throw new InvalidOperationException($"no node named '{fullName}'. Have: " +
                                               string.Join(", ", Graph.Nodes.Select(n => n.Name).Take(40)));

    public IEnumerable<Edge> EdgesOfKind(EdgeKind kind) => Graph.Edges.Where(e => e.Kind == kind);

    public string NameOf(int id) => Graph.ById(id)?.Name ?? "<missing>";

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* temp dir; best effort */ }
    }

    private static readonly (string Name, string Body)[] Sources =
    [
        ("Messages.cs",
         """
         namespace CompanyA.Commands { public sealed class CreateOrder { public int Id { get; set; } } }
         namespace CompanyB.Commands { public sealed class CreateOrder { public string Reference { get; set; } = ""; } }
         """),

        ("Abstractions.cs",
         """
         namespace Shared
         {
             public interface IRequestHandler<TRequest, TResponse> { }
             public interface IMediator { object Send(object request); }
             public interface IServiceCollection { }
             public interface IOrderStore { int Save(int id); }
             public interface IClock { }
         }
         """),

        ("Handlers.cs",
         """
         using Shared;

         namespace CompanyA.Handlers
         {
             using CompanyA.Commands;

             public sealed class CreateOrderHandler : IRequestHandler<CreateOrder, int>
             {
                 private readonly IOrderStore _store;
                 public CreateOrderHandler(IOrderStore store) => _store = store;
                 public int Handle(CreateOrder request) => _store.Save(request.Id);
             }
         }

         namespace CompanyB.Handlers
         {
             using CompanyB.Commands;

             public sealed class CreateOrderHandler : IRequestHandler<CreateOrder, string>
             {
                 public string Handle(CreateOrder request) => request.Reference;
             }
         }
         """),

        ("Stores.cs",
         """
         using Shared;

         namespace Infrastructure
         {
             public sealed class SqlOrderStore : IOrderStore { public int Save(int id) => id; }
             public sealed class CachedOrderStore : IOrderStore { public int Save(int id) => id; }
             public sealed class FakeOrderStore : IOrderStore { public int Save(int id) => 0; }
             public sealed class SystemClock : IClock { }
         }
         """),

        ("Api.cs",
         """
         using Shared;
         using CompanyA.Commands;

         namespace Api
         {
             public sealed class OrderController
             {
                 private readonly IMediator _mediator;
                 public OrderController(IMediator mediator) => _mediator = mediator;
                 public object Post() => _mediator.Send(new CreateOrder { Id = 1 });
             }
         }
         """),

        ("Registration.cs",
         """
         using Shared;
         using Infrastructure;

         namespace Api
         {
             public static class Registrations
             {
                 // The host calls only this; the real bindings live one frame deeper.
                 public static IServiceCollection AddApplication(this IServiceCollection services)
                 {
                     services.TryAddScoped<IOrderStore, SqlOrderStore>();
                     services.AddKeyedSingleton<IOrderStore, CachedOrderStore>("cache");
                     services.AddScoped<IClock>(sp => new SystemClock());
                     return services;
                 }

                 public static void TryAddScoped<TService, TImplementation>(this IServiceCollection s) { }
                 public static void AddKeyedSingleton<TService, TImplementation>(this IServiceCollection s, object key) { }
                 public static void AddScoped<TService>(this IServiceCollection s, System.Func<object, TService> factory) { }
             }

             public static class Host
             {
                 public static void Configure(IServiceCollection services) => services.AddApplication();
             }
         }
         """),

        ("Loop.cs",
         """
         namespace Loop
         {
             public class Alpha { public void Go(Beta b) => b.Run(); }
             public class Beta { public void Run() { new Gamma().Step(); } }
             public class Gamma { public void Step() { new Alpha().Go(new Beta()); } }
         }
         """)
    ];
}
