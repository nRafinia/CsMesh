using CsMesh.Analysis;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The owner-edge path covers every declared member, so the short-name fallback in
/// <c>DeclaringTypeId</c> fires zero times on an ordinary index. Its cases have to be built by hand:
/// a node created by a reference rather than a declaration (no TypeUse owner edge), on a type whose
/// Short collides with another kind's. The fallback now qualifies on the fully-qualified Name and
/// returns null when even that is ambiguous.
/// </summary>
public sealed class DeclaringTypeFallbackTests
{
    private static Graph GraphOf(params Node[] nodes)
    {
        var g = new Graph
        {
            Root = "/repo",
            FormatVersion = Graph.CurrentFormatVersion,
            BuiltAt = DateTimeOffset.UtcNow,
            Nodes = [.. nodes],
            Edges = []
        };
        g.Freeze();
        return g;
    }

    private static Node Node(int id, string kind, string name, string @short, string key) =>
        new() { Id = id, Kind = kind, Name = name, Short = @short, Key = key, File = "src/Code.cs", Line = id };

    /// <summary>
    /// Two types named Widget in different namespaces both produce the member short
    /// "Widget.Changed", which is why the fallback cannot key on Short: it would return whichever
    /// Widget it walked first.
    /// </summary>
    [Fact]
    public void The_owner_is_found_by_full_name_when_short_collides()
    {
        var a = Node(1, "type", "A.Widget", "Widget", "type:A.Widget");
        var b = Node(2, "type", "B.Widget", "Widget", "type:B.Widget");
        var changed = Node(3, "event", "B.Widget.Changed", "Widget.Changed", "event:B.Widget.Changed");

        var g = GraphOf(a, b, changed);

        Assert.DoesNotContain(g.In(changed.Id), e => e.Kind == EdgeKind.TypeUse);
        Assert.Equal(b.Id, Queries.DeclaringTypeId(g, changed));
    }

    /// <summary>When the qualified prefix itself matches more than one node, null is the answer.</summary>
    [Fact]
    public void An_ambiguous_qualified_name_returns_null_not_the_first_match()
    {
        var a = Node(1, "type", "Demo.Widget", "Widget", "type:A");
        var b = Node(2, "type", "Demo.Widget", "Widget", "type:B");
        var changed = Node(3, "event", "Demo.Widget.Changed", "Widget.Changed", "event:Demo.Widget.Changed");

        var g = GraphOf(a, b, changed);

        Assert.Null(Queries.DeclaringTypeId(g, changed));
    }

    /// <summary>The self-check accepts enum; the fallback's type match must too, or an enum member is unreachable.</summary>
    [Fact]
    public void An_enum_owner_is_matched_by_the_fallback()
    {
        var status = Node(1, "enum", "Demo.OrderStatus", "OrderStatus", "enum:Demo.OrderStatus");
        var draft = Node(2, "enum-member", "Demo.OrderStatus.Draft", "OrderStatus.Draft", "field:Demo.OrderStatus.Draft");

        var g = GraphOf(status, draft);

        Assert.Equal(status.Id, Queries.DeclaringTypeId(g, draft));
    }

    [Fact]
    public void A_type_is_its_own_declaring_type()
    {
        var t = Node(5, "type", "Demo.Thing", "Thing", "type:Demo.Thing");

        var g = GraphOf(t);

        Assert.Equal(t.Id, Queries.DeclaringTypeId(g, t));
    }
}
