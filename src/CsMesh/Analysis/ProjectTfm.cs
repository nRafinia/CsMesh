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

    /// <summary>
    /// Whether the SDK would emit a global-usings file for this project at all. False means the
    /// project either does not opt in or has no csproj to say so, and no implicit set is synthesized.
    /// </summary>
    public static bool ImplicitUsingsEnabled(string csprojPath)
    {
        XDocument document;
        try { document = XDocument.Parse(File.ReadAllText(csprojPath)); }
        catch { return false; }

        var value = Value(document, "ImplicitUsings");
        return value is not null &&
               (value.Equals("enable", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase));
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
    /// Whether the project builds with the Web SDK, whose implicit usings include the ASP.NET Core
    /// namespaces the plain SDK does not add.
    /// </summary>
    public static bool IsWebSdk(string csprojPath)
    {
        string text;
        try { text = File.ReadAllText(csprojPath); }
        catch { return false; }

        return text.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The global usings the SDK would generate for one project, when no build wrote the generated
    /// file. The base set is what every SDK adds when ImplicitUsings is on; the ASP.NET Core
    /// namespaces are added only for the Web SDK, so a plain project is not handed types its real
    /// build cannot see.
    ///
    /// The csproj's own &lt;Using&gt; items are part of the same generated file, so they are
    /// reconstructed here too: an Include becomes a plain global using, an Alias a name for it, and
    /// Static a static import; a Remove drops an implicit or explicit using again. This matters even
    /// when ImplicitUsings is off -- a test project that only lists &lt;Using Include="Xunit" /&gt;
    /// still gets that using from the SDK. An item naming another property is counted unevaluable
    /// and left out rather than guessed at.
    /// </summary>
    public static string Synthesize(string csprojPath) => Synthesize(csprojPath, out _);

    public static string Synthesize(string csprojPath, out int unevaluable)
    {
        unevaluable = 0;

        XDocument document;
        try { document = XDocument.Parse(File.ReadAllText(csprojPath)); }
        catch { return string.Empty; }

        // (target, directive); target is what a <Using Remove> matches against.
        var entries = new List<(string Target, string Directive)>();

        if (ImplicitUsingsEnabled(csprojPath))
        {
            foreach (var ns in BaseNamespaces(csprojPath))
                entries.Add((ns, $"global using global::{ns};"));
        }

        var removals = new List<string>();
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName != "Using") continue;

            var remove = element.Attribute("Remove")?.Value;
            if (!string.IsNullOrWhiteSpace(remove))
            {
                if (HasProperty(remove)) { unevaluable++; continue; }
                removals.Add(remove.Trim());
                continue;
            }

            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include)) continue;

            var alias = element.Attribute("Alias")?.Value;
            if (HasProperty(include) || (alias is not null && HasProperty(alias)))
            {
                unevaluable++;
                continue;
            }

            var target = include.Trim();
            if (!string.IsNullOrWhiteSpace(alias))
                entries.Add((alias.Trim(), $"global using {alias.Trim()} = global::{target};"));
            else if (IsTrue(element.Attribute("Static")?.Value))
                entries.Add((target, $"global using static global::{target};"));
            else
                entries.Add((target, $"global using global::{target};"));
        }

        entries.RemoveAll(entry => removals.Contains(entry.Target, StringComparer.Ordinal));

        return string.Join("\n", entries.Select(entry => entry.Directive));
    }

    /// <summary>The namespaces every SDK adds when ImplicitUsings is on, plus the Web SDK's.</summary>
    private static List<string> BaseNamespaces(string csprojPath)
    {
        var namespaces = new List<string>
        {
            "System",
            "System.Collections.Generic",
            "System.IO",
            "System.Linq",
            "System.Net.Http",
            "System.Threading",
            "System.Threading.Tasks"
        };

        if (IsWebSdk(csprojPath))
        {
            namespaces.AddRange(
            [
                "Microsoft.AspNetCore.Builder",
                "Microsoft.AspNetCore.Hosting",
                "Microsoft.AspNetCore.Http",
                "Microsoft.AspNetCore.Routing",
                "Microsoft.Extensions.Configuration",
                "Microsoft.Extensions.DependencyInjection",
                "Microsoft.Extensions.Hosting",
                "Microsoft.Extensions.Logging",
                "System.Net.Http.Json"
            ]);
        }

        return namespaces;
    }

    private static bool HasProperty(string value) => value.Contains("$(", StringComparison.Ordinal);

    private static bool IsTrue(string? value) => value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("enable", StringComparison.OrdinalIgnoreCase));

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
