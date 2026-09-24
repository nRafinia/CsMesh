using CsMesh.Models;

namespace CsMesh.Analysis;

/// <summary>
/// The <c>Name(param,param)</c> selector that separates overloads sharing one name.
///
/// Exit 3 lists two overloads of one member in one type and project, and --project cannot separate
/// them: same name, same project, even the same file. The name alone cannot say which one is meant,
/// but the key already carries the parameter types (<see cref="Indexer"/> Builder.Key), so the query
/// may name them. This parses that selector and filters Graph.Resolve's candidates by it; it adds no
/// new name grammar and nothing to the graph.
/// </summary>
public static class SymbolSelector
{
    public enum SelectorStatus
    {
        /// <summary>No parameter list; the query resolved as a plain name.</summary>
        Plain,

        /// <summary>A parameter list was given and at least one overload matched it.</summary>
        Matched,

        /// <summary>The name part resolved to nothing.</summary>
        NameNotFound,

        /// <summary>The name part resolved, but no overload matched the list.</summary>
        NoOverloadMatch,

        /// <summary>The query is not a well-formed selector.</summary>
        SyntaxError
    }

    public sealed record Result(
        SelectorStatus Status,
        string NamePart,
        List<Node> Matches,
        List<Node> NameMatches,
        string? Error);

    public static bool HasList(string query) => query.IndexOf('(') >= 0;

    /// <summary>
    /// Splits a query into its name part and parameter list, resolves the name, and filters the
    /// result by the list. The list is a selector, not a prefix filter: arity must match exactly,
    /// so a partial list matches nothing rather than silently picking the first overload.
    /// </summary>
    public static Result Analyze(Graph graph, string query)
    {
        var text = query.Trim();
        var open = text.IndexOf('(');

        if (open < 0)
        {
            var resolved = graph.Resolve(text);
            return new Result(resolved.Count == 0 ? SelectorStatus.NameNotFound : SelectorStatus.Plain,
                              text, resolved, resolved, null);
        }

        var namePart = text[..open].Trim();

        // The parser reads to the final ')'. What an unquoted "Type.Member(int, string)" leaves in
        // argv is "Type.Member(int," and its parameter tokens are separate positionals, so the
        // honest answer is a usage error naming the quoting remedy rather than a not-found.
        if (!text.EndsWith(')'))
        {
            var error = $"selector '{query}' has no closing ')'. Quote the whole selector so the shell "
                      + "passes it as one argument: csmesh <command> \"Type.Member(int, string)\"";
            return new Result(SelectorStatus.SyntaxError, namePart, [], [], error);
        }

        var userParams = SplitTopLevel(text[(open + 1)..^1]);
        var named = graph.Resolve(namePart);
        if (named.Count == 0)
            return new Result(SelectorStatus.NameNotFound, namePart, [], [], null);

        // Parameter types exist only on methods; a type or field named with a list never matches.
        var methods = named.Where(n => n.Kind == "method").ToList();

        // Strict over the whole set first, then lenient only when strict found nothing -- so a
        // selector that names a nullable overload keeps that overload even though a non-nullable
        // one would also accept the spurious '?' on its own.
        var matched = methods.Where(n => Matches(n.Key, userParams, strict: true)).ToList();
        if (matched.Count == 0)
            matched = methods.Where(n => Matches(n.Key, userParams, strict: false)).ToList();

        return new Result(matched.Count > 0 ? SelectorStatus.Matched : SelectorStatus.NoOverloadMatch,
                          namePart, matched, named, null);
    }

    /// <summary>The parameter tokens of a method key, or null when the key is not a method's.</summary>
    internal static IReadOnlyList<string>? ParamsFromKey(string key)
    {
        var lastBar = key.LastIndexOf('|');
        if (lastBar < 0) return null;

        var beforeKind = key[..lastBar];
        var secondLast = beforeKind.LastIndexOf('|');
        var identity = secondLast >= 0 ? beforeKind[(secondLast + 1)..] : beforeKind;

        var open = identity.IndexOf('(');
        if (open < 0 || !identity.EndsWith(')')) return null;

        return SplitTopLevel(identity[(open + 1)..^1]);
    }

