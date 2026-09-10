using CsMesh.Analysis;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

public sealed class WiringSiteHopTests(GraphFixture fixture) : IClassFixture<GraphFixture>
{
    private static BudgetWriter Writer(int budget = 4000) => new(budget);

    [Fact]
    public void Path_and_trace_print_site_on_site_bearing_hop()
    {
        var from = fixture.Node("Api.OrderController.Post");
        var to = fixture.Node("CompanyA.Handlers.CreateOrderHandler.Handle");

        // Verify Path
        var wPath = Writer();
        var exitPath = Queries.Path(fixture.Graph, from, to, 6, wPath, []);
        Assert.Equal(Exit.Ok, exitPath);

        var pathHop = Assert.Single(wPath.Lines, l => l.Contains("CreateOrderHandler.Handle"));
        Assert.Matches(@"@ src[/\\]Api\.cs:\d+", pathHop);

        var pathRow = Assert.Single(wPath.Rows, r => r.Symbol == "CreateOrderHandler.Handle");
        Assert.NotNull(pathRow.Site);
        Assert.Matches(@"src[/\\]Api\.cs:\d+", pathRow.Site);

        // Verify Trace
        var wTrace = Writer();
        var exitTrace = Queries.Trace(fixture.Graph, from, 2, wTrace, []);
        Assert.Equal(Exit.Ok, exitTrace);

        var traceHop = Assert.Single(wTrace.Lines, l => l.Contains("CreateOrderHandler.Handle"));
        Assert.Matches(@"@ src[/\\]Api\.cs:\d+", traceHop);

        var traceRow = Assert.Single(wTrace.Rows, r => r.Symbol == "CreateOrderHandler.Handle");
        Assert.NotNull(traceRow.Site);
        Assert.Matches(@"src[/\\]Api\.cs:\d+", traceRow.Site);
    }

    [Fact]
    public void Sibling_lookup_resolves_wiring_site_on_interface_dispatch_hop()
    {
        var temp = Path.Combine(Path.GetTempPath(), "csmesh-sibling-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(temp, "Services.cs"), """
                namespace TestApp
                {
                    public interface IGreeter { void Greet(); }
                    public class Greeter : IGreeter { public void Greet() { } }
                }
                """);
            File.WriteAllText(Path.Combine(temp, "Caller.cs"), """
                namespace TestApp
                {
                    public class Caller
                    {
                        private readonly IGreeter _g;
                        public Caller(IGreeter g) => _g = g;
                        public void Run() => _g.Greet();
                    }
                }
                """);
            File.WriteAllText(Path.Combine(temp, "Wiring.cs"), """
                namespace TestApp
                {
                    public static class Wiring
                    {
                        public static void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection s)
                        {
                            s.AddScoped<IGreeter, Greeter>();
                        }
                    }
                }
                namespace Microsoft.Extensions.DependencyInjection
                {
                    public interface IServiceCollection { }
                    public static class Ext
                    {
                        public static IServiceCollection AddScoped<TService, TImpl>(this IServiceCollection s) => s;
                    }
                }
                """);

            var graph = Indexer.Build(temp);
            graph.Freeze();

            var caller = graph.Nodes.Single(n => n.Short == "Caller.Run");
            var greeterMethod = graph.Nodes.Single(n => n.Short == "Greeter.Greet");

            // Verify Path: caller -> IGreeter.Greet -> Greeter.Greet
            var wPath = Writer();
            var exitPath = Queries.Path(graph, caller, greeterMethod, 6, wPath, []);
            Assert.Equal(Exit.Ok, exitPath);

            var pathHop = Assert.Single(wPath.Lines, l => l.Contains("-> Greeter.Greet"));
            Assert.Matches(@"@ Wiring\.cs:\d+", pathHop);

            var pathRow = Assert.Single(wPath.Rows, r => r.Symbol == "Greeter.Greet");
            Assert.NotNull(pathRow.Site);
            Assert.Matches(@"Wiring\.cs:\d+", pathRow.Site);

            // Verify Trace: caller -> IGreeter.Greet -> Greeter.Greet
            var wTrace = Writer();
            var exitTrace = Queries.Trace(graph, caller, 6, wTrace, []);
            Assert.Equal(Exit.Ok, exitTrace);

            var traceHop = Assert.Single(wTrace.Lines, l => l.Contains("-> Greeter.Greet"));
            Assert.Matches(@"@ Wiring\.cs:\d+", traceHop);

            var traceRow = Assert.Single(wTrace.Rows, r => r.Symbol == "Greeter.Greet");
            Assert.NotNull(traceRow.Site);
            Assert.Matches(@"Wiring\.cs:\d+", traceRow.Site);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    [Fact]
    public void Sibling_lookup_omits_site_when_service_has_multiple_bindings()
    {
        var temp = Path.Combine(Path.GetTempPath(), "csmesh-multisibling-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(temp, "Services.cs"), """
                namespace TestApp
                {
                    public interface IGreeter { void Greet(); }
                    public class GreeterA : IGreeter { public void Greet() { } }
                    public class GreeterB : IGreeter { public void Greet() { } }
                }
                """);
            File.WriteAllText(Path.Combine(temp, "Caller.cs"), """
                namespace TestApp
                {
                    public class Caller
                    {
                        private readonly IGreeter _g;
                        public Caller(IGreeter g) => _g = g;
                        public void Run() => _g.Greet();
                    }
                }
                """);
            File.WriteAllText(Path.Combine(temp, "Wiring.cs"), """
                namespace TestApp
                {
                    public static class Wiring
                    {
                        public static void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection s)
                        {
                            s.AddScoped<IGreeter, GreeterA>();
                            s.AddScoped<IGreeter, GreeterB>();
                        }
                    }
                }
                namespace Microsoft.Extensions.DependencyInjection
                {
                    public interface IServiceCollection { }
                    public static class Ext
                    {
                        public static IServiceCollection AddScoped<TService, TImpl>(this IServiceCollection s) => s;
                    }
                }
                """);

            var graph = Indexer.Build(temp);
            graph.Freeze();

            var caller = graph.Nodes.Single(n => n.Short == "Caller.Run");
            var greeterA = graph.Nodes.Single(n => n.Short == "GreeterA.Greet");

            var wPath = Writer();
            var exitPath = Queries.Path(graph, caller, greeterA, 6, wPath, []);
            Assert.Equal(Exit.Ok, exitPath);

            var pathHop = Assert.Single(wPath.Lines, l => l.Contains("-> GreeterA.Greet"));
            Assert.DoesNotContain("@ Wiring.cs", pathHop);

            var pathRow = Assert.Single(wPath.Rows, r => r.Symbol == "GreeterA.Greet");
            Assert.Null(pathRow.Site);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }
}
