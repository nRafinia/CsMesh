using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A miss is a fork in an agent's session: a name it can correct costs one line, and a bare
/// 'not found' costs a fallback to grep over the whole repository. These pin the cases where the
/// old one-directional Contains() check produced the expensive answer to an easy question.
/// </summary>
public sealed class SuggestionTests(GraphFixture fixture) : IClassFixture<GraphFixture>
{
    /// <summary>
    /// The failure this was written for. An agent reads an endpoint, names the operation the way
    /// prose names it, and asks for the padded form of a real method. The old check tested whether
    /// the real name contained the query, which is backwards for this -- "CountAsync" does not
    /// contain "CountOrdersAsync" -- so the answer was a flat miss.
    /// </summary>
    [Fact]
    public void PaddedNameFindsTheShorterRealMethod()
    {
        var hits = SymbolSuggest.For(fixture.Graph, "CountOrdersAsync");

        Assert.Contains(hits, h => h.Node.Short.EndsWith("CountAsync", StringComparison.Ordinal));
    }

    /// <summary>A transposition shares no whole word with the name it meant, so tokens alone miss it.</summary>
    [Fact]
    public void TransposedLettersStillFindTheType()
    {
        var hits = SymbolSuggest.For(fixture.Graph, "SqlOrderStroe");

        Assert.Contains(hits, h => h.Node.Short.EndsWith("SqlOrderStore", StringComparison.Ordinal));
    }

    /// <summary>
    /// A qualifier the caller supplied is evidence. Two types can carry the same method name, and
    /// ranking the one that was actually named below the one that was not turns a correct
    /// suggestion into a misleading one.
    /// </summary>
    [Fact]
    public void NamedOwnerOutranksTheSameMethodElsewhere()
    {
        var hits = SymbolSuggest.For(fixture.Graph, "SqlOrderStore.SaveOrderAsync");

        Assert.NotEmpty(hits);
        Assert.Contains("SqlOrderStore", hits[0].Node.Short, StringComparison.Ordinal);
    }

    /// <summary>Nonsense must not produce confident suggestions; a wrong pointer is worse than none.</summary>
    [Fact]
    public void UnrelatedInputSuggestsNothing()
    {
        Assert.Empty(SymbolSuggest.For(fixture.Graph, "zzqxwv"));
    }

    [Fact]
    public void TokeniserSplitsOnCaseAndKeepsCapitalRunsWhole()
    {
        var tokens = SymbolSuggest.Tokens("DeleteCredentialAsync");

        Assert.Contains("delete", tokens);
        Assert.Contains("credential", tokens);
        Assert.Contains("async", tokens);

        Assert.Contains("http", SymbolSuggest.Tokens("HttpClientFactory"));
        Assert.Contains("client", SymbolSuggest.Tokens("HttpClientFactory"));
    }

    [Fact]
    public void EditDistanceIsSymmetricAndCaseInsensitive()
    {
        Assert.Equal(0, SymbolSuggest.Distance("Order", "order"));
        Assert.Equal(SymbolSuggest.Distance("kitten", "sitting"), SymbolSuggest.Distance("sitting", "kitten"));
        Assert.Equal(3, SymbolSuggest.Distance("kitten", "sitting"));
    }
}

