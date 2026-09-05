using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Two failures that shared one victim: the question "which class does the container actually
/// return for this interface".
///
/// The first was a refusal. sp =&gt; sp.GetRequiredService&lt;T&gt;() was treated as an alias to
/// another registration rather than a binding, so a codebase that registers each concrete type
/// once and exposes it through several interfaces produced almost no bindings at all.
///
/// The second was a ranking key that never matched. An implementation arrives on two edges -- the
/// DiBinding from where it was registered and the Interface from where it was declared -- and the
/// sort looked for a marker only the second one carries, while deduplication kept only the first.
/// A registered class led the list when its score tied with everything else and insertion order
/// happened to favour it. Register at anything under full confidence and it sorted below classes
/// nobody had registered.
/// </summary>
public sealed class DiRankingTests : IDisposable
{
    private readonly List<string> _temp = [];

    private Graph Index(string body)
    {
        var root = Path.Combine(Path.GetTempPath(), "csmesh-rank-" + Guid.NewGuid().ToString("N")[..8]);
        _temp.Add(root);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Program.cs"), body + Container);
        return Indexer.Build(root);
    }

    private const string Container =
        """

        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IServiceProvider2 { }
            public interface IServiceCollection { }
            public static class Ext
            {
                public static IServiceCollection AddScoped<T>(this IServiceCollection s) => s;
                public static IServiceCollection AddSingleton<T>(this IServiceCollection s) => s;
                public static IServiceCollection AddSingleton<T>(this IServiceCollection s, System.Func<System.IServiceProvider, T> f) => s;
                public static IServiceCollection AddSingleton<TService, TImpl>(this IServiceCollection s) => s;
            }
            public static class Provider
            {
                public static T GetRequiredService<T>(this System.IServiceProvider p) => default!;
            }
        }
        """;

    private static List<string> ImplOrder(Graph g, string interfaceName)
    {
        var target = g.Nodes.Single(n => n.Short == interfaceName);
        var w = new BudgetWriter(2000);

        Assert.Equal(Exit.Ok, Queries.Impl(g, target, w, []));

        return w.Lines
            .Skip(1)
            .Select(l => l.Trim().Split("  ")[0])
            .Where(l => l.Length > 0)
            .ToList();
    }

    [Fact]
    public void An_alias_registration_produces_a_binding()
    {
        var g = Index(
            """
            using Microsoft.Extensions.DependencyInjection;

            public interface ITenantContext { }
            public sealed class TenantContextAccessor : ITenantContext { }

            public static class Wiring
            {
                public static void Register(IServiceCollection s)
                {
                    s.AddScoped<TenantContextAccessor>();
                    s.AddSingleton<ITenantContext>(sp => sp.GetRequiredService<TenantContextAccessor>());
                }
            }
            """);

        var edge = Assert.Single(g.Edges, e => e.Kind == EdgeKind.DiBinding);
        Assert.Equal("factory-alias", edge.Source);

        // Below full confidence -- the lambda could branch -- but above the threshold that lets it
        // outrank an unregistered implementor, which is the entire purpose of drawing it.
        Assert.True(edge.Score < 1.0);
        Assert.True(edge.Score >= Edge.TrustThreshold);
    }

    [Fact]
    public void The_registered_class_leads_even_when_the_binding_is_not_certain()
    {
        var g = Index(
            """
            using Microsoft.Extensions.DependencyInjection;

            public interface ISecretStore { }
            public sealed class LocalSecretStore : ISecretStore { }
            public sealed class AzureSecretStore : ISecretStore { }
            public sealed class FakeSecretStore : ISecretStore { }

            public static class Wiring
            {
                public static void Register(IServiceCollection s)
                {
                    s.AddScoped<LocalSecretStore>();
                    s.AddSingleton<ISecretStore>(sp => sp.GetRequiredService<LocalSecretStore>());
                }
            }
            """);

        // Alphabetically Local comes last of the three. Only the registration puts it first.
        Assert.Equal("LocalSecretStore", ImplOrder(g, "ISecretStore")[0]);
    }

    [Fact]
    public void A_two_argument_registration_still_leads()
    {
        var g = Index(
            """
            using Microsoft.Extensions.DependencyInjection;

            public interface IClock { }
            public sealed class SystemClock : IClock { }
            public sealed class AtomicClock : IClock { }

            public static class Wiring
            {
                public static void Register(IServiceCollection s) => s.AddSingleton<IClock, SystemClock>();
            }
            """);

        Assert.Equal("SystemClock", ImplOrder(g, "IClock")[0]);
    }

    [Fact]
    public void A_direct_construction_is_still_read_as_a_binding()
    {
        var g = Index(
            """
            using Microsoft.Extensions.DependencyInjection;

            public interface IIdGen { }
            public sealed class GuidIdGen : IIdGen { }

            public static class Wiring
            {
                public static void Register(IServiceCollection s) => s.AddSingleton<IIdGen>(sp => new GuidIdGen());
            }
            """);

        var edge = Assert.Single(g.Edges, e => e.Kind == EdgeKind.DiBinding);
        Assert.Equal("factory-lambda", edge.Source);
    }

    [Fact]
    public void An_implementation_is_listed_once_not_once_per_edge()
    {
        var g = Index(
            """
            using Microsoft.Extensions.DependencyInjection;

            public interface IStore { }
            public sealed class SqlStore : IStore { }

            public static class Wiring
            {
                public static void Register(IServiceCollection s) => s.AddSingleton<IStore, SqlStore>();
            }
            """);

        // Both a DiBinding and an Interface edge point at SqlStore; grouping must collapse them.
        Assert.Equal(["SqlStore"], ImplOrder(g, "IStore"));
    }

    [Fact]
    public void An_extension_method_is_one_node_and_its_callers_reach_it()
    {
        var g = Index(
            """
            public interface IPipeline { }

            public static class PipelineExtensions
            {
                public static IPipeline UseRetry(this IPipeline p) => p;
            }

            public static class Startup
            {
                public static void Configure(IPipeline p) => p.UseRetry();
            }
            """);

        // A declaration gives the original symbol, a call site gives the reduced one whose receiver
        // has left the parameter list. Keyed apart, they became two nodes for one method, and the
        // call edge landed on the copy that no query ever resolves to.
        var declared = Assert.Single(g.Nodes, n => n.Short == "PipelineExtensions.UseRetry");
        var caller = g.Nodes.Single(n => n.Short == "Startup.Configure");

        Assert.Contains(g.Out(caller.Id), e => e.To == declared.Id && e.Kind == EdgeKind.Call);
    }
    
    public void Dispose()
    {
        foreach (var dir in _temp)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }
}