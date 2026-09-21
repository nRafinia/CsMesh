using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CsMesh.Analysis;

/// <summary>
/// What a csproj says it compiles, read as raw XML.
///
/// The split partitions files by owning project, so ownership has to match what a real build
/// compiles. The nearest-csproj rule is only the default: a project can remove a file from its
/// default glob (<c>&lt;Compile Remove&gt;</c>), turn the default glob off entirely
/// (<c>EnableDefaultCompileItems=false</c>) and list what it wants, or pull in a file from outside
/// its directory through a link or a shared <c>.projitems</c>. MSBuild evaluation is deliberately
/// not performed -- the same startup and AOT reasons <see cref="ProjectScope"/> reads XML -- so an
/// item that still contains an unresolved property after the one property we can name
/// (<c>$(MSBuildThisFileDirectory)</c>) is counted and left out rather than guessed at.
///
/// This type is parsing only. Until ownership reads it, the model changes nothing.
/// </summary>
internal sealed class CompileItemModel
{
    /// <summary>Whether the SDK's default <c>**/*.cs</c> glob applies. Defaults to true.</summary>
    public bool EnableDefaultCompileItems { get; set; } = true;

    /// <summary>Remove globs, relative to the project directory.</summary>
    public List<string> Removes { get; } = [];

    /// <summary>Include globs, relative to the project directory. A concrete path is a glob with no
    /// wildcard.</summary>
    public List<string> Includes { get; } = [];

    /// <summary>Raw item expressions that contain a property this parser does not evaluate.</summary>
    public List<string> Unevaluable { get; } = [];

    public int UnevaluableCount => Unevaluable.Count;

    /// <summary>True when the project's <c>&lt;Compile Remove&gt;</c> items name this file.</summary>
    public bool RemovesFile(string projectDirectory, string file) =>
        Removes.Any(pattern => CompileItems.Matches(pattern, Path.GetRelativePath(projectDirectory, file)));

    /// <summary>True when the project's <c>&lt;Compile Include&gt;</c> items name this file.</summary>
    public bool IncludesFile(string projectDirectory, string file) =>
        Includes.Any(pattern => CompileItems.Matches(pattern, Path.GetRelativePath(projectDirectory, file)));
}

internal static class CompileItems
{
    /// <summary>
    /// Reads a csproj and everything it imports as a shared project. Never throws: an unreadable
    /// project yields an empty model that leaves the default glob in force.
    /// </summary>
    public static CompileItemModel Parse(string csprojPath)
    {
        var model = new CompileItemModel();

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(csprojPath));
        if (projectDirectory is null) return model;

        XDocument document;
        try { document = XDocument.Parse(File.ReadAllText(csprojPath)); }
        catch { return model; }

        var enable = Value(document, "EnableDefaultCompileItems");
        if (enable is not null) model.EnableDefaultCompileItems = !enable.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

        Collect(document, projectDirectory, projectDirectory, model);

        foreach (var import in ImportPaths(document, projectDirectory, model))
        {
            if (!import.EndsWith(".projitems", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(import)) continue;

            XDocument shared;
            try { shared = XDocument.Parse(File.ReadAllText(import)); }
            catch { continue; }

            var sharedDirectory = Path.GetDirectoryName(import)!;
            Collect(shared, sharedDirectory, projectDirectory, model);
        }

        return model;
    }

    /// <summary>
    /// Collects the Compile items of one XML document. <paramref name="containingDirectory"/> is
    /// where relative paths are anchored and where <c>$(MSBuildThisFileDirectory)</c> points;
    /// <paramref name="projectDirectory"/> is where the patterns are stored relative to.
    /// </summary>
    private static void Collect(XDocument document, string containingDirectory, string projectDirectory, CompileItemModel model)
    {
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName != "Compile") continue;

            var remove = element.Attribute("Remove")?.Value;
            if (!string.IsNullOrWhiteSpace(remove))
            {
                var pattern = Resolve(remove, containingDirectory, projectDirectory, model);
                if (pattern is not null) model.Removes.Add(pattern);
            }

