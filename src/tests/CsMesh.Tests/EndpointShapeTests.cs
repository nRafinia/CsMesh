using CsMesh.Analysis;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Endpoint registration is where a codebase's HTTP surface either enters the graph or vanishes
/// without trace. Vanishing is the dangerous outcome: blast-radius reports zero entrypoints, which
/// reads as "nothing serves this" rather than "I did not recognise the registration", and there is
/// no unresolved site anywhere to suggest otherwise.
///
/// The shapes here are taken from real solutions rather than invented.
/// </summary>
public sealed class EndpointShapeTests : IDisposable
{
    private readonly string _root;
    private readonly Graph _graph;

    public EndpointShapeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-endpoints-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        // The framework shape: pattern first, handler second.
        File.WriteAllText(Path.Combine(_root, "src", "Direct.cs"), """
            namespace Demo;

            public static class Direct
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/orders", ListOrders);
                    app.MapPost("/orders", CreateOrder);
                }

                public static void ListOrders() { }
                public static void CreateOrder() { }
            }
            """);

        // The wrapper shape, as the Clean Architecture template writes it: the project declares
        // its own Map* taking the handler first so the OpenAPI operation id comes from the method
        // name, and the prefix is applied later by a MapGroup the source never spells out.
        File.WriteAllText(Path.Combine(_root, "src", "Wrapped.cs"), """
            namespace Demo;

            public static class Builders
            {
                public static object MapPost(this IEndpointRouteBuilder b, Delegate handler, string pattern = "") => null!;
                public static object MapPut(this IEndpointRouteBuilder b, Delegate handler, string pattern) => null!;
            }

            public static class Wrapped
            {
                public static void Map(IEndpointRouteBuilder group)
                {
                    group.MapPost(CreateItem);
                    group.MapPut(UpdateItem, "{id}");
                }

                public static void CreateItem() { }
                public static void UpdateItem() { }
            }
            """);

        File.WriteAllText(Path.Combine(_root, "src", "Shims.cs"), """
            namespace Demo;
            public interface IEndpointRouteBuilder { }
            public static class Shims
            {
                public static object MapGet(this IEndpointRouteBuilder b, string pattern, Delegate handler) => null!;
                public static object MapPost(this IEndpointRouteBuilder b, string pattern, Delegate handler) => null!;
            }
            """);