    // ------------------------------------------------------------------ matching

    private static bool Matches(string key, IReadOnlyList<string> userParams, bool strict)
    {
        var keyParams = ParamsFromKey(key);
        if (keyParams is null || keyParams.Count != userParams.Count) return false;

        return Pass(userParams, keyParams, strict);
    }

    private static bool Pass(IReadOnlyList<string> user, IReadOnlyList<string> key, bool strict)
    {
        for (var i = 0; i < user.Count; i++)
        {
            if (!PositionMatches(user[i], key[i], strict)) return false;
        }

        return true;
    }

    private static bool PositionMatches(string user, string key, bool strict)
    {
        SplitModifier(user, out var userMod, out var userType);
        SplitModifier(key, out var keyMod, out var keyType);

        // ref/out/in are exact: a modifier on one side and not the other is no match.
        if (!string.Equals(userMod, keyMod, StringComparison.OrdinalIgnoreCase)) return false;

        return TypeMatches(ParseType(userType), ParseType(keyType), strict);
    }

    private static void SplitModifier(string token, out string modifier, out string type)
    {
        token = token.Trim();
        foreach (var candidate in Modifiers)
        {
            if (token.Length > candidate.Length + 1 &&
                token.StartsWith(candidate + " ", StringComparison.OrdinalIgnoreCase))
            {
                modifier = candidate;
                type = token[(candidate.Length + 1)..].Trim();
                return;
            }
        }

        modifier = "";
        type = token;
    }

    private static readonly string[] Modifiers = ["ref", "out", "in"];

    private sealed class TypeSig
    {
        public bool IsTuple;
        public List<TypeSig> Args = [];
        public List<string> Segments = [];
        public string ArraySuffix = "";
        public bool Nullable;
    }

    /// <summary>
    /// One parameter type into a shape the matcher can walk: array suffixes, nullable marker, tuple
    /// elements or generic arguments, and a namespace-qualified base name. `global::` and tuple
    /// element names are removed here so neither side can match or fail on furniture.
    /// </summary>
    private static TypeSig ParseType(string text)
    {
        text = text.Replace("global::", "").Trim();
        var sig = new TypeSig();

        while (true)
        {
            text = text.Trim();
            if (text.EndsWith('?'))
            {
                sig.Nullable = true;
                text = text[..^1].TrimEnd();
                continue;
            }

            if (text.EndsWith(']'))
            {
                var bracket = text.LastIndexOf('[');
                if (bracket < 0) break;
                sig.ArraySuffix = text[bracket..] + sig.ArraySuffix;
                text = text[..bracket].TrimEnd();
                continue;
            }

            break;
        }

        if (text.StartsWith('(') && text.EndsWith(')'))
        {
            sig.IsTuple = true;
            foreach (var element in SplitTopLevel(text[1..^1]))
            {
                sig.Args.Add(ParseType(StripElementName(element)));
            }

            return sig;
        }

        var generic = TopLevelIndexOf(text, '<');
        if (generic >= 0 && text.EndsWith('>'))
        {
            sig.Segments = Segments(text[..generic]);
            foreach (var argument in SplitTopLevel(text[(generic + 1)..^1]))
            {
                sig.Args.Add(ParseType(argument));
            }
        }
        else
        {
            sig.Segments = Segments(text);
        }

        return sig;
    }

    private static bool TypeMatches(TypeSig user, TypeSig key, bool strict)
    {
        if (!string.Equals(Normalize(user.ArraySuffix), Normalize(key.ArraySuffix), StringComparison.OrdinalIgnoreCase))
            return false;

        if (user.Nullable != key.Nullable)
        {
            // Lenient pass tolerates only a user '?' the key does not have; a key '?' the user
            // omitted is never forgiven.
            if (strict || !user.Nullable) return false;
        }

        if (user.IsTuple != key.IsTuple) return false;
        if (user.Args.Count != key.Args.Count) return false;

        for (var i = 0; i < user.Args.Count; i++)
        {
            if (!TypeMatches(user.Args[i], key.Args[i], strict)) return false;
        }

        return user.IsTuple || SegmentsMatch(user.Segments, key.Segments);
    }

