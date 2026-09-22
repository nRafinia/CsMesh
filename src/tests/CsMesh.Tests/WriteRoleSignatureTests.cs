using CsMesh.Analysis;
using CsMesh.Models;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Role is part of the finding signature, so under --calls a member access that became a write is
/// a structural change. It must stay invisible by default: Call edges are excluded from
/// changes/review unless --calls is passed, and if that ever stopped holding, every index upgraded
/// to v14 would report all of its member accesses as changed at once.
/// </summary>
public sealed class WriteRoleSignatureTests
{
    private static Graph Fork(Graph source, EdgeRole? callRole)
    {
        var copy = new Graph
        {
            Root = source.Root,
            FormatVersion = source.FormatVersion,
            BuiltAt = source.BuiltAt,
            Nodes = source.Nodes,
            Edges = source.Edges.Select(e => new Edge
            {
                From = e.From,
                To = e.To,
                Kind = e.Kind,
                Note = e.Note,
                Confidence = e.Confidence,
                Source = e.Source,
                Site = e.Site,
                Role = e.Kind == EdgeKind.Call ? callRole : e.Role
            }).ToList()
        };

        copy.Freeze();
        return copy;
    }

    private static Graph Indexed()
    {
        var root = Path.Combine(Path.GetTempPath(), "csmesh-role-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "W.cs"),
            "namespace Demo;\npublic class W { public int P { get; set; } public void Go() { this.P = 1; } }");
        try { return Indexer.Build(root); }
        finally { try { Directory.Delete(root, true); } catch { /* temp dir */ } }
    }

    [Fact]
    public void A_read_that_became_a_write_is_a_change_under_calls()
    {
        var before = Fork(Indexed(), EdgeRole.Read);
        var after = Fork(Indexed(), EdgeRole.Read | EdgeRole.Write);

        var findings = Queries.DiffFindings(after, before, includeCalls: true);

        Assert.NotEmpty(findings);
        Assert.Contains(findings, f => f.Kind == EdgeKind.Call);
    }

    [Fact]
    public void A_read_that_became_a_write_is_invisible_by_default()
    {
        var before = Fork(Indexed(), EdgeRole.Read);
        var after = Fork(Indexed(), EdgeRole.Read | EdgeRole.Write);

        Assert.Empty(Queries.DiffFindings(after, before, includeCalls: false));
    }
}
