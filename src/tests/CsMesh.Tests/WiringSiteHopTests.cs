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
    public void Sibling_lookup_prints_site_with_count_when_service_has_multiple_bindings()
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

            // Verify Path
            var wPath = Writer();
            var exitPath = Queries.Path(graph, caller, greeterA, 6, wPath, []);
            Assert.Equal(Exit.Ok, exitPath);

            var pathHop = Assert.Single(wPath.Lines, l => l.Contains("-> GreeterA.Greet"));
            Assert.Matches(@"@ Wiring\.cs:\d+ \(1 of 2 bindings\)", pathHop);

            var pathRow = Assert.Single(wPath.Rows, r => r.Symbol == "GreeterA.Greet");
            Assert.NotNull(pathRow.Site);
            Assert.Matches(@"Wiring\.cs:\d+", pathRow.Site);

            // Verify Trace
            var wTrace = Writer();
            var exitTrace = Queries.Trace(graph, caller, 6, wTrace, []);
            Assert.Equal(Exit.Ok, exitTrace);

            var traceHop = Assert.Single(wTrace.Lines, l => l.Contains("-> GreeterA.Greet"));
            Assert.Matches(@"@ Wiring\.cs:\d+ \(1 of 2 bindings\)", traceHop);

            var traceRow = Assert.Single(wTrace.Rows, r => r.Symbol == "GreeterA.Greet");
            Assert.NotNull(traceRow.Site);
            Assert.Matches(@"Wiring\.cs:\d+", traceRow.Site);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    [Fact]
    public void Sibling_lookup_annotates_low_confidence_scan_binding()
    {
        var temp = Path.Combine(Path.GetTempPath(), "csmesh-lowconf-" + Guid.NewGuid().ToString("N")[..8]);
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

            var graph = Indexer.Build(temp);
            var caller = graph.Nodes.Single(n => n.Short == "Caller.Run");
            var greeterIface = graph.Nodes.Single(n => n.Short == "IGreeter");
            var greeterImpl = graph.Nodes.Single(n => n.Short == "Greeter");

            // Add an assembly scan binding edge with score 0.55 < Edge.TrustThreshold (0.8)
            graph.Edges.Add(new CsMesh.Models.Edge
            {
                From = greeterIface.Id,
                To = greeterImpl.Id,
                Kind = CsMesh.Models.EdgeKind.DiBinding,
                Confidence = 0.55,
                Source = "assembly-scan",
                Site = "Startup.cs:41",
                Note = "scoped"
            });
            graph.Freeze();

            var wTrace = Writer();
            var exitTrace = Queries.Trace(graph, caller, 6, wTrace, []);
            Assert.Equal(Exit.Ok, exitTrace);

            var traceHop = Assert.Single(wTrace.Lines, l => l.Contains("-> Greeter.Greet"));
            Assert.Contains("@ Startup.cs:41 ?0.55 assembly-scan", traceHop);

            var traceRow = Assert.Single(wTrace.Rows, r => r.Symbol == "Greeter.Greet");
            Assert.Equal("Startup.cs:41", traceRow.Site);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    [Fact]
    public void Sibling_lookup_omits_site_when_no_binding_targets_implementation()
    {
        var temp = Path.Combine(Path.GetTempPath(), "csmesh-nobindingtarget-" + Guid.NewGuid().ToString("N")[..8]);
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
            var greeterB = graph.Nodes.Single(n => n.Short == "GreeterB.Greet");

            // Path to GreeterB (which has no DI binding targeting it)
            var wPath = Writer();
            var exitPath = Queries.Path(graph, caller, greeterB, 6, wPath, []);
            Assert.Equal(Exit.Ok, exitPath);

            var pathHop = Assert.Single(wPath.Lines, l => l.Contains("-> GreeterB.Greet"));
            Assert.Contains("[impl]", pathHop);
            Assert.DoesNotContain("@", pathHop);

            var pathRow = Assert.Single(wPath.Rows, r => r.Symbol == "GreeterB.Greet");
            Assert.Null(pathRow.Site);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    [Fact]
    public void FormatSite_shortens_to_line_when_site_in_target_file()
    {
        var temp = Path.Combine(Path.GetTempPath(), "csmesh-samefile-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(temp, "App.cs"), """
                namespace TestApp
                {
                    public interface IWorker { void DoWork(); }
                    public class Worker : IWorker
                    {
                        public void DoWork() { }

                        public static void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection s)
                        {
                            s.AddScoped<IWorker, Worker>();
                        }
                    }
                    public class Caller
                    {
                        private readonly IWorker _w;
                        public Caller(IWorker w) => _w = w;
                        public void Run() => _w.DoWork();
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
            var workerMethod = graph.Nodes.Single(n => n.Short == "Worker.DoWork");

            var wTrace = Writer();
            var exitTrace = Queries.Trace(graph, caller, 6, wTrace, []);
            Assert.Equal(Exit.Ok, exitTrace);

            var traceHop = Assert.Single(wTrace.Lines, l => l.Contains("-> Worker.DoWork"));
            Assert.Matches(@"@ line \d+", traceHop);
            Assert.DoesNotContain("@ App.cs:", traceHop);

            var traceRow = Assert.Single(wTrace.Rows, r => r.Symbol == "Worker.DoWork");
            Assert.NotNull(traceRow.Site);
            Assert.Matches(@"App\.cs:\d+", traceRow.Site);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    [Fact]
    public void Interface_dispatch_with_no_binding_prints_no_site_suffix()
    {
        var temp = Path.Combine(Path.GetTempPath(), "csmesh-nobinding-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(temp, "App.cs"), """
                namespace TestApp
                {
                    public interface IWorker { void DoWork(); }
                    public class Worker : IWorker { public void DoWork() { } }
                    public class Caller
                    {
                        private readonly IWorker _w;
                        public Caller(IWorker w) => _w = w;
                        public void Run() => _w.DoWork();
                    }
                }
                """);

            var graph = Indexer.Build(temp);
            graph.Freeze();

            var caller = graph.Nodes.Single(n => n.Short == "Caller.Run");
            var workerMethod = graph.Nodes.Single(n => n.Short == "Worker.DoWork");

            var wTrace = Writer();
            var exitTrace = Queries.Trace(graph, caller, 6, wTrace, []);
            Assert.Equal(Exit.Ok, exitTrace);

            var traceHop = Assert.Single(wTrace.Lines, l => l.Contains("-> Worker.DoWork"));
            Assert.Contains("[impl]", traceHop);
            Assert.DoesNotContain("@", traceHop);

            var traceRow = Assert.Single(wTrace.Rows, r => r.Symbol == "Worker.DoWork");
            Assert.Null(traceRow.Site);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    [Fact]
    public void Hop_site_suffix_overflows_tight_budget_with_exit_code_2()
    {
        var temp = Path.Combine(Path.GetTempPath(), "csmesh-budget-" + Guid.NewGuid().ToString("N")[..8]);
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

            var rootLine = $"Caller.Run  Caller.cs:{caller.Line}";
            var hopWithoutSite = $"  -> Greeter.Greet  [impl, di-bound]  Services.cs:{greeterMethod.Line}";
            var costWithoutSite = BudgetWriter.Estimate(rootLine) + BudgetWriter.Estimate(hopWithoutSite);

            // A budget set to exactly costWithoutSite fits the bare hop but overflows with the suffix.
            var wTight = new BudgetWriter(costWithoutSite);
            var exitTight = Queries.Trace(graph, caller, 6, wTight, []);
            Assert.Equal(Exit.OverBudget, exitTight);

            // Raising budget to include the suffix allows it to pass
            var siteSuffix = "  @ Wiring.cs:7";
            var costWithSite = BudgetWriter.Estimate(rootLine) + BudgetWriter.Estimate(hopWithoutSite + siteSuffix);
            var wGenerous = new BudgetWriter(costWithSite + 10);
            var exitGenerous = Queries.Trace(graph, caller, 6, wGenerous, []);
            Assert.Equal(Exit.Ok, exitGenerous);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }
}

