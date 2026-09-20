using System.Xml.Linq;

namespace CsMesh.Analysis;

/// <summary>
/// Which target framework a project's <c>obj/</c> and <c>bin/</c> directories should be read from,
/// and what implicit global usings the SDK would have generated for it.
///
/// The indexer used to read every target-framework directory it could find: a repository whose
/// project was retargeted from net8 to net10 keeps both trees under <c>obj/</c>, and compiling the
/// stale net8 global-using set alongside the net10 one imports namespaces the current build never
/// imports. The same is true of assemblies under <c>bin/</c>, where an old framework's copy of a
/// package can shadow the current one. Choosing one framework per project makes the index agree
/// with the build that produced the tree.
///
/// The csproj's own <c>TargetFramework</c> is the authority; only when that tree is absent do we
/// fall back to the newest one on disk, which is the same "newest build wins" instinct that keeps
/// a duplicate generated type out of the graph. Everything here reads raw XML rather than
/// evaluating MSBuild, for the same startup and AOT reasons <see cref="ProjectScope"/> does.
/// </summary>
internal static class ProjectTfm
{
    /// <summary>The framework declared by the csproj: <c>TargetFramework</c>, else the first of
    /// <c>TargetFrameworks</c>. Null when the project declares neither.</summary>
    public static string? Declared(string csprojPath)
    {
        XDocument document;
        try { document = XDocument.Parse(File.ReadAllText(csprojPath)); }
        catch { return null; }

        var single = Value(document, "TargetFramework");
        if (!string.IsNullOrWhiteSpace(single)) return single.Trim();

        var many = Value(document, "TargetFrameworks");
        if (string.IsNullOrWhiteSpace(many)) return null;

        return many.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .FirstOrDefault(t => t.Length > 0);
    }

    /// <summary>
    /// The framework directory under <paramref name="kind"/> ("obj" or "bin") to read for a project.
    /// The declared framework is preferred and kept whenever its directory exists; otherwise the
    /// newest framework directory on disk is used, because a project that was just retargeted has
    /// its output under the framework it was last built with, not the one it now declares.
    /// </summary>
    public static string? Choose(string projectDirectory, string kind)
    {
        var csproj = Single(projectDirectory);
        var declared = csproj is null ? null : Declared(csproj);

        if (declared is not null && FrameworkDirectories(projectDirectory, kind, declared).Any())
            return declared;

        return NewestFramework(projectDirectory, kind);
    }

    /// <summary>The csproj the project directory directly holds, or null.</summary>
    public static string? Single(string projectDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(projectDirectory, "*.csproj")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Framework-named directories directly under <c>{project}/{kind}/{config}</c>. Only the shape
    /// the SDK writes is accepted: a name with a dot, which excludes "ref", "generated", "Debug"
    /// and the other prose directories that share the level.
    /// </summary>
    private static IEnumerable<string> FrameworkDirectories(string projectDirectory, string kind, string framework)
    {
        var baseDir = Path.Combine(projectDirectory, kind);
        if (!Directory.Exists(baseDir)) yield break;

        foreach (var config in EnumerateDirectories(baseDir))
        {
            var candidate = Path.Combine(config, framework);
            if (Directory.Exists(candidate)) yield return candidate;
        }
    }

    /// <summary>The newest framework-named directory under <c>{project}/{kind}</c>, or null.</summary>
    private static string? NewestFramework(string projectDirectory, string kind)
    {
        var baseDir = Path.Combine(projectDirectory, kind);
        if (!Directory.Exists(baseDir)) return null;

        string? newest = null;
        var newestStamp = DateTime.MinValue;

        foreach (var config in EnumerateDirectories(baseDir))
        {
            foreach (var candidate in EnumerateDirectories(config))
            {
                var name = Path.GetFileName(candidate);
                if (!LooksLikeFramework(name)) continue;

                DateTime stamp;
                try { stamp = Directory.GetLastWriteTimeUtc(candidate); }
                catch { continue; }

                if (newest is not null && stamp <= newestStamp) continue;
                newest = name;
                newestStamp = stamp;
            }
        }

        return newest;
    }

    /// <summary>Configuration directories (Debug, Release, ...) directly under a base directory.</summary>
    public static IEnumerable<string> ConfigDirectories(string baseDirectory) =>
        EnumerateDirectories(baseDirectory);

    /// <summary>Whether a directory name is a target framework moniker.</summary>
    public static bool IsFrameworkName(string name) => LooksLikeFramework(name);

    /// <summary>A framework moniker such as net10.0, netstandard2.0 or net10.0-windows.</summary>
    private static bool LooksLikeFramework(string name) =>
        name.StartsWith("net", StringComparison.OrdinalIgnoreCase) && name.Contains('.');

    private static string? Value(XDocument document, string localName) =>
        document.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == localName)
            ?.Value;

    private static IEnumerable<string> EnumerateDirectories(string parent)
    {
        try { return Directory.EnumerateDirectories(parent); }
        catch { return []; }
    }
}
