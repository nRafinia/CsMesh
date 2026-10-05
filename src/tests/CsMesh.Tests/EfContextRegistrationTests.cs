using CsMesh.Analysis;
using CsMesh.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// EF Core registers its contexts through the container like any other service, but the three method
/// names default to different lifetimes and one of them registers a factory rather than the context.
/// The golden fixture pins the edges; these assertions pin the parts a graph snapshot does not
/// render -- the self-registration tags -- and the receiver gate that keeps a same-named method on
/// another type from inventing a registration.
/// </summary>
public sealed class EfContextRegistrationTests
{
    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CsMesh.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "tests", "CsMesh.Tests", "Fixtures", "ef-context");
    }

    private static Graph Graph() => Indexer.Build(FixtureDir());

    private static Node Node(Graph g, string name) => g.Nodes.Single(n => n.Name == name);

    private static string Key(Graph g, int id) => g.ById(id)!.Key;

    [Fact]
    public void A_one_argument_registration_tags_the_context_scoped()
    {
        var g = Graph();

        Assert.Contains("di:scoped", Node(g, "Demo.OrdersContext").Tags);
        Assert.Contains("di:scoped", Node(g, "Demo.BillingContext").Tags);
    }

    [Fact]
    public void A_two_argument_registration_binds_the_service_to_the_implementation_scoped()
    {
        var g = Graph();
        var service = Node(g, "Demo.IOrdersStore");
        var implementation = Node(g, "Demo.OrdersStore");

        var edge = Assert.Single(g.Edges, e =>
            e.Kind == EdgeKind.DiBinding &&
            Key(g, e.From) == service.Key &&
            Key(g, e.To) == implementation.Key);

        Assert.Equal("scoped", edge.Note);
    }

    [Fact]
    public void A_constant_context_lifetime_overrides_the_scoped_default()
    {
        var g = Graph();
        var service = Node(g, "Demo.IClock");
        var implementation = Node(g, "Demo.SystemClock");

        var edge = Assert.Single(g.Edges, e =>
            e.Kind == EdgeKind.DiBinding &&
            Key(g, e.From) == service.Key &&
            Key(g, e.To) == implementation.Key);

        Assert.Equal("transient", edge.Note);
    }

    [Fact]
    public void The_factory_is_registered_singleton_and_the_context_is_not_registered()
    {
        var g = Graph();
        var factory = Node(g, "Microsoft.EntityFrameworkCore.IDbContextFactory<TContext>");
        var context = Node(g, "Demo.AuditContext");

        Assert.Contains("di:singleton", factory.Tags);
        Assert.DoesNotContain(context.Tags, t => t.StartsWith("di:", StringComparison.Ordinal));

        Assert.Single(g.Edges, e =>
            e.Kind == EdgeKind.DiBinding &&
            Key(g, e.From) == factory.Key &&
            Key(g, e.To) == context.Key);
    }

    [Fact]
    public void The_container_registration_is_recorded_and_the_same_named_call_elsewhere_is_not()
    {
        var g = Graph();

        var factory = Node(g, "Microsoft.EntityFrameworkCore.IDbContextFactory<TContext>");
        var registered = Node(g, "Demo.AuditContext");
        var ignored = Node(g, "Demo.NegativeContext");

        // The container call in this same compilation is recorded: the factory service takes the
        // singleton tag and the binding to the context it produces.
        Assert.Contains("di:singleton", factory.Tags);
        Assert.Single(g.Edges, e =>
            e.Kind == EdgeKind.DiBinding &&
            Key(g, e.From) == factory.Key &&
            Key(g, e.To) == registered.Key);

        // The same method names on a receiver that is not the container are not. Asserting the
        // positive in the same compilation is what lets this test fail when the receiver gate is
        // dropped instead of passing because nothing ran at all.
        Assert.DoesNotContain(ignored.Tags, t => t.StartsWith("di:", StringComparison.Ordinal));
        Assert.DoesNotContain(g.Edges, e => e.Kind == EdgeKind.DiBinding && Key(g, e.To) == ignored.Key);
    }

    /// <summary>
    /// The invocation symbol is not the thing the receiver gate can rely on. A registration whose
    /// call fails overload resolution still names its context in the syntax, and csmesh must record
    /// the registration anyway. Here the ambiguity is self-contained, so a standalone compilation
    /// reproduces the same null symbol the indexer sees.
    /// </summary>
    [Fact]
    public void A_registration_whose_invocation_symbol_is_null_still_tags_the_context()
    {
        const string source = """
            using System;
            using Microsoft.Extensions.DependencyInjection;

            namespace Microsoft.Extensions.DependencyInjection
            {
                public interface IServiceCollection { }
                public static class Ext
                {
                    // Deliberately ambiguous for a null argument: neither overload is more specific,
                    // so the invocation comes back with no symbol.
                    public static IServiceCollection AddDbContext<TContext>(this IServiceCollection s, IComparable a) => s;
                    public static IServiceCollection AddDbContext<TContext>(this IServiceCollection s, IFormattable a) => s;
                }
            }

            namespace Demo
            {
                public sealed class AmbiguousContext { }
                public static class Wiring
                {
                    public static void Register(IServiceCollection s) => s.AddDbContext<AmbiguousContext>(null);
                }
            }
            """;

        Assert.True(The_invocation_does_not_bind(source), "the invocation must not bind, or this test proves nothing");

        var root = Path.Combine(Path.GetTempPath(), "csmesh-efnull-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "Types.cs"), source);

            var g = Indexer.Build(root);

            Assert.Contains("di:scoped", Node(g, "Demo.AmbiguousContext").Tags);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir */ }
        }
    }

    private static bool The_invocation_does_not_bind(string source)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = paths.Where(p => p.Length > 0)
            .Select(p => MetadataReference.CreateFromFile(p)).Cast<MetadataReference>().ToList();

        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("null-symbol", [tree], references);
        var model = compilation.GetSemanticModel(tree);

        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression.ToString().Contains("AddDbContext<AmbiguousContext>", StringComparison.Ordinal));

        var info = model.GetSymbolInfo(invocation);
        return info.Symbol is null && info.CandidateReason == CandidateReason.OverloadResolutionFailure;
    }
}
