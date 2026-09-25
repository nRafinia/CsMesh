using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// An exit-1 selector is not silence: the name resolved, only the parameter list matched nothing.
/// silence answers that by listing the overloads that do exist with their selectors, so one can be
/// pasted back instead of a second guess.
/// </summary>
[Collection("console-capture")]
public sealed class OverloadSelectorSilenceTests : IDisposable
{
    private readonly string _root;
    private readonly Models.Graph _graph;

    public OverloadSelectorSilenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-selsilence-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        File.WriteAllText(Path.Combine(_root, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        File.WriteAllText(Path.Combine(_root, "Overloads.cs"), """
            namespace Demo
            {
                public sealed class Overloads
                {
                    public void Handle(int value) { }
                    public void Handle(string value) { }
                }
            }
            """);

        _graph = Indexer.Build(_root);
        _graph.Freeze();
        GraphStore.Save(_graph);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    private string Run(string query, out int exit)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            exit = QueryCommand.Execute(_root, new Options([query]), "silence");
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }

    [Fact]
    public void Silence_lists_the_existing_overloads_with_their_selectors()
    {
        var text = Run("Demo.Overloads.Handle(bool)", out var exit);

        Assert.Equal(Exit.NotFound, exit);
        Assert.Contains("no overload", text, StringComparison.Ordinal);
        Assert.Contains("Handle(int)", text, StringComparison.Ordinal);
        Assert.Contains("Handle(string)", text, StringComparison.Ordinal);

        // Every listed selector resolves to exactly one candidate.
        foreach (var selector in ListedSelectors(text))
        {
            var match = Assert.Single(SymbolSelector.Analyze(_graph, selector).Matches);
            Assert.Equal("Handle", match.Short.Split('.').Last());
        }
    }

    [Fact]
    public void A_matching_selector_does_not_list_overloads()
    {
        var text = Run("Demo.Overloads.Handle(int)", out var exit);

        Assert.NotEqual(Exit.NotFound, exit);
        Assert.DoesNotContain("no overload", text, StringComparison.Ordinal);
    }

    private static List<string> ListedSelectors(string text)
    {
        var selectors = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (!trimmed.StartsWith("  ", StringComparison.Ordinal)) continue;

            var body = trimmed[2..];
            var cut = body.IndexOf("  ", StringComparison.Ordinal);
            if (cut < 0) continue;

            var selector = body[..cut];
            if (selector.Contains("Handle(", StringComparison.Ordinal)) selectors.Add(selector);
        }

        return selectors;
    }
}
