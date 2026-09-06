using CsMesh.Models;

namespace CsMesh.Common;

/// <summary>
/// Turns a name that resolved to nothing into the names it was probably meant to be.
///
/// The old suggestion pass asked one question -- does any symbol's short name contain the term --
/// which only ever fires when the caller typed a strict prefix or infix of the real name. The way
/// callers actually get a name wrong is the other direction: they remember the shape of it and
/// pad it out. An agent that has read an endpoint and wants the service behind it asks for
/// DeleteCredentialAsync, because that is what the operation is called in prose; the method is
/// DeleteAsync, and "DeleteAsync".Contains("DeleteCredentialAsync") is false, so the tool answers
/// a bare 'not found' and hands back the round trip it exists to remove.
///
/// Scoring is deliberately cheap on the common path. Every node gets a token comparison, which is
/// two small set operations; only the shortlist that survives that pays for an edit distance.
/// </summary>
public static class SymbolSuggest
{
    private const int Shortlist = 60;
    private const int MinimumScore = 30;

    public sealed record Hit(Node Node, int Score, string Why);

    /// <summary>
    /// Ranked near misses for a query that resolved to nothing.
    /// </summary>
    /// <param name="query">What the caller typed, dotted qualifiers and all.</param>
    public static List<Hit> For(Graph g, string query, int take = 5)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var leaf = Leaf(query);
        var owner = Owner(query);
        var leafTokens = Tokens(leaf);
        if (leafTokens.Count == 0) return [];

        var rough = new List<(Node Node, int Score, string Why)>();

        foreach (var node in g.Nodes)
        {
            if (node.Kind == "enum-member") continue;

            var candidateLeaf = Leaf(node.Short);
            var candidateTokens = Tokens(candidateLeaf);
            if (candidateTokens.Count == 0) continue;

            var shared = 0;
            foreach (var token in candidateTokens)
            {
                if (leafTokens.Contains(token)) shared++;
            }

            if (shared == 0) continue;

            // Every word of the candidate appearing in the query is the padding case:
            // DeleteAsync inside DeleteCredentialAsync. That is the miss worth catching.
            var covered = (double)shared / candidateTokens.Count;
            var recall = (double)shared / leafTokens.Count;

            var score = (int)(covered * 55 + recall * 25);
            var why = covered >= 1.0 ? "contains" : "shares words";

            // A qualifier the caller supplied is evidence, not noise. Asking for
            // CredentialService.DeleteCredentialAsync should not rank AuditWriter.DeleteAsync
            // above the method on the type that was named.
            if (owner.Length > 0 &&
                node.Short.Contains(owner, StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
                why = "same type";
            }

            rough.Add((node, score, why));
        }

        if (rough.Count == 0) return Typos(g, leaf, take);

        // Edit distance is the expensive signal, so it only runs on names that already survived
        // the token pass. It is what separates a genuine near miss from a shared common word.
        var ranked = rough
            .OrderByDescending(r => r.Score)
            .Take(Shortlist)
            .Select(r =>
            {
                var distance = Distance(Leaf(r.Node.Short), leaf);
                var longest = Math.Max(Leaf(r.Node.Short).Length, leaf.Length);
                var closeness = longest == 0 ? 0 : 1.0 - (double)distance / longest;
                return new Hit(r.Node, r.Score + (int)(closeness * 20), r.Why);
            })
            .Where(h => h.Score >= MinimumScore)
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Node.Short.Length)
            .Take(take)
            .ToList();

        return ranked.Count > 0 ? ranked : Typos(g, leaf, take);
    }

    /// <summary>
    /// Fallback for names that share no whole word with anything, which is what a real typo looks
    /// like: Crednetial has no token in common with Credential.
    /// </summary>
    private static List<Hit> Typos(Graph g, string leaf, int take)
    {
        var hits = new List<Hit>();

        foreach (var node in g.Nodes)
        {
            if (node.Kind == "enum-member") continue;

            var candidate = Leaf(node.Short);

            // Edit distance is O(n*m); comparing wildly different lengths cannot pay off, and the
            // length gate is what keeps this affordable across a whole graph.
            if (Math.Abs(candidate.Length - leaf.Length) > 3) continue;
            if (candidate.Length == 0) continue;
            if (char.ToLowerInvariant(candidate[0]) != char.ToLowerInvariant(leaf[0])) continue;

            var distance = Distance(candidate, leaf);
            if (distance == 0 || distance > 3) continue;

            hits.Add(new Hit(node, 100 - distance * 20, "typo"));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Node.Short.Length)
            .Take(take)
            .ToList();
    }

    private static string Leaf(string s)
    {
        var dot = s.LastIndexOf('.');
        return dot < 0 ? s : s[(dot + 1)..];
    }

    private static string Owner(string s)
    {
        var dot = s.LastIndexOf('.');
        if (dot <= 0) return string.Empty;

        var head = s[..dot];
        var inner = head.LastIndexOf('.');
        return inner < 0 ? head : head[(inner + 1)..];
    }

    /// <summary>
    /// Splits an identifier the way a reader does: on case changes, digits and separators, so
    /// DeleteCredentialAsync becomes delete / credential / async.
    /// </summary>
    internal static HashSet<string> Tokens(string identifier)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(identifier)) return tokens;

        var current = new System.Text.StringBuilder();

        void Flush()
        {
            if (current.Length > 1) tokens.Add(current.ToString());
            current.Clear();
        }

        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];

            if (c is '_' or '-' or '.' or ' ')
            {
                Flush();
                continue;
            }

            // A capital starts a new word unless it is inside a run of capitals that is still
            // going, which keeps HttpClient as http / client rather than h / ttp / client.
            if (char.IsUpper(c) && current.Length > 0)
            {
                var previous = identifier[i - 1];
                var nextIsLower = i + 1 < identifier.Length && char.IsLower(identifier[i + 1]);
                if (char.IsLower(previous) || nextIsLower) Flush();
            }

            current.Append(char.ToLowerInvariant(c));
        }

        Flush();

        // A single-word identifier still needs to be comparable.
        if (tokens.Count == 0 && identifier.Length > 0) tokens.Add(identifier.ToLowerInvariant());

        return tokens;
    }

    /// <summary>Levenshtein distance, two rows rather than a full matrix.</summary>
    internal static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
