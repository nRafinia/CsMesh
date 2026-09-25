using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Two overloads of one member in one type and project defeat --project: same name, same project,
/// same file. The selector names the parameter list so the walk resolves to the one meant. These pin
/// the parse and the match; the candidate-row/footer formatting and silence come next.
/// </summary>
[Collection("console-capture")]
public sealed class OverloadSelectorTests : IDisposable
{
    private readonly string _root;
    private readonly Models.Graph _graph;

    public OverloadSelectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-selector-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        File.WriteAllText(Path.Combine(_root, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        File.WriteAllText(Path.Combine(_root, "Overloads.cs"), """
            using System;
            using System.Collections.Generic;

            namespace Demo;

            public sealed class Overloads
            {
                public void Handle(int value) { }
                public void Handle(string value) { }
                public void Handle(int a, string b) { }
                public void Handle(int[] values) { }
                public void Handle(List<int> values) { }
                public void Handle(List<string> values) { }
                public void Handle(ref int value) { }
                public void Handle(int? value) { }
                public void Handle((int, string) pair) { }
                public void Handle(Guid id) { }

                public void Init(out int value) { value = 0; }
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

    private SymbolSelector.Result Analyze(string query) => SymbolSelector.Analyze(_graph, query);

    private int Trace(string query)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            return QueryCommand.Execute(_root, new Options([query]), "trace");
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private string TraceErr(string query, out int exit)
    {
        var original = Console.Error;
        var buffer = new StringWriter();
        try
        {
            Console.SetError(buffer);
            exit = QueryCommand.Execute(_root, new Options([query]), "trace");
        }
        finally
        {
            Console.SetError(original);
        }

        return buffer.ToString();
    }

    [Fact]
    public void A_selector_resolves_the_one_overload_and_traces_it()
    {
        Assert.Equal(Exit.Ok, Trace("Demo.Overloads.Handle(int)"));
        Assert.Equal(Exit.Ok, Trace("Demo.Overloads.Handle(string)"));
        Assert.Equal(Exit.Ok, Trace("Demo.Overloads.Handle(int,string)"));
        Assert.Equal(Exit.Ok, Trace("Demo.Overloads.Handle(int[])"));
    }

    [Fact]
    public void A_name_alone_stays_ambiguous()
    {
        Assert.Equal(Exit.Ambiguous, Trace("Demo.Overloads.Handle"));
    }

    [Fact]
    public void A_selector_with_no_matching_overload_is_exit_one()
    {
        Assert.Equal(Exit.NotFound, Trace("Demo.Overloads.Handle(double)"));
    }

    [Fact]
    public void An_unclosed_selector_is_exit_sixty_four_and_names_the_quoting_remedy()
    {
        var error = TraceErr("Demo.Overloads.Handle(int", out var exit);

        Assert.Equal(Exit.Usage, exit);
        Assert.Contains("Quote", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Type_names_match_by_alias_and_by_suffix()
    {
        Assert.Single(Analyze("Demo.Overloads.Handle(Int32)").Matches);
        Assert.Single(Analyze("Demo.Overloads.Handle(System.Int32)").Matches);
        Assert.Single(Analyze("Demo.Overloads.Handle(System.Collections.Generic.List<int>)").Matches);
        Assert.Single(Analyze("Demo.Overloads.Handle(int)").Matches);
    }

    [Fact]
    public void Generic_arguments_are_compared_not_ignored()
    {
        var ints = Assert.Single(Analyze("Demo.Overloads.Handle(List<int>)").Matches);
        var strings = Assert.Single(Analyze("Demo.Overloads.Handle(List<string>)").Matches);

        Assert.NotEqual(ints.Key, strings.Key);
    }

    [Fact]
    public void Ref_out_and_in_are_exact()
    {
        var byRef = Assert.Single(Analyze("Demo.Overloads.Handle(ref int)").Matches);
        var byValue = Assert.Single(Analyze("Demo.Overloads.Handle(int)").Matches);
        Assert.NotEqual(byRef.Key, byValue.Key);

        Assert.Single(Analyze("Demo.Overloads.Init(out int)").Matches);
        Assert.Empty(Analyze("Demo.Overloads.Init(int)").Matches);
    }

    [Fact]
    public void Nullable_is_strict_first_and_never_forgives_a_missing_question_mark()
    {
        // int? exists, so the strict pass keeps it and the int overload is not picked up.
        var nullable = Assert.Single(Analyze("Demo.Overloads.Handle(int?)").Matches);
        Assert.Contains("int?", nullable.Key, StringComparison.Ordinal);

        // No Handle(string?) exists; the user's spurious '?' is forgiven only then.
        Assert.Single(Analyze("Demo.Overloads.Handle(string?)").Matches);
    }

    [Fact]
    public void Tuples_match_with_or_without_element_names()
    {
        var bare = Assert.Single(Analyze("Demo.Overloads.Handle((int,string))").Matches);
        var spaced = Assert.Single(Analyze("Demo.Overloads.Handle((int, string))").Matches);
        Assert.Equal(bare.Key, spaced.Key);
    }

    [Fact]
    public void A_partial_list_matches_nothing()
    {
        // The list is a selector, not a prefix filter: three tokens match no two-parameter overload.
        var result = Analyze("Demo.Overloads.Handle(int,string,string)");

        Assert.Equal(SymbolSelector.SelectorStatus.NoOverloadMatch, result.Status);
        Assert.Empty(result.Matches);
    }
}
