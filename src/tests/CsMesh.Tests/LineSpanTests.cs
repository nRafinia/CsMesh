using System.Text.Json;
using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A declaration's printed location is its full span, so an agent's follow-up Read is a precise
/// range: <c>file:start-end</c> for a multi-line declaration, <c>file:start</c> for a single-line
/// one. Both text and JSON carry it, and the JSON field sits next to the existing line rather than
/// replacing it.
/// </summary>
[Collection("console-capture")]
public sealed class LineSpanTests : IDisposable
{
    private readonly string _root;
    private readonly Graph _graph;

    public LineSpanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-span-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        File.WriteAllText(Path.Combine(_root, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        File.WriteAllText(Path.Combine(_root, "Widget.cs"), """
            namespace Demo
            {
                public sealed class Widget
                {
                    public int Value { get; set; }
                    public void Run()
                    {
                        Value = 1;
                    }
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

    private string Run(string query, bool json, out int exit)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            exit = QueryCommand.Execute(_root, new Options(json ? [query, "--json"] : [query]), "trace");
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }

    private Node Node(string shortName) => _graph.Nodes.Single(n => n.Short == shortName);

    [Fact]
    public void Multi_line_method_prints_its_span_in_text_and_json()
    {
        var method = Node("Widget.Run");
        Assert.True(method.EndLine > method.Line, "the fixture method must span more than one line");

        var text = Run("Demo.Widget.Run", json: false, out _);
        Assert.Contains($"Widget.cs:{method.Line}-{method.EndLine}", text, StringComparison.Ordinal);

        var raw = Run("Demo.Widget.Run", json: true, out _);
        var report = JsonSerializer.Deserialize(raw.Trim(), AppJsonContext.Default.QueryResult)!;

        var root = Assert.Single(report.Rows, r => r.Depth == 0);
        Assert.Equal(method.Line, root.Line);
        Assert.Equal(method.EndLine, root.EndLine);
    }

    [Fact]
    public void Single_line_property_prints_only_its_start_line_in_text_and_json()
    {
        var property = Node("Widget.Value");
        Assert.Equal(property.Line, property.EndLine);

        var text = Run("Demo.Widget.Value", json: false, out _);
        Assert.Contains($"Widget.cs:{property.Line}", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"Widget.cs:{property.Line}-", text, StringComparison.Ordinal);

        var raw = Run("Demo.Widget.Value", json: true, out _);
        var report = JsonSerializer.Deserialize(raw.Trim(), AppJsonContext.Default.QueryResult)!;

        var root = Assert.Single(report.Rows, r => r.Depth == 0);
        Assert.Equal(property.Line, root.Line);
        Assert.Equal(property.Line, root.EndLine);
    }
}
