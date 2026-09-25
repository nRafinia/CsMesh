using System.Text.Json;
using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Exit 3 on one member's overloads is the case --project cannot settle: same name, same project,
/// same file. Each candidate row now prints its selector, the footer points at it, and --json carries
/// it. The load-bearing property is that a printed selector, passed back verbatim, resolves to
/// exactly the row it was printed on.
/// </summary>
[Collection("console-capture")]
public sealed class OverloadSelectorCandidateTests : IDisposable
{
    private readonly string _root;
    private readonly Models.Graph _graph;

    public OverloadSelectorCandidateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-selrow-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        File.WriteAllText(Path.Combine(_root, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        // Two overloads take a type whose simple name is Token in different namespaces, so their
        // short selectors collide and the row has to qualify those positions to stay unique.
        File.WriteAllText(Path.Combine(_root, "Overloads.cs"), """
            using System;
            using System.Collections.Generic;

            namespace Demo
            {
                public sealed class Overloads
                {
                    public void Handle(int value) { }
                    public void Handle(string value) { }
                    public void Handle(int a, string b) { }
                    public void Handle(A.Token t) { }
                    public void Handle(B.Token t) { }
                    public void Handle(Guid id) { }
                }

                public sealed class Other
                {
                    public void Handle(int value) { }
                }
            }

            namespace A { public sealed class Token { } }
            namespace B { public sealed class Token { } }
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

    private static List<(string Selector, int Line)> CandidateRows(string text)
    {
        var rows = new List<(string, int)>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (!trimmed.StartsWith("  ", StringComparison.Ordinal)) continue;

            var body = trimmed[2..];
            var separator = body.IndexOf("  (", StringComparison.Ordinal);
            var colon = body.LastIndexOf(':');
            if (separator < 0 || colon < 0) continue;
            if (!int.TryParse(body[(colon + 1)..], out var number)) continue;

            rows.Add((body[..separator], number));
        }

        return rows;
    }

    [Fact]
    public void Every_printed_selector_resolves_to_exactly_its_own_candidate()
    {
        var text = Run("Demo.Overloads.Handle", json: false, out var exit);

        Assert.Equal(Exit.Ambiguous, exit);

        var rows = CandidateRows(text);
        Assert.NotEmpty(rows);

        foreach (var (selector, line) in rows)
        {
            var result = SymbolSelector.Analyze(_graph, selector);
            Assert.Equal(SymbolSelector.SelectorStatus.Matched, result.Status);

            var match = Assert.Single(result.Matches);
            Assert.Equal(line, match.Line);
        }
    }

    [Fact]
    public void Colliding_short_type_names_are_qualified_until_the_rows_differ()
    {
        var text = Run("Demo.Overloads.Handle", json: false, out _);

        Assert.Contains("Handle(A.Token)", text, StringComparison.Ordinal);
        Assert.Contains("Handle(B.Token)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_footer_advises_a_selector_only_when_every_candidate_shares_name_and_project()
    {
        var overloads = Run("Demo.Overloads.Handle", json: false, out _);
        Assert.Contains("selector", overloads, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--project", overloads, StringComparison.Ordinal);

        // A bare member matches Handle in two types, so --project is still the right advice.
        var mixed = Run("Handle", json: false, out _);
        Assert.Contains("--project", mixed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_json_rows_carry_the_selector_and_it_round_trips()
    {
        var raw = Run("Demo.Overloads.Handle", json: true, out var exit);
        var report = JsonSerializer.Deserialize(raw.Trim(), AppJsonContext.Default.QueryResult)!;

        Assert.Equal(Exit.Ambiguous, exit);
        Assert.NotEmpty(report.Rows);

        foreach (var row in report.Rows)
        {
            Assert.False(string.IsNullOrEmpty(row.Selector));

            var match = Assert.Single(SymbolSelector.Analyze(_graph, row.Selector!).Matches);
            Assert.Equal(row.Line, match.Line);
        }
    }
}
