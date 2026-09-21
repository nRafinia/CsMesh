using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// "calls resolved" is TotalCallSites minus UnresolvedCallSites. A call the compiler left
/// ambiguous -- two candidates, no symbol -- is not a resolved call, but it used to be subtracted
/// from neither side, so an ambiguous site counted as bound. On a real solution that let 17 broken
/// sites coexist with a reported 100.0%.
///
/// The two unresolved causes are counted and shown separately: "no candidate" usually means a
/// reference is missing, "ambiguous overload" means two in-scope symbols fit and the compiler
/// refused. Different problems, same total.
///
/// Console.Out is process-global, so this joins the console-capture collection.
/// </summary>
[Collection("console-capture")]
public sealed class CallResolutionMetricTests : IDisposable
{
    private readonly string _root;

    public CallResolutionMetricTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-callmetric-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    /// <summary>
    /// string and StringBuilder share no conversion, so Pick(null) fits both and binds neither.
    /// </summary>
    private void WriteAmbiguousCall()
    {
        File.WriteAllText(Path.Combine(_root, "Ambiguous.cs"), """
            namespace Demo;

            public sealed class Caller
            {
                public void Pick(string value) { }
                public void Pick(System.Text.StringBuilder value) { }
                public void Go() => Pick(null);
            }
            """);
    }

    private static string Capture(Func<int> run)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            run();
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }

    [Fact]
    public void An_ambiguous_overload_counts_as_an_unresolved_call()
    {
        WriteAmbiguousCall();

        var graph = Indexer.Build(_root);

        Assert.Equal(1, graph.TotalCallSites);
        Assert.Equal(1, graph.UnresolvedCallSites);
        Assert.Equal(1, graph.UnresolvedByReason.GetValueOrDefault("call/ambiguous-overload"));
        Assert.Equal(0, graph.UnresolvedByReason.GetValueOrDefault("call/no-candidate-symbol"));
    }

    [Fact]
    public void Doctor_reports_less_than_full_and_names_both_causes()
    {
        WriteAmbiguousCall();
        GraphStore.Save(Indexer.Build(_root));

        var text = Capture(() => DoctorCommand.Execute(_root, new Options([])));

        Assert.Contains("ambiguous overload", text, StringComparison.Ordinal);
        Assert.Contains("no candidate", text, StringComparison.Ordinal);
        Assert.DoesNotContain("100.0%", text, StringComparison.Ordinal);
    }
}
