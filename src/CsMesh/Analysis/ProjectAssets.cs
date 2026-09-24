using System.Text.Json;

namespace CsMesh.Analysis;

/// <summary>
/// Reads one project's <c>obj/project.assets.json</c>: the compile assets restore resolved for it,
/// and its project dependencies.
///
/// The reference set used to come only from <c>bin/</c>, and a class library does not copy its
/// package closure there, so a package type bound only if some app in the tree happened to copy it.
/// The assets file is the compiler's own input for the same question: restore writes, per target
/// framework, the exact assembly each package compiles against. Reading it makes a restored tree
/// sufficient even when <c>bin/</c> is empty, and gives each project its own package closure rather
/// than the union every <c>bin/</c> happened to contain.
///
/// Parsed with <see cref="JsonDocument"/>, which is part of System.Text.Json and needs no
/// reflection or source-generated model, so Native AOT is unaffected and no package is added. Every
/// failure is a <c>false</c> return: the caller falls back to the historic <c>bin/</c> walk for that
/// project rather than failing the index.
/// </summary>
internal static class ProjectAssets
{
    /// <summary>A project dependency named by the assets file: its assembly name and directory.</summary>
    public sealed record ProjectDependency(string AssemblyName, string Directory);

    /// <summary>The compile assets of one project and its project dependencies.</summary>
    public sealed record Entry(
        List<string> CompileFiles,
        List<string> MissingAssemblyNames,
        List<ProjectDependency> ProjectDependencies);

    /// <summary>The deterministic location of a project's assets file.</summary>
    public static string PathFor(string projectDirectory) =>
        Path.Combine(projectDirectory, "obj", "project.assets.json");

    /// <summary>
    /// Reads the assets file for <paramref name="projectDirectory"/> at the RID-less target for
    /// <paramref name="tfm"/>. Returns false when the file is absent, unparsable, or has no target
    /// for that framework and no RID-less target at all.
    /// </summary>
    public static bool TryRead(string projectDirectory, string? tfm, out Entry entry)
    {
        entry = new Entry([], [], []);

        var path = PathFor(projectDirectory);
        if (!File.Exists(path)) return false;

        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllBytes(path)); }
        catch { return false; }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!TryTarget(root, tfm, out var target)) return false;

            var folders = new List<string>();
            if (root.TryGetProperty("packageFolders", out var packageFolders) &&
                packageFolders.ValueKind == JsonValueKind.Object)
            {
                foreach (var folder in packageFolders.EnumerateObject()) folders.Add(folder.Name);
            }

            var libraryPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("libraries", out var libraries) &&
                libraries.ValueKind == JsonValueKind.Object)
            {
                foreach (var library in libraries.EnumerateObject())
                {
                    if (library.Value.ValueKind == JsonValueKind.Object &&
                        library.Value.TryGetProperty("path", out var libraryPath) &&
                        libraryPath.ValueKind == JsonValueKind.String)
                    {
                        libraryPaths[library.Name] = libraryPath.GetString()!;
                    }
                }
            }

            foreach (var item in target.EnumerateObject())
            {
                var type = item.Value.ValueKind == JsonValueKind.Object &&
                           item.Value.TryGetProperty("type", out var kind)
                    ? kind.GetString()
                    : null;

                if (type == "project")
                {
                    if (libraryPaths.TryGetValue(item.Name, out var projectRelative))
                    {
                        var csproj = Path.GetFullPath(Path.Combine(projectDirectory, projectRelative));
                        var dependencyDirectory = Path.GetDirectoryName(csproj);
                        if (!string.IsNullOrEmpty(dependencyDirectory))
                        {
                            var slash = item.Name.IndexOf('/');
                            var assemblyName = slash < 0 ? item.Name : item.Name[..slash];
                            entry.ProjectDependencies.Add(new ProjectDependency(assemblyName, dependencyDirectory));
                        }
                    }

                    continue;
                }

                if (type != "package") continue;
                if (!item.Value.TryGetProperty("compile", out var compile) ||
                    compile.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var asset in compile.EnumerateObject())
                {
                    var key = asset.Name;
                    if (key.EndsWith("_._", StringComparison.Ordinal)) continue;

                    var slash = key.LastIndexOf('/');
                    var fileName = slash < 0 ? key : key[(slash + 1)..];

                    if (libraryPaths.TryGetValue(item.Name, out var libraryRelative) &&
                        TryResolve(folders, libraryRelative, key, out var fullPath))
                    {
                        entry.CompileFiles.Add(fullPath);
                    }
                    else
                    {
                        entry.MissingAssemblyNames.Add(fileName);
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The RID-less target for <paramref name="tfm"/>. A RID-specific key (<c>tfm/rid</c>) is never
    /// chosen: the build compiles against the RID-less asset, and a runtime-specific one can differ.
    /// </summary>
    private static bool TryTarget(JsonElement root, string? tfm, out JsonElement target)
    {
        target = default;
        if (!root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(tfm))
        {
            return targets.TryGetProperty(tfm, out target) && target.ValueKind == JsonValueKind.Object;
        }

        foreach (var candidate in targets.EnumerateObject())
        {
            if (candidate.Name.Contains('/')) continue;
            target = candidate.Value;
            return target.ValueKind == JsonValueKind.Object;
        }

        return false;
    }

    /// <summary>
    /// The first existing file across <paramref name="folders"/>, in the order the assets file lists
    /// them, of library <c>path</c> joined with the compile key.
    /// </summary>
    private static bool TryResolve(
        List<string> folders, string libraryRelative, string compileKey, out string fullPath)
    {
        fullPath = string.Empty;

        var relative = Path.Combine(
            libraryRelative.Replace('/', Path.DirectorySeparatorChar),
            compileKey.Replace('/', Path.DirectorySeparatorChar));

        foreach (var folder in folders)
        {
            string candidate;
            try { candidate = Path.Combine(folder, relative); }
            catch { continue; }

            if (File.Exists(candidate))
            {
                fullPath = candidate;
                return true;
            }
        }

        return false;
    }
}