/// <summary>
/// One generic paragraph for every kind of miss was cheap to write and expensive to act on. A
/// route template and a connection string are not the same problem and must not get the same
/// advice -- one of them the graph can answer.
/// </summary>
public sealed class OutOfGraphTests(GraphFixture fixture) : IClassFixture<GraphFixture>
{
    [Fact]
    public void ConfigKeyPointsAtAppSettingsRatherThanTheGraph()
    {
        var verdict = OutOfGraph.Classify("ConnectionStrings", fixture.Graph);

        Assert.NotNull(verdict);
        Assert.Contains(verdict.Next, n => n.Contains("appsettings", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ColonSeparatedKeyIsRecognisedWithoutAKnownRoot()
    {
        var verdict = OutOfGraph.Classify("Wibble:Wobble:Timeout", fixture.Graph);

        Assert.NotNull(verdict);
        Assert.Contains(verdict.Next, n => n.Contains("appsettings", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Routes are the case the old message got wrong twice: it called them ungraphed and sent the
    /// caller to grep, when handlers carry their template as a tag and entrypoints can answer.
    /// </summary>
    [Fact]
    public void RouteIsSentToEntrypointsNotGrep()
    {
        var verdict = OutOfGraph.Classify("/api/v1/credentials/{id:guid}", fixture.Graph);

        Assert.NotNull(verdict);
        Assert.Contains(verdict.Next, n => n.StartsWith("csmesh entrypoints", StringComparison.Ordinal));
        Assert.DoesNotContain(verdict.Next, n => n.StartsWith("grep", StringComparison.Ordinal));
    }

    /// <summary>A route's version segment and its placeholders are not the word worth searching for.</summary>
    [Fact]
    public void RouteSuggestionSkipsVersionAndPlaceholderSegments()
    {
        var verdict = OutOfGraph.Classify("/api/v1/credentials", fixture.Graph);

        Assert.NotNull(verdict);
        Assert.Contains(verdict.Next, n => n.Contains("credentials", StringComparison.Ordinal));
    }

    [Fact]
    public void EnvironmentVariableIsNotTreatedAsASymbol()
    {
        var verdict = OutOfGraph.Classify("ASPNETCORE_ENVIRONMENT", fixture.Graph);

        Assert.NotNull(verdict);
        Assert.Contains(verdict.Next, n => n.Contains("launchSettings", StringComparison.OrdinalIgnoreCase)
                                           || n.Contains("Dockerfile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RazorFileSaysWhyItIsAbsentRatherThanThatItDoesNotExist()
    {
        var verdict = OutOfGraph.Classify("Login.razor", fixture.Graph);

        Assert.NotNull(verdict);
        Assert.Contains("Razor", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProseIsRoutedToGrep()
    {
        var verdict = OutOfGraph.Classify("could not save the order", fixture.Graph);

        Assert.NotNull(verdict);
        Assert.Contains(verdict.Next, n => n.StartsWith("grep", StringComparison.Ordinal));
    }

    [Fact]
    public void TodoMarkerIsRoutedToGrep()
    {
        var verdict = OutOfGraph.Classify("TODO", fixture.Graph);

        Assert.NotNull(verdict);
        Assert.Contains(verdict.Next, n => n.StartsWith("grep", StringComparison.Ordinal));
    }

    /// <summary>
    /// The classifier must stay quiet for anything that reads like a name, or it will talk over
    /// the near-miss list -- which is the better answer whenever there is one.
    /// </summary>
    [Fact]
    public void PlainIdentifierIsLeftToTheSuggester()
    {
        Assert.Null(OutOfGraph.Classify("OrderServiceFactory", fixture.Graph));
    }
}

/// <summary>
/// Order of operations, which running the real binary is what caught. Token matching always finds
/// something: ASPNETCORE_ENVIRONMENT shares words with LooksLikeEnvironmentVariable, and prose
/// shares words with half the graph. Both are true and neither is an answer, so anything that
/// structurally cannot be an identifier has to be classified before the suggester speaks.
/// </summary>
public sealed class MissOrderingTests(GraphFixture fixture) : IClassFixture<GraphFixture>
{
    [Theory]
    [InlineData("ASPNETCORE_ENVIRONMENT")]
    [InlineData("Login.razor")]
    [InlineData("/api/v1/orders")]
    [InlineData("could not save the order")]
    [InlineData("Wibble:Wobble:Timeout")]
    public void InputThatCannotBeAnIdentifierIsDecisive(string query)
    {
        var verdict = OutOfGraph.Classify(query, fixture.Graph);

        Assert.NotNull(verdict);
        Assert.True(verdict.Decisive, $"'{query}' must outrank the suggester");
    }

    /// <summary>
    /// A bare word could be either -- Hangfire is a package and could equally be a class here --
    /// so the near-miss list keeps its turn and the classifier stays a fallback.
    /// </summary>
    [Fact]
    public void AnIdentifierShapedConfigRootLeavesRoomForTheSuggester()
    {
        var verdict = OutOfGraph.Classify("Serilog", fixture.Graph);

        Assert.NotNull(verdict);
        Assert.False(verdict.Decisive);
    }
}