        _graph = Indexer.Build(_root);
        _graph.Freeze();
    }

    private Node Node(string shortName) =>
        _graph.Nodes.FirstOrDefault(n => n.Short == shortName)
        ?? throw new InvalidOperationException($"no node '{shortName}'. Have: " +
                                               string.Join(", ", _graph.Nodes.Select(n => n.Short).Take(40)));

    /// <summary>A whole URL was read from the source, so it is reported as one.</summary>
    [Fact]
    public void AFrameworkRegistrationKeepsItsFullRouteTag()
    {
        Assert.Contains("http:GET /orders", Node("Direct.ListOrders").Tags);
        Assert.Contains("http:POST /orders", Node("Direct.CreateOrder").Tags);
    }

    /// <summary>
    /// The failure this was written for. One argument, no literal, so both of the old checks
    /// rejected it and an entire HTTP surface went missing silently.
    /// </summary>
    [Fact]
    public void AHandlerFirstRegistrationIsStillRecognised()
    {
        Assert.Contains("endpoint:POST", Node("Wrapped.CreateItem").Tags);
    }

    [Fact]
    public void AHandlerFirstRegistrationWithARelativePatternIsRecognised()
    {
        Assert.Contains("endpoint:PUT", Node("Wrapped.UpdateItem").Tags);
    }

    /// <summary>
    /// A relative pattern is a fragment, not a URL. The prefix lives on a MapGroup that templates
    /// like this one build by reflection, so there is nothing to read -- and a confident wrong URL
    /// is worse than an absent one. The verb is certain; only the path is withheld.
    /// </summary>
    [Fact]
    public void APartialRouteIsNeverPrintedAsIfItWereWhole()
    {
        var tags = Node("Wrapped.UpdateItem").Tags;

        Assert.DoesNotContain(tags, t => t.StartsWith("http:", StringComparison.Ordinal));
        Assert.DoesNotContain(tags, t => t.Contains("{id}", StringComparison.Ordinal));
    }

    /// <summary>
    /// Being reachable from an entrypoint is what blast-radius answers, and it is the thing that
    /// was wrong: every endpoint method existed in the graph, none of them counted.
    /// </summary>
    [Fact]
    public void EndpointMethodsCountAsEntrypointsWhicheverShapeRegisteredThem()
    {
        var entrypoints = _graph.Nodes
            .Where(n => n.Tags.Any(t => t.StartsWith("http:", StringComparison.Ordinal)
                                        || t.StartsWith("endpoint:", StringComparison.Ordinal)))
            .Select(n => n.Short)
            .ToList();

        Assert.Contains("Direct.ListOrders", entrypoints);
        Assert.Contains("Wrapped.CreateItem", entrypoints);
        Assert.Contains("Wrapped.UpdateItem", entrypoints);
    }

    /// <summary>
    /// Loosening the argument checks must not turn every Map-named call into a route. Nothing here
    /// passes anything callable, so nothing should be tagged.
    /// </summary>
    [Fact]
    public void ACallableArgumentIsRequiredBeforeAnythingIsTagged()
    {
        var noise = Path.Combine(_root, "src", "Noise.cs");
        File.WriteAllText(noise, """
            namespace Demo;

            public static class Noise
            {
                public static void Go(IEndpointRouteBuilder app)
                {
                    app.MapGet("/health", "not-a-handler");
                    app.MapPost("/health");
                }

                public static object MapGet(this IEndpointRouteBuilder b, string p, string s) => null!;
                public static object MapPost(this IEndpointRouteBuilder b, string p) => null!;
            }
            """);

        var graph = Indexer.Build(_root);
        graph.Freeze();

        Assert.DoesNotContain(graph.Nodes, n =>
            n.Short.StartsWith("Noise.", StringComparison.Ordinal) &&
            n.Tags.Any(t => t.StartsWith("http:", StringComparison.Ordinal)
                            || t.StartsWith("endpoint:", StringComparison.Ordinal)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }
}

/// <summary>
/// A validator already reached its command in the graph, because AbstractValidator&lt;T&gt; is a
/// real type reference. What was missing was any way to tell that edge apart from an ordinary
/// caller, so 'who touches this command' listed the validator beside the handler with nothing to
/// say that one can reject the request before the other ever sees it.
/// </summary>
public sealed class ValidatorTagTests : IDisposable
{
    private readonly string _root;
    private readonly Graph _graph;

    public ValidatorTagTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-validators-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        File.WriteAllText(Path.Combine(_root, "src", "Feature.cs"), """
            namespace Demo;

            public class AbstractValidator<T> { }

            public record CreateOrder(string Reference);

            public class CreateOrderValidator : AbstractValidator<CreateOrder>
            {
                public CreateOrderValidator() { }
            }

            public class CreateOrderHandler
            {
                public void Handle(CreateOrder command) { }
            }
            """);

        _graph = Indexer.Build(_root);
        _graph.Freeze();
    }

    private Node Node(string shortName) =>
        _graph.Nodes.FirstOrDefault(n => n.Short == shortName)
        ?? throw new InvalidOperationException($"no node '{shortName}'");

    [Fact]
    public void AValidatorIsTagged()
    {
        Assert.Contains("validator", Node("CreateOrderValidator").Tags);
    }

    /// <summary>The handler beside it must not be, or the tag separates nothing.</summary>
    [Fact]
    public void OrdinaryTypesAreNotTagged()
    {
        Assert.DoesNotContain("validator", Node("CreateOrderHandler").Tags);
        Assert.DoesNotContain("validator", Node("CreateOrder").Tags);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }
}