            var include = element.Attribute("Include")?.Value;
            if (!string.IsNullOrWhiteSpace(include))
            {
                var pattern = Resolve(include, containingDirectory, projectDirectory, model);
                if (pattern is not null) model.Includes.Add(pattern);
            }
        }
    }

    /// <summary>
    /// Import paths relative to the project. An Import whose path names a property this parser does
    /// not know is recorded as unevaluable; a plain relative path is returned even when the file is
    /// absent, so the caller can decide.
    /// </summary>
    private static IEnumerable<string> ImportPaths(XDocument document, string projectDirectory, CompileItemModel model)
    {
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName != "Import") continue;

            var project = element.Attribute("Project")?.Value;
            if (string.IsNullOrWhiteSpace(project)) continue;

            var resolved = StripThisFileDirectory(project, projectDirectory);
            if (resolved is null)
            {
                model.Unevaluable.Add(project);
                continue;
            }

            string full;
            try { full = Path.GetFullPath(Path.Combine(projectDirectory, resolved)); }
            catch { continue; }

            yield return full;
        }
    }

    /// <summary>
    /// Turns one item expression into a glob relative to the project directory, or records it as
    /// unevaluable and returns null. <c>$(MSBuildThisFileDirectory)</c> is the one property whose
    /// value is known without evaluating anything.
    /// </summary>
    private static string? Resolve(string expression, string containingDirectory, string projectDirectory, CompileItemModel model)
    {
        var relative = StripThisFileDirectory(expression, containingDirectory);
        if (relative is null)
        {
            model.Unevaluable.Add(expression);
            return null;
        }

        string absolute;
        try
        {
            absolute = Path.IsPathRooted(relative) ? relative : Path.Combine(containingDirectory, relative);
            return Normalize(Path.GetRelativePath(projectDirectory, absolute));
        }
        catch
        {
            model.Unevaluable.Add(expression);
            return null;
        }
    }

    /// <summary>
    /// Replaces <c>$(MSBuildThisFileDirectory)</c> with the containing file's directory. Returns
    /// null when any other property reference remains.
    /// </summary>
    private static string? StripThisFileDirectory(string expression, string containingDirectory)
    {
        // The property always carries a trailing separator, which is what makes
        // "$(MSBuildThisFileDirectory)Generated\File.cs" resolve under the current directory.
        var withSeparator = containingDirectory.EndsWith('\\') || containingDirectory.EndsWith('/')
            ? containingDirectory
            : containingDirectory + Path.DirectorySeparatorChar;

        var replaced = Regex.Replace(expression, @"\$\(MSBuildThisFileDirectory\)",
            _ => withSeparator, RegexOptions.IgnoreCase);

        return replaced.Contains("$(", StringComparison.Ordinal) ? null : replaced;
    }

    /// <summary>
    /// MSBuild's glob subset: <c>*</c> within a path segment, <c>?</c> for one character, and
    /// <c>**</c> across segments. Both slash directions are accepted; comparison is case-insensitive
    /// on Windows and case-sensitive elsewhere, as the filesystems are.
    /// </summary>
    public static bool Matches(string pattern, string relativePath)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        var options = RegexOptions.CultureInvariant;
        if (OperatingSystem.IsWindows()) options |= RegexOptions.IgnoreCase;

        try
        {
            return Regex.IsMatch(Normalize(relativePath), ToRegex(pattern), options);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string ToRegex(string pattern)
    {
        var normalized = Normalize(pattern);
        var builder = new StringBuilder("^");

        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == '*')
            {
                if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < normalized.Length && normalized[i + 1] == '/')
                    {
                        i++;
                        builder.Append("(?:.*/)?");   // zero or more whole segments
                    }
                    else
                    {
                        builder.Append(".*");          // anything, separators included
                    }
                }
                else
                {
                    builder.Append("[^/]*");           // anything within one segment
                }
            }
            else if (c == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(c.ToString()));
            }
        }

        return builder.Append('$').ToString();
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string? Value(XDocument document, string localName) =>
        document.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == localName)
            ?.Value;
}
