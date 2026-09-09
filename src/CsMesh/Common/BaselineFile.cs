namespace CsMesh.Common;

/// <summary>
/// The accepted-findings baseline written by 'csmesh review --accept', at .csmesh/accepted.txt.
///
/// One finding per line, keyed by the hash 'review' derives from the edge itself so the file
/// survives edits that have nothing to do with the finding. The rest of the line is prose for
/// whoever reviews the file in a pull request -- a baseline nobody can read gets accepted
/// wholesale, which defeats the point of having one.
/// </summary>
public static class BaselineFile
{
    public static string PathFor(string root) => Path.Combine(root, ".csmesh", "accepted.txt");

    /// <summary>The set of accepted finding ids. Blank lines and '#' comments are ignored.</summary>
    public static HashSet<string> Load(string root)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var path = PathFor(root);
        if (!File.Exists(path)) return ids;

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            var space = trimmed.IndexOf(' ');
            ids.Add(space < 0 ? trimmed : trimmed[..space]);
        }

        return ids;
    }

    /// <summary>
    /// Replaces the baseline with exactly the given entries (id plus the human-readable text that
    /// follows it on the line). Returns how many previously accepted ids were dropped because they
    /// no longer match anything -- the base moved forward and the finding they described is dead.
    /// </summary>
    public static int Save(string root, IEnumerable<(string Id, string Text)> entries)
    {
        var previous = Load(root);
        var list = entries.ToList();
        var currentIds = list.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var pruned = previous.Count(id => !currentIds.Contains(id));

        var path = PathFor(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var lines = new List<string>
        {
            "# csmesh review baseline -- one accepted finding per line.",
            "# the hash is what 'review' matches on; the rest is here for a human reading the diff.",
            "# regenerate with: csmesh review --accept",
            ""
        };
        lines.AddRange(list.Select(e => $"{e.Id}  {e.Text}"));

        File.WriteAllLines(path, lines);
        return pruned;
    }
}
