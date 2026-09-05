using System.Xml.Linq;
using CsMesh.Common;

namespace CsMesh.Analysis;

/// <summary>
/// Which projects in a repository are actually part of the build.
///
/// A tree can hold source that nothing compiles: a project removed from the solution but left on
/// disk, a spike, a vendored copy. Indexing it is not harmless. Its types compete in symbol
/// lookups, its registrations are reported as if the container used them, and its unresolved
/// references drag the quality numbers down -- all with no way for the reader to tell that half
/// the noise comes from code that never runs.
///
/// Everything here is read as raw XML. Evaluating MSBuild would be more accurate and would cost
/// the startup time and the AOT compatibility that make this tool worth using.
/// </summary>
public sealed class ProjectScope
{
    private readonly HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);

    private ProjectScope(string root, IEnumerable<string> live, IEnumerable<string> excluded,
                         string reason, string decision)
    {
        Root = root;
        Reason = reason;
        Decision = decision;
        Excluded = excluded.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        var paths = live.ToList();
        foreach (var path in paths) _live.Add(Directory.GetParent(path)!.FullName);

        var nameByPath = paths.ToDictionary(
            Path.GetFullPath,
            Path.GetFileNameWithoutExtension,
            StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var targets = ReferencedProjects(path)
                .Select(r => nameByPath.GetValueOrDefault(r))
                .Where(n => n != null && n != name)
                .Select(n => n!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            References[name] = targets;
        }
    }

    public string Root { get; }

    /// <summary>How the decision was made, for reporting. Empty when nothing was excluded.</summary>
    public string Reason { get; }

    /// <summary>
    /// What decided the scope, always populated. Reported whether or not anything was excluded:
    /// a filter that quietly fell back to including everything looks identical to a filter that
    /// had nothing to exclude, and one of those is a bug.
    /// </summary>
    public string Decision { get; }

    /// <summary>Project files left out, relative to the root.</summary>
    public List<string> Excluded { get; }

    /// <summary>
    /// Declared ProjectReference edges between the projects in scope, by project name.
    ///
    /// Read from the csproj rather than inferred from symbol edges. Inferring it got the direction
    /// backwards: an interface edge runs from the abstraction to the implementation, which is what
    /// runs at dispatch but the opposite of what depends on what. A layer never depends on the
    /// layer above it, and a map that says otherwise is not worth reading.
    /// </summary>
    public Dictionary<string, List<string>> References { get; } = new(StringComparer.Ordinal);

    public bool ExcludesAnything => Excluded.Count > 0;

    /// <summary>
    /// Directories of the projects being compiled from source, one per project in scope. Used to
    /// keep their own build output out of the reference set.
    /// </summary>
    public IReadOnlyCollection<string> LiveDirectories => _live;

    /// <summary>
    /// True when a file belongs to a project that is part of the build, or to no project at all.
    /// A loose .cs file with no csproj above it is kept: there is nothing to judge it by, and
    /// silently dropping it would be worse than including it.
    /// </summary>
    public bool Includes(string file)
    {
        if (_live.Count == 0) return true;

        var dir = Directory.GetParent(file);
        var stop = Path.GetFullPath(Root);
        var sawProject = false;

        while (dir != null && dir.FullName.StartsWith(stop, StringComparison.OrdinalIgnoreCase))
        {
            if (_live.Contains(dir.FullName)) return true;

            try
            {
                if (dir.EnumerateFiles("*.csproj").Any()) sawProject = true;
            }
            catch
            {
                // Unreadable directory: treat as no project and let the file through.
            }

            dir = dir.Parent;
        }

        return !sawProject;
    }

    /// <summary>
    /// Everything, with no filtering. Used by --all and when no project files exist.
    ///
    /// The project list is still collected even though nothing is excluded by it. Passing an empty
    /// one left <see cref="LiveDirectories"/> empty, so no build output was kept out of the
    /// reference set and every one of the repository's own types was in the compilation twice --
    /// exactly the state the filtered path had just been fixed out of. --all widens what is
    /// indexed; it does not mean nothing is known about what is being indexed.
    /// </summary>
    public static ProjectScope Everything(string root, string decision = "no project files; indexing every source file")
    {
        List<string> projects;
        try
        {
            projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !p.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                .Where(p => !p.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            projects = [];
        }

        return new ProjectScope(root, projects, [], "", decision);
    }

    public static ProjectScope Discover(string root)
    {
        List<string> projects;
        try
        {
            projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !p.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                .Where(p => !p.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return Everything(root, "project files could not be listed; indexing everything");
        }

        if (projects.Count == 0) return Everything(root);

        // A solution file is the author saying which projects are in. Nothing inferred can beat
        // that, so it is tried first and used alone when it produces anything.
        var fromSolution = FromSolutions(root, projects);
        if (fromSolution != null) return fromSolution;

        return FromReferenceClosure(root, projects);
    }

    // ------------------------------------------------------------------ solution files

    private static ProjectScope? FromSolutions(string root, List<string> projects)
    {
        // Only the shallowest solutions get a vote. A vendored library or submodule under
        // src/Shared carries its own .slnx listing its own projects, and letting that union with
        // the top-level solution puts every one of them back in scope -- which is exactly the code
        // the top-level solution left out.
        var all = SolutionFiles(root).ToList();
        if (all.Count == 0) return null;

        var shallowest = all.Min(s => Depth(root, s));
        var chosen = all.Where(s => Depth(root, s) == shallowest).ToList();

        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var solutions = new List<string>();

        foreach (var solution in chosen)
        {
            solutions.Add(Path.GetFileName(solution));
            foreach (var path in ProjectPathsIn(solution))
            {
                try
                {
                    named.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(solution)!, path)));
                }
                catch
                {
                    // A malformed path entry is not worth failing an index over.
                }
            }
        }

        if (named.Count == 0)
        {
            Dbg.Log("solution files listed no projects; falling back to the reference closure");
            return null;
        }

        var live = projects.Where(p => named.Contains(Path.GetFullPath(p))).ToList();
        if (live.Count == 0)
        {
            // Every listed path failed to match something on disk. Usually a relative path this
            // parser resolved differently; either way, guessing from here would be worse.
            Dbg.Log($"none of the {named.Count} project(s) named in {string.Join(", ", solutions)} " +
                    "matched a file on disk; falling back to the reference closure");
            return null;
        }

        var excluded = projects.Except(live, StringComparer.OrdinalIgnoreCase)
            .Select(p => Path.GetRelativePath(root, p));

        var scope = new ProjectScope(root, live, excluded,
            $"not listed in {string.Join(", ", solutions)}",
            $"{string.Join(", ", solutions)}: {live.Count} of {projects.Count} project(s) in scope");
        Dbg.Log($"scope: {string.Join(", ", solutions)} -> {live.Count} of {projects.Count} project(s)");
        return scope;
    }

    private static int Depth(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        return relative.Count(c => c == '/');
    }

    private static IEnumerable<string> SolutionFiles(string root)
    {
        foreach (var pattern in new[] { "*.slnx", "*.sln" })
        {
            IEnumerable<string> found;
            try { found = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var file in found)
            {
                var normalized = file.Replace('\\', '/');
                if (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)) continue;
                if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)) continue;
                yield return file;
            }
        }
    }

    /// <summary>
    /// .slnx is XML with Project/@Path. .sln is the older text format where each project line
    /// carries the path as the second quoted field.
    /// </summary>
    private static IEnumerable<string> ProjectPathsIn(string solution)
    {
        if (solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            XDocument document;
            try { document = XDocument.Load(solution); }
            catch (Exception ex)
            {
                Dbg.Log($"could not read {solution}: {ex.Message}");
                yield break;
            }

            foreach (var element in document.Descendants("Project"))
            {
                var path = element.Attribute("Path")?.Value;
                if (!string.IsNullOrWhiteSpace(path)) yield return path;
            }

            yield break;
        }

        string[] lines;
        try { lines = File.ReadAllLines(solution); }
        catch { yield break; }

        foreach (var line in lines)
        {
            if (!line.StartsWith("Project(", StringComparison.Ordinal)) continue;

            var quoted = line.Split('"');
            // Project("{guid}") = "Name", "relative\path.csproj", "{guid}"
            if (quoted.Length < 6) continue;

            var path = quoted[5];
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) yield return path;
        }
    }

    // ------------------------------------------------------------------ reference closure

    /// <summary>
    /// Without a solution, walk out from the things that are built for their own sake: executables
    /// and test projects. Test projects are roots because nothing references a test project, and
    /// dropping them would take the test callers out of every blast radius.
    /// </summary>
    private static ProjectScope FromReferenceClosure(string root, List<string> projects)
    {
        var references = projects.ToDictionary(
            p => Path.GetFullPath(p),
            ReferencedProjects,
            StringComparer.OrdinalIgnoreCase);

        var roots = projects.Where(IsRoot).Select(Path.GetFullPath).ToList();
        if (roots.Count == 0)
        {
            Dbg.Log("no executable or test project found; indexing everything");
            return Everything(root, "no executable or test project found; indexing everything");
        }

        var reached = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        var frontier = new Queue<string>(roots);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var next in references.GetValueOrDefault(current, []))
            {
                if (reached.Add(next)) frontier.Enqueue(next);
            }
        }

        var live = projects.Where(p => reached.Contains(Path.GetFullPath(p))).ToList();
        var excluded = projects.Except(live, StringComparer.OrdinalIgnoreCase)
            .Select(p => Path.GetRelativePath(root, p));

        Dbg.Log($"scope: ProjectReference closure from {roots.Count} root(s) -> " +
                $"{live.Count} of {projects.Count} project(s)");

        return new ProjectScope(root, live, excluded,
            "not reachable by ProjectReference from any executable or test project",
            $"ProjectReference closure from {roots.Count} root(s): {live.Count} of {projects.Count} project(s) in scope");
    }

    private static bool IsRoot(string csproj)
    {
        var text = ReadOrEmpty(csproj);
        if (text.Length == 0) return true;

        if (text.Contains("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains("<OutputType>WinExe</OutputType>", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains("<IsTestProject>true</IsTestProject>", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static List<string> ReferencedProjects(string csproj)
    {
        var result = new List<string>();
        var directory = Path.GetDirectoryName(csproj)!;

        XDocument document;
        try { document = XDocument.Parse(ReadOrEmpty(csproj)); }
        catch { return result; }

        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName != "ProjectReference") continue;

            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include)) continue;

            try
            {
                result.Add(Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar))));
            }
            catch
            {
                // Ignore an unusable path rather than abandon the project.
            }
        }

        return result;
    }

    private static string ReadOrEmpty(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }
}