    /// <summary>
    /// The user's name is a whole-token suffix of the key's, case-insensitively, after mapping the
    /// C# keyword to its BCL name on both sides. The suffix is what lets <c>List&lt;int&gt;</c> match
    /// <c>System.Collections.Generic.List&lt;int&gt;</c> without letting <c>List&lt;int&gt;</c> match
    /// <c>List&lt;string&gt;</c> -- the generic arguments are compared, not the text.
    /// </summary>
    private static bool SegmentsMatch(IReadOnlyList<string> user, IReadOnlyList<string> key)
    {
        var u = CanonicalSegments(user);
        var k = CanonicalSegments(key);
        if (u.Count == 0 || u.Count > k.Count) return false;

        var offset = k.Count - u.Count;
        for (var i = 0; i < u.Count; i++)
        {
            if (!string.Equals(u[i], k[offset + i], StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private static List<string> CanonicalSegments(IReadOnlyList<string> segments)
    {
        var mapped = segments.Select(s => Aliases.TryGetValue(s, out var bcl) ? bcl : s).ToList();
        if (mapped.Count > 1 && string.Equals(mapped[0], "System", StringComparison.OrdinalIgnoreCase))
            mapped.RemoveAt(0);
        return mapped;
    }

    /// <summary>The C# keyword to BCL name for every type SymbolDisplayFormat's UseSpecialTypes maps.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["object"] = "Object", ["bool"] = "Boolean", ["char"] = "Char", ["sbyte"] = "SByte",
        ["byte"] = "Byte", ["short"] = "Int16", ["ushort"] = "UInt16", ["int"] = "Int32",
        ["uint"] = "UInt32", ["long"] = "Int64", ["ulong"] = "UInt64", ["decimal"] = "Decimal",
        ["float"] = "Single", ["double"] = "Double", ["string"] = "String", ["void"] = "Void",
        ["nint"] = "IntPtr", ["nuint"] = "UIntPtr"
    };

    // ------------------------------------------------------------------ printing

    /// <summary>
    /// The selector for one node: its name and the parameter list with short type names and no
    /// spaces after commas. A node without a method key prints its name alone.
    /// </summary>
    public static string SelectorFor(Node node)
    {
        var parameters = ParamsFromKey(node.Key);
        return parameters is null
            ? node.Name
            : node.Name + "(" + string.Join(",", parameters.Select(ShortParam)) + ")";
    }

    /// <summary>
    /// The selectors for a candidate list, disambiguated so no two are identical: where short type
    /// names leave two candidates looking the same, the differing positions are printed qualified
    /// (namespaces, without `global::`) until the selectors differ. Every returned selector parsed
    /// back resolves to exactly the node it was printed for.
    /// </summary>
    public static List<string> SelectorsFor(IReadOnlyList<Node> nodes)
    {
        var keys = nodes.Select(n => ParamsFromKey(n.Key)).ToList();
        var result = new string[nodes.Count];

        foreach (var group in Enumerable.Range(0, nodes.Count).GroupBy(i => ShortSelectorOf(nodes[i])))
        {
            var indices = group.ToList();
            if (indices.Count == 1)
            {
                result[indices[0]] = ShortSelectorOf(nodes[indices[0]]);
                continue;
            }

            var arity = keys[indices[0]]!.Count;
            var qualified = new bool[arity];

            while (true)
            {
                var rendered = indices
                    .Select(i => (Index: i, Text: Signature(nodes[i], keys[i]!, qualified)))
                    .ToList();

                var duplicate = rendered
                    .GroupBy(x => x.Text, StringComparer.Ordinal)
                    .FirstOrDefault(g => g.Count() > 1);
                if (duplicate is null) break;

                var pair = duplicate.ToList();
                var a = keys[pair[0].Index]!;
                var b = keys[pair[1].Index]!;

                var progressed = false;
                for (var p = 0; p < arity; p++)
                {
                    if (qualified[p]) continue;
                    if (!string.Equals(ShortParam(a[p]), ShortParam(b[p]), StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(QualParam(a[p]), QualParam(b[p]), StringComparison.OrdinalIgnoreCase))
                    {
                        // Short already tells them apart; leaving this position short is enough.
                        continue;
                    }

                    if (string.Equals(ShortParam(a[p]), ShortParam(b[p]), StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(QualParam(a[p]), QualParam(b[p]), StringComparison.OrdinalIgnoreCase))
                    {
                        qualified[p] = true;
                        progressed = true;
                        break;
                    }
                }

                // Two distinct keys cannot render alike once every differing position is qualified;
                // the guard only stops a malformed pair from looping forever.
                if (!progressed) break;
            }

            foreach (var i in indices)
            {
                result[i] = Signature(nodes[i], keys[i]!, qualified);
            }
        }

        return result.ToList();
    }

    private static string ShortSelectorOf(Node node)
    {
        var parameters = ParamsFromKey(node.Key);
        return parameters is null
            ? node.Name
            : node.Name + "(" + string.Join(",", parameters.Select(ShortParam)) + ")";
    }

    private static string Signature(Node node, IReadOnlyList<string> parameters, bool[] qualified) =>
        node.Name + "(" + string.Join(",", parameters.Select((token, i) =>
            qualified[i] ? QualParam(token) : ShortParam(token))) + ")";

    private static string ShortParam(string token)
    {
        SplitModifier(token, out var modifier, out var type);
        return (modifier.Length > 0 ? modifier + " " : "") + Render(ParseType(type), qualified: false);
    }

    private static string QualParam(string token)
    {
        SplitModifier(token, out var modifier, out var type);
        return (modifier.Length > 0 ? modifier + " " : "") + Render(ParseType(type), qualified: true);
    }

    private static string Render(TypeSig sig, bool qualified)
    {
        string body;
        if (sig.IsTuple)
        {
            body = "(" + string.Join(",", sig.Args.Select(a => Render(a, qualified))) + ")";
        }
        else
        {
            var name = qualified
                ? string.Join(".", sig.Segments)
                : sig.Segments.Count > 0 ? sig.Segments[^1] : "";
            body = sig.Args.Count > 0
                ? name + "<" + string.Join(",", sig.Args.Select(a => Render(a, qualified))) + ">"
                : name;
        }

        return body + sig.ArraySuffix + (sig.Nullable ? "?" : "");
    }

    // ------------------------------------------------------------------ text parsing

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        if (text.Trim().Length == 0) return parts;

        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(text[start..].Trim());
        return parts.Where(p => p.Length > 0).ToList();
    }

    private static int TopLevelIndexOf(string text, char want)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (depth == 0 && c == want) return i;
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
        }

        return -1;
    }

    private static List<string> Segments(string name) =>
        name.Replace(" ", "").Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>
    /// Removes a tuple element's name: a bare identifier after the last top-level space, where the
    /// space is not inside a nested generic, tuple or array.
    /// </summary>
    private static string StripElementName(string element)
    {
        element = element.Trim();

        var depth = 0;
        var lastSpace = -1;
        for (var i = 0; i < element.Length; i++)
        {
            var c = element[i];
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            else if (depth == 0 && char.IsWhiteSpace(c)) lastSpace = i;
        }

        if (lastSpace <= 0) return element;

        var tail = element[(lastSpace + 1)..].Trim();
        return IsIdentifier(tail) ? element[..lastSpace].Trim() : element;
    }

    private static bool IsIdentifier(string s) =>
        s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') &&
        s.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static string Normalize(string s) => s.Replace(" ", "");
}
