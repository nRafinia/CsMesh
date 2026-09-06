using System.Runtime.InteropServices;

namespace CsMesh.Common;

/// <summary>
/// Finds the .NET shared framework directory to compile against.
///
/// RuntimeEnvironment.GetRuntimeDirectory answers "where is the runtime hosting me", which under
/// NativeAOT is a question with no answer: there is no hosted runtime, the framework is linked
/// into the executable, and the call returns the directory the binary happens to sit in. AddDir
/// then finds no reference assemblies there and reports success with zero of them.
///
/// Nothing about that looks like a failure from the outside. Roslyn still parses every file and
/// still binds anything that needs only types declared in the repository; what it loses is every
/// call whose overload resolution depends on a framework type. On a real solution that took
/// unbound call sites from 79 to 470 while the index still built, still reported node and edge
/// counts, and still answered queries -- just with a large share of the call graph quietly absent.
///
/// So the question asked here is the other one: where is .NET installed. That has an answer
/// whether or not this process is running on it.
/// </summary>
public static class RuntimeLocator
{
    /// <summary>
    /// A shared framework directory, or null when .NET cannot be located at all.
    ///
    /// The hosted answer is tried first and kept whenever it is genuinely a framework directory,
    /// so the ordinary JIT build keeps the exact behaviour it had.
    /// </summary>
    /// <param name="hostedDirectory">
    /// What the host reports, or null to ask it. Injectable because the only environment where
    /// this fix matters is one no test process can be in: a test run is JIT-hosted by definition,
    /// so without a seam the AOT path would be verifiable solely by publishing a native binary and
    /// measuring it -- which is worth doing, and is not a regression test.
    /// </param>
    public static string? FindSharedFramework(string? hostedDirectory = null)
    {
        var hosted = hostedDirectory ?? RuntimeEnvironment.GetRuntimeDirectory();
        if (IsFrameworkDirectory(hosted))
        {
            return hosted;
        }

        Dbg.Log($"runtime directory '{hosted}' holds no framework assemblies; searching for an installation");

        foreach (var root in CandidateRoots())
        {
            var framework = NewestFrameworkIn(root);
            if (framework != null)
            {
                Dbg.Log($"resolved shared framework: {framework}");
                return framework;
            }
        }

        Dbg.Log("no .NET installation found. Set DOTNET_ROOT to the directory containing 'shared'.");
        return null;
    }

    /// <summary>
    /// A directory only counts if it actually holds the assemblies a compilation needs. Testing
    /// for the marker file rather than the path shape is what keeps the AOT case honest: the
    /// binary's own directory can be named anything, including something that looks plausible.
    /// </summary>
    private static bool IsFrameworkDirectory(string? path) =>
        !string.IsNullOrEmpty(path) &&
        File.Exists(Path.Combine(path, "System.Private.CoreLib.dll")) &&
        File.Exists(Path.Combine(path, "System.Runtime.dll"));

    /// <summary>
    /// Places a .NET installation may be, most explicit first. DOTNET_ROOT is the variable the
    /// host itself reads, so an unusual install has already had to set it.
    /// </summary>
    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_ROOT(x64)", "DOTNET_ROOT_X64", "DOTNET_ROOT(x86)" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value)) yield return value;
        }

        // The dotnet executable lives at the root of the installation, so finding it on PATH
        // locates everything else. Symlinks are followed because package managers install a link
        // in /usr/bin and the real tree elsewhere -- taking the link's own directory would find
        // /usr/bin and nothing under it.
        if (FromPath() is { } fromPath && seen.Add(fromPath)) yield return fromPath;

        foreach (var wellKnown in WellKnownRoots())
        {
            if (Directory.Exists(wellKnown) && seen.Add(wellKnown)) yield return wellKnown;
        }
    }

    private static string? FromPath()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(entry.Trim(), exe); }
            catch { continue; }

            if (!File.Exists(candidate)) continue;

            try
            {
                var resolved = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                               ?? candidate;
                var directory = Path.GetDirectoryName(resolved);
                if (!string.IsNullOrEmpty(directory)) return directory;
            }
            catch (Exception ex)
            {
                Dbg.Log($"could not resolve '{candidate}': {ex.Message}");
            }
        }

        return null;
    }

    private static IEnumerable<string> WellKnownRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)" })
            {
                var programFiles = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrEmpty(programFiles)) yield return Path.Combine(programFiles, "dotnet");
            }

            yield break;
        }

        yield return "/usr/share/dotnet";
        yield return "/usr/local/share/dotnet";
        yield return "/usr/lib/dotnet";
        yield return "/opt/dotnet";

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, ".dotnet");
    }

    /// <summary>
    /// The highest Microsoft.NETCore.App under a root. Highest rather than nearest: compiling
    /// against a newer framework than the target resolves overloads that a older one would miss,
    /// and the alternative when versions disagree is no references at all.
    /// </summary>
    private static string? NewestFrameworkIn(string root)
    {
        string shared;
        try { shared = Path.Combine(root, "shared", "Microsoft.NETCore.App"); }
        catch { return null; }

        if (!Directory.Exists(shared)) return null;

        try
        {
            return Directory.EnumerateDirectories(shared)
                .Where(IsFrameworkDirectory)
                .OrderByDescending(d => ParseVersion(Path.GetFileName(d)))
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Dbg.Log($"could not read '{shared}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Preview versions carry a suffix such as 10.0.0-rc.2, which Version cannot parse.</summary>
    private static Version ParseVersion(string? name)
    {
        if (string.IsNullOrEmpty(name)) return new Version(0, 0);

        var dash = name.IndexOf('-');
        var numeric = dash < 0 ? name : name[..dash];

        return Version.TryParse(numeric, out var version) ? version : new Version(0, 0);
    }
}
