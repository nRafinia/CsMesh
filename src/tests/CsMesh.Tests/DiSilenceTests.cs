using CsMesh.Analysis;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A registration csmesh cannot draw an edge for has to say so.
///
/// It used to record only one failure -- a short name that matched two types. Every other way of
/// giving up returned null in silence: a service type from a NuGet package, a type in a project the
/// scope excluded, a registration form carrying no type argument at all. The visible result was
/// 'unresolved --kind di' answering "none", which reads as "DI is fully understood" when it means
/// "DI was never attempted here". On a solution with four projects outside the scope, that is the
/// difference between eight bindings looking correct and eight bindings looking suspicious.
/// </summary>
public sealed class DiSilenceTests : IDisposable
{
    private readonly List<string> _temp = [];

    private Graph Index(string source)
    {
        var root = Path.Combine(Path.GetTempPath(), "csmesh-di-" + Guid.NewGuid().ToString("N")[..8]);
        _temp.Add(root);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Program.cs"), source);
        return Indexer.Build(root);
    }

    private static IEnumerable<string> DiReasons(Graph g) =>
        g.UnresolvedByReason.Keys
            .Where(k => k.StartsWith("di/", StringComparison.Ordinal))
            .Select(k => k[3..]);

    /// <summary>
    /// A stand-in container. Using directives have to sit above it, so callers pass the body that
    /// follows and this is appended -- a file whose usings come after a namespace block does not
    /// parse, and a test built on one proves nothing about resolution.
    /// </summary>
    private const string Container =
        """

        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IServiceCollection { }
            public static class ServiceCollectionServiceExtensions
            {
                public static IServiceCollection AddScoped<TService, TImpl>(this IServiceCollection s) => s;
                public static IServiceCollection AddSingleton<TService, TImpl>(this IServiceCollection s) => s;
                public static IServiceCollection AddSingleton(this IServiceCollection s, object instance) => s;
            }
        }
        """;

    [Fact]
    public void A_registration_both_of_whose_sides_are_in_source_is_not_reported()
    {
        var g = Index(
            """
            using Microsoft.Extensions.DependencyInjection;

            public interface IStore { }
            public sealed class SqlStore : IStore { }

            public static class Wiring
            {
                public static void Register(IServiceCollection s) => s.AddScoped<IStore, SqlStore>();
            }
            """ + Container);

        Assert.Empty(DiReasons(g));
        Assert.Contains(g.Edges, e => e.Kind == EdgeKind.DiBinding);
    }

    [Fact]
    public void A_type_the_index_does_not_contain_is_reported()
    {
        // System.Text.StringBuilder is real, the compiler binds it, and it is not in the source
        // being indexed -- the same shape as a service type from a package or from a project the
        // solution file left out.
        var g = Index(
            """
            using Microsoft.Extensions.DependencyInjection;
            using System.Text;

            public interface IBuilder { }

            public static class Wiring
            {
                public static void Register(IServiceCollection s) => s.AddSingleton<IBuilder, StringBuilder>();
            }
            """ + Container);

        Assert.Contains("type-outside-index", DiReasons(g));
    }

    [Fact]
    public void A_registration_carrying_no_type_argument_is_reported()
    {
        var g = Index(
            """
            using Microsoft.Extensions.DependencyInjection;

            public sealed class Widget { }

            public static class Wiring
            {
                public static void Register(IServiceCollection s) => s.AddSingleton(new Widget());
            }
            """ + Container);

        Assert.Contains("no-type-argument", DiReasons(g));
    }

    [Fact]
    public void Code_with_no_registrations_reports_nothing()
    {
        var g = Index(
            """
            public interface IStore { }
            public sealed class SqlStore : IStore { }
            """);

        Assert.Empty(DiReasons(g));
    }

    public void Dispose()
    {
        foreach (var dir in _temp)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }
}