using System.Xml.Linq;

namespace CsMesh.Analysis;

/// <summary>
/// The assemblies a project grants access to its internals, read as raw XML.
///
/// <c>InternalsVisibleTo</c> is an assembly-level attribute, so a friend reads the internal types of
/// the project that names it. The single whole-solution compilation made internals visible to
/// everything without the attribute mattering; one compilation per project does not, and a test or
/// a sibling assembly that reads an internal through the friend relationship stops binding. The SDK
/// turns these csproj items into the attribute at build time, so the source compilation has to be
/// handed the same text or the relationship does not exist.
///
/// MSBuild evaluation is deliberately not performed, as elsewhere: the one property whose value is
/// knowable without evaluation is not even useful here, and an item that names another property is
/// counted unevaluable rather than guessed at.
/// </summary>
internal static class InternalsVisibleTo
{
    /// <summary>
    /// Friend assembly names, and how many items named a property this parser does not evaluate.
    /// Two csproj spellings are read: the SDK item <c>&lt;InternalsVisibleTo Include="X" /&gt;</c>
    /// and the older <c>&lt;AssemblyAttribute&gt;</c> form whose <c>_Parameter1</c> is the friend.
    /// The value is kept whole, so a name carrying a public key is preserved.
    /// </summary>
    public static (List<string> Friends, int Unevaluable) Read(string csprojPath)
    {
        var friends = new List<string>();
        var unevaluable = 0;

        XDocument document;
        try { document = XDocument.Parse(File.ReadAllText(csprojPath)); }
        catch { return (friends, unevaluable); }

        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName == "InternalsVisibleTo")
            {
                Add(element.Attribute("Include")?.Value, friends, ref unevaluable);
            }
            else if (element.Name.LocalName == "AssemblyAttribute")
            {
                var include = element.Attribute("Include")?.Value;
                if (include is null || !include.Contains("InternalsVisibleTo", StringComparison.Ordinal)) continue;

                var parameter = element.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "_Parameter1")?.Value;
                Add(parameter, friends, ref unevaluable);
            }
        }

        return (friends, unevaluable);
    }

    /// <summary>
    /// A friend name as it must appear in the attribute to match the friend's compilation.
    ///
    /// IVT is matched on the friend's simple assembly name, which for a source compilation is the
    /// name it was created with. A project whose real name was disambiguated (two projects share
    /// it) therefore has to be named by that compilation name, not the real one the csproj wrote.
    /// The map holds only real names that pick out exactly one in-scope project; anything else --
    /// an external friend, or an ambiguous one -- keeps the written text.
    /// </summary>
    public static string Resolve(string friend, IReadOnlyDictionary<string, string> compilationNameByRealName)
    {
        var comma = friend.IndexOf(',');
        var simple = (comma < 0 ? friend : friend[..comma]).Trim();
        var suffix = comma < 0 ? "" : friend[comma..];

        return compilationNameByRealName.TryGetValue(simple, out var compilationName)
            ? compilationName + suffix
            : friend;
    }

    private static void Add(string? value, List<string> friends, ref int unevaluable)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var trimmed = value.Trim();
        if (trimmed.Contains("$(", StringComparison.Ordinal))
        {
            unevaluable++;
            return;
        }

        friends.Add(trimmed);
    }
}
