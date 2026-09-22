using CsMesh.Common;
using CsMesh.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsMesh.Analysis;

/// <summary>
/// Builds the symbol graph directly from source files using Roslyn compilation
/// without requiring full MSBuild workspace evaluation.
/// </summary>
public static partial class Indexer
{
    /// <summary>
    /// Whether a file on disk is a managed assembly.
    ///
    /// This used to be a list of file names known to be native shims, which meant it was only ever
    /// as complete as the last runtime someone looked inside. Adding the sibling shared frameworks
    /// to the reference set immediately proved the point: WindowsDesktop ships wpfgfx_cor3.dll and
    /// four more like it, none of them on the list, and eight fresh CS0009 diagnostics appeared.
    ///
    /// Reading the PE header answers the question directly and costs one open per file. A native
    /// DLL has no metadata directory; CreateFromFile does not notice, because it defers, so the
    /// failure surfaces later as a compiler diagnostic about a file the caller never chose.
    /// </summary>
    private static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            return pe.HasMetadata;
        }
        catch
        {
            return false;
        }
    }

    private static readonly string[] SkipDirs =
    {
        "/bin/", "/obj/", "/node_modules/", "/.git/", "/.vs/", "/.idea/", "/.svn/",
        "/packages/", "/TestResults/", "/artifacts/", "/.csmesh/"
    };

    /// <summary>
    /// Whether a file falls under one of <see cref="SkipDirs"/>, judged by its path relative to
    /// the root being indexed rather than by its absolute path.
    ///
    /// 'review' indexes a base revision from a worktree it creates at .csmesh/base -- deliberately
    /// inside the directory this indexer otherwise treats as its own cache and always excludes.
    /// Matching against the absolute path meant every file in that worktree carried '.csmesh/'
    /// somewhere in its ancestry regardless of what the scan root actually was, and the base graph
    /// indexed to zero files every time. The exclusion means "do not descend into a build output
    /// or scratch directory found while scanning", which is a statement about descendants of root,
    /// not about root's own location on disk.
    /// </summary>
    private static bool IsSkipped(string root, string file)
    {
        var relative = "/" + Path.GetRelativePath(root, file).Replace('\\', '/');
        return SkipDirs.Any(d => relative.Contains(d, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] RazorPatterns = { "*.razor", "*.cshtml" };

    /// <summary>
    /// .razor and .cshtml files under the root, using the same skip rules and project scope as
    /// EnumerateSourceFiles. Counted rather than parsed here: whether any of them were actually
    /// recovered from generated output is a separate question, answered by RazorComponentsIndexed.
    ///
    /// Scoped, not a plain directory walk: a project this index left out on purpose can still hold
    /// .razor files, and counting those against the total would report "3 of 9" in a repository
    /// where only 3 were ever going to be looked at.
    /// </summary>
    private static int CountRazorFiles(string root, ProjectScope scope)
    {
        var count = 0;
        foreach (var pattern in RazorPatterns)
        {
            IEnumerable<string> found;
            try { found = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var file in found)
            {
                if (IsSkipped(root, file)) continue;
                if (!scope.Includes(file)) continue;
                count++;
            }
        }

        return count;
    }

    public static IEnumerable<string> EnumerateSourceFiles(string root) =>
        EnumerateSourceFiles(root, ProjectScope.Everything(root));

    public static IEnumerable<string> EnumerateSourceFiles(string root, ProjectScope scope)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (IsSkipped(root, file)) continue;
            if (!scope.Includes(file)) continue;

            var normalized = file.Replace('\\', '/');
            if (normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (normalized.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (normalized.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)) continue;
            yield return file;
        }
    }

    /// <summary>
    /// The global usings Microsoft.NET.Sdk turns on when ImplicitUsings is enabled, plus the ones
    /// the Web SDK adds.
    ///
    /// These live in obj/Debug/{tfm}/{Project}.GlobalUsings.g.cs, which this indexer skipped twice
    /// over: obj/ is excluded as build output, and *.g.cs is excluded as generated. The intent was
    /// to drop designer files. The effect was that every project with ImplicitUsings -- the
    /// default since .NET 6 -- compiled with no System namespace, so List&lt;&gt;, Task, Guid and
    /// CancellationToken were unbound and roughly a quarter of every call site failed to resolve.
    ///
    /// The generated file is preferred when a build has produced one. This is the floor.
    /// </summary>
    private const string ImplicitUsings =
        """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        global using global::System.Net.Http.Json;
        global using global::Microsoft.AspNetCore.Builder;
        global using global::Microsoft.AspNetCore.Http;
        global using global::Microsoft.AspNetCore.Routing;
        global using global::Microsoft.Extensions.Configuration;
        global using global::Microsoft.Extensions.DependencyInjection;
        global using global::Microsoft.Extensions.Hosting;
        global using global::Microsoft.Extensions.Logging;
        """;

    /// <summary>
    /// Global using trees to compile, one project at a time.
    ///
    /// Read from the project's chosen target framework on purpose: a project retargeted from net8
    /// to net10 leaves both <c>obj/Debug/net8.0</c> and <c>obj/Debug/net10.0</c> behind, and the
    /// build never reads the older one. Compiling both imports namespaces the current build cannot
    /// see, which is a class of false binding rather than a missing one.
    ///
    /// When no generated file exists but the csproj asks for implicit usings, the set the SDK would
    /// have written is synthesized from the csproj and its Sdk attribute -- the Web SDK's ASP.NET
    /// namespaces only for a Web project. A repository with no csproj at all keeps the historic
    /// one-size-fits-all set, since there is no project to read a framework or an opt-in from.
    /// </summary>
    private static List<OwnedTree> GlobalUsingTrees(
        ProjectScope scope, CSharpParseOptions parseOptions, out int unevaluableUsings)
    {
        unevaluableUsings = 0;
        var result = new List<OwnedTree>();

        if (!scope.HasProjects)
        {
            result.Add(new OwnedTree(
                CSharpSyntaxTree.ParseText(ImplicitUsings, parseOptions, path: "<global-usings>"),
                new[] { "" }));
            return result;
        }

        var index = 0;
        foreach (var projectDirectory in scope.LiveDirectories)
        {
            var texts = ProjectGlobalUsingTexts(projectDirectory, out var unevaluable);
            unevaluableUsings += unevaluable;

            foreach (var text in texts)
            {
                result.Add(new OwnedTree(
                    CSharpSyntaxTree.ParseText(text, parseOptions, path: $"<global-usings-{index++}>"),
                    new[] { projectDirectory }));
            }
        }

        return result;
    }

    /// <summary>
    /// The SDK global-using sets for one project: what the build generated for its chosen target
    /// framework, or the set its ImplicitUsings, Sdk and &lt;Using&gt; items imply when no build
    /// wrote one. A source global-using file is not here; it is an ordinary owned .cs and arrives
    /// with the source. When a generated set exists it already contains the &lt;Using&gt; items, so
    /// nothing is synthesized and nothing is added twice.
    /// </summary>
    private static List<string> ProjectGlobalUsingTexts(string projectDirectory, out int unevaluable)
    {
        unevaluable = 0;

        var csproj = ProjectTfm.Single(projectDirectory);
        if (csproj is null) return [];

        var texts = new List<string>();
        foreach (var file in GlobalUsingFilesIn(projectDirectory, ProjectTfm.Choose(projectDirectory, "obj")))
        {
            try { texts.Add(File.ReadAllText(file)); } catch { /* unreadable contributes nothing */ }
        }

        if (texts.Count == 0)
        {
            var synthesized = ProjectTfm.Synthesize(csproj, out unevaluable);
            if (synthesized.Length > 0) texts.Add(synthesized);
        }

        return texts.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Generated global-usings files for one project under its chosen target framework.</summary>
    private static List<string> GlobalUsingFilesIn(string projectDirectory, string? framework)
    {
        var result = new List<string>();
        if (framework is null) return result;

        var objDirectory = Path.Combine(projectDirectory, "obj");
        if (!Directory.Exists(objDirectory)) return result;

        foreach (var config in ProjectTfm.ConfigDirectories(objDirectory))
        {
            var frameworkDirectory = Path.Combine(config, framework);
            if (!Directory.Exists(frameworkDirectory)) continue;

            try
            {
                result.AddRange(Directory.EnumerateFiles(frameworkDirectory, "*.GlobalUsings.g.cs",
                    SearchOption.AllDirectories));
            }
            catch
            {
                // An unreadable framework directory contributes nothing rather than failing the index.
            }
        }

        return result;
    }

    /// <summary>
    /// Whether a generated file belongs to Razor. Razor is the one generator given a special path:
    /// its output names the .razor/.cshtml file it came from on a checksum line, so a type can be
    /// mapped back to source a user can open rather than the obj/ path a clean deletes.
    /// </summary>
    private static bool IsRazorGenerated(string file) =>
        (file.EndsWith("_razor.g.cs", StringComparison.OrdinalIgnoreCase) ||
         file.EndsWith("_cshtml.g.cs", StringComparison.OrdinalIgnoreCase)) &&
        file.Replace('\\', '/').Split('/')
            .Any(segment => segment.EndsWith("RazorSourceGenerator", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Paths the source enumerator never returns: Razor sources and generated build output. Build()
    /// stamps them, and an incremental pass must carry those stamps forward -- it replaces Files
    /// wholesale with the .cs files it re-parsed, and a dropped stamp makes the next rebuild's
    /// changed generated output invisible to freshness.
    /// </summary>
    private static bool IsGeneratedTrackedPath(string path) =>
        path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Generated .g.cs files already on disk for one project, newest configuration and target
    /// framework combination first, filtered by <paramref name="accept"/>.
    ///
    /// These exist only when the project was last built with both -p:EmitCompilerGeneratedFiles=true
    /// and a non-incremental build -- a warm build with only the property set writes nothing, because
    /// nothing re-ran the generator. Absence is the default: most builds pass neither flag, and this
    /// indexer works with whatever obj/ happens to hold.
    ///
    /// The path is walked one level at a time -- obj/{config}/{tfm}/generated/... -- rather than
    /// globbed with a fixed Debug/net10.0 guess, because both segments are whatever the last build
    /// used, on a repository this indexer knows nothing else about.
    ///
    /// The generator's own directory is named after its assembly-qualified type, so nothing here
    /// keys on the directory name. Newest wins on purpose: Debug and Release both hold a copy of
    /// every generated type, and the same type arriving twice is CS0101 across the whole graph.
    /// Taking only the newest generated directory is what stops that.
    /// </summary>
    private static IReadOnlyList<string> GeneratedSourceFiles(string projectDir, Func<string, bool> accept)
    {
        var objDir = Path.Combine(projectDir, "obj");
        if (!Directory.Exists(objDir)) return [];

        List<string>? newestFiles = null;
        var newestStamp = DateTime.MinValue;

        IEnumerable<string> configDirs;
        try { configDirs = Directory.EnumerateDirectories(objDir); }
        catch { return []; }

        foreach (var configDir in configDirs)
        {
            IEnumerable<string> tfmDirs;
            try { tfmDirs = Directory.EnumerateDirectories(configDir); }
            catch { continue; }

            foreach (var tfmDir in tfmDirs)
            {
                var generatedDir = Path.Combine(tfmDir, "generated");
                if (!Directory.Exists(generatedDir)) continue;

                List<string> files;
                try
                {
                    // The generator's own directory is named after its assembly-qualified type, so
                    // nothing here keys on the directory name; accept decides per file. Razor's two
                    // suffixes (*_razor.g.cs, *_cshtml.g.cs) live in IsRazorGenerated; every other
                    // generator's output is accepted as plain .g.cs.
                    files = Directory.EnumerateFiles(generatedDir, "*.g.cs", SearchOption.AllDirectories)
                        .Where(accept)
                        .ToList();
                }
                catch { continue; }

                if (files.Count == 0) continue;

                DateTime stamp;
                try { stamp = Directory.GetLastWriteTimeUtc(generatedDir); }
                catch { stamp = DateTime.MinValue; }

                if (newestFiles != null && stamp <= newestStamp) continue;
                newestFiles = files;
                newestStamp = stamp;
            }
        }

        return newestFiles ?? [];
    }

    private static IReadOnlyList<string> GeneratedRazorFiles(string projectDir) =>
        GeneratedSourceFiles(projectDir, IsRazorGenerated);

    /// <summary>Everything under obj/**/generated that Razor does not own.</summary>
    private static IReadOnlyList<string> GeneratedNonRazorFiles(string projectDir) =>
        GeneratedSourceFiles(projectDir, f => !IsRazorGenerated(f));

    /// <summary>
    /// The .razor file a generated source came from, read off its own "#pragma checksum" line
    /// rather than derived from the generated file's own path.
    ///
    /// The generated file lives at .../Counter_razor.g.cs, one level of transformation away from
    /// Counter.razor and inside obj/, which will not survive the caller's next clean. Every graph
    /// node built from this tree must point at the real source, and the checksum line is the one
    /// place the compiler recorded it.
    /// </summary>
    private static string? RazorSourcePath(string generatedText)
    {
        var newline = generatedText.IndexOf('\n');
        var firstLine = newline >= 0 ? generatedText[..newline] : generatedText;
        if (!firstLine.TrimStart().StartsWith("#pragma checksum", StringComparison.Ordinal)) return null;

        var start = firstLine.IndexOf('"');
        if (start < 0) return null;
        var end = firstLine.IndexOf('"', start + 1);
        if (end < 0) return null;

        var path = firstLine[(start + 1)..end];
        return path.Length > 0 ? path : null;
    }

    /// <summary>
    /// Generated Razor sources for every project in scope, paired with the .razor file each one
    /// actually came from. A project excluded from the index does not get its components indexed
    /// either -- the same reasoning ProjectScope applies to .cs files applies here.
    ///
    /// A generated file older than the source it names is skipped rather than indexed: it was
    /// compiled from whatever the .razor file held before the edit that made it newer, and this
    /// indexer has no way to bind against text that no longer exists. Indexing it anyway would not
    /// merely be stale -- the FileStamp this pass writes afterwards comes from the .razor file's
    /// own current mtime, not the generated file's, so the next freshness check would find nothing
    /// to disagree with and call the graph clean while it silently holds pre-edit content forever,
    /// until some unrelated edit happens to touch the same file again. Skipping leaves the .razor
    /// file untracked instead, which is the honest state: unknown, not confidently wrong.
    /// </summary>
    private static (List<(string ProjectDir, string RazorPath, string Text)> Sources, int Stale) CollectGeneratedRazorSources(ProjectScope scope)
    {
        var result = new List<(string, string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stale = 0;

        foreach (var projectDir in scope.LiveDirectories)
        {
            foreach (var generated in GeneratedRazorFiles(projectDir))
            {
                string text;
                try { text = File.ReadAllText(generated); } catch { continue; }

                var razorPath = RazorSourcePath(text);
                if (razorPath == null || !File.Exists(razorPath)) continue;
                if (!seen.Add(razorPath)) continue;

                DateTime razorWrite, generatedWrite;
                try { razorWrite = File.GetLastWriteTimeUtc(razorPath); } catch { continue; }
                try { generatedWrite = File.GetLastWriteTimeUtc(generated); } catch { continue; }

                // Same forgiveness GraphStore.DirtyFiles gives ordinary freshness comparisons: some
                // filesystems round mtimes to the nearest 2 seconds, and without slack a build and
                // the edit right before it can land on the same rounded tick in either order.
                if (razorWrite - generatedWrite > TimeSpan.FromSeconds(2))
                {
                    stale++;
                    continue;
                }

                result.Add((projectDir, razorPath, text));
            }
        }

        return (result, stale);
    }

    /// <summary>
    /// Generated .g.cs for every non-Razor generator in scope, paired with the generated file's own
    /// absolute path. Unlike Razor there is no source file to map a type back to: a
    /// JsonSerializerContext's output names no origin, and no single source edit can be attributed
    /// as the cause of a stale artifact.
    ///
    /// Razor's 2 s staleness check is therefore dropped here and the files are counted instead. The
    /// only file a generic generated source could be compared against is itself; comparing it to
    /// any newer project source would invalidate every generated type after any unrelated edit and
    /// reintroduce the unresolved calls this ingestion exists to close. What is indexed is what the
    /// last build wrote; an edited source still marks its own file [STALE], and a clean rebuild is
    /// what refreshes the generated half. A file that cannot be read is skipped rather than counted.
    /// </summary>
    private static (List<(string ProjectDir, string File, string Text)> Sources, int Unreadable) CollectGeneratedSources(
        string root, ProjectScope scope)
    {
        var result = new List<(string, string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = 0;

        foreach (var projectDir in scope.LiveDirectories)
        {
            foreach (var generated in GeneratedNonRazorFiles(projectDir))
            {
                var relative = Path.GetRelativePath(root, generated);
                if (!seen.Add(relative)) continue;

                string text;
                try { text = File.ReadAllText(generated); }
                catch { unreadable++; continue; }

                result.Add((projectDir, generated, text));
            }
        }

        return (result, unreadable);
    }

    /// <summary>
    /// Indexes <paramref name="root"/>. When <paramref name="referenceRoot"/> is given, the
    /// reference set is read from that tree's bin/ instead of <paramref name="root"/>'s.
    ///
    /// Review needs this. A base revision is checked out into a clean worktree with no bin/, so
    /// indexing it against its own tree leaves every package type unbound: the DI, MediatR and
    /// route edges those types produced drop, and symbol keys that name a package type shift, so
    /// the comparison reports a false exit 5 against a working tree that did not change. Compiling
    /// the base against the working tree's built reference set removes the difference. The shadow
    /// rule stays the base scope's own assemblies, because those are exactly the types the base
    /// compiles from source and must not also load as references.
    /// </summary>
    public static Graph Build(string root, Action<string>? progress = null, bool includeAllProjects = false,
                              string? referenceRoot = null)
    {
        var scope = includeAllProjects ? ProjectScope.Everything(root) : ProjectScope.Discover(root);

        List<string> files;
        List<IReadOnlyList<string>> owners;
        using (Timings.Phase("enumerate+ownership"))
        {
            files = EnumerateSourceFiles(root, scope).ToList();
            owners = new List<IReadOnlyList<string>>(files.Count);
            foreach (var file in files) owners.Add(OwnershipOf(scope, file));
        }

        progress?.Invoke($"parsing {files.Count} files");

        var owned = new List<OwnedTree>(files.Count);
        var stamps = new List<FileStamp>(files.Count);
        var dirs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

        using (Timings.Phase("parse"))
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                string text;
                try { text = File.ReadAllText(file); } catch { continue; }

                owned.Add(new OwnedTree(
                    CSharpSyntaxTree.ParseText(text, parseOptions, path: file),
                    owners[i]));

                var fileInfo = new FileInfo(file);
                stamps.Add(new FileStamp
                {
                    Path = Path.GetRelativePath(root, file),
                    Ticks = fileInfo.LastWriteTimeUtc.Ticks,
                    Size = fileInfo.Length
                });

                var dir = fileInfo.DirectoryName;
                if (dir == null) continue;
                var relDir = Path.GetRelativePath(root, dir);
                if (!dirs.ContainsKey(relDir))
                {
                    try { dirs[relDir] = Directory.GetLastWriteTimeUtc(dir).Ticks; } catch { }
                }
            }
        }

        // Generated Razor sources are added through their own path rather than by loosening
        // EnumerateSourceFiles's obj/ and *.g.cs exclusions -- both are correct for every other
        // generator's output, and loosening them would pull in AssemblyInfo, GlobalUsings and the
        // rest alongside every real duplicate, which shows up as a flood of CS0101.
        //
        // The tree's path is the .razor file the checksum line names, not the generated file: every
        // node built from it -- NodeFor, SyntheticNode -- takes its File from the syntax tree's
        // path, so this one substitution is what keeps components pointing at source a user can
        // actually open instead of an obj/ path that a clean deletes.
        List<(string ProjectDir, string RazorPath, string Text)> razorSources;
        int staleRazorSources;
        using (Timings.Phase("generated-razor"))
        {
            (razorSources, staleRazorSources) = CollectGeneratedRazorSources(scope);
            foreach (var (projectDir, razorPath, text) in razorSources)
            {
                owned.Add(new OwnedTree(
                    CSharpSyntaxTree.ParseText(text, parseOptions, path: razorPath),
                    new[] { projectDir }));

                var razorInfo = new FileInfo(razorPath);
                stamps.Add(new FileStamp
                {
                    Path = Path.GetRelativePath(root, razorPath),
                    Ticks = razorInfo.LastWriteTimeUtc.Ticks,
                    Size = razorInfo.Length
                });
            }
        }

        // Non-Razor generators: System.Text.Json, Regex, LibraryImport, LoggerMessage and the rest.
        // Their output is real C# the compiler binds against, so without it every call that takes a
        // generated member -- JsonSerializer.Serialize(x, Ctx.Default.T), a partial method body --
        // is left unresolved, or resolved to the wrong overload. The .g.cs exclusion in
        // EnumerateSourceFiles stays: these are added through their own path so the two sets cannot
        // collide, exactly as Razor already did.
        List<(string ProjectDir, string File, string Text)> generatedSources;
        using (Timings.Phase("generated-sources"))
        {
            (generatedSources, _) = CollectGeneratedSources(root, scope);
            foreach (var (projectDir, generated, text) in generatedSources)
            {
                owned.Add(new OwnedTree(
                    CSharpSyntaxTree.ParseText(text, parseOptions, path: generated),
                    new[] { projectDir }));

                var generatedInfo = new FileInfo(generated);
                stamps.Add(new FileStamp
                {
                    Path = Path.GetRelativePath(root, generated),
                    Ticks = generatedInfo.LastWriteTimeUtc.Ticks,
                    Size = generatedInfo.Length
                });
            }
        }

        // The repository root itself may gain a new source file without any tracked directory changing.
        if (!dirs.ContainsKey("."))
        {
            try { dirs["."] = Directory.GetLastWriteTimeUtc(root).Ticks; } catch { }
        }

        // Each project gets its own SDK set, and a repository with no projects keeps the historic
        // one-size-fits-all set. A source global-using file is an ordinary owned .cs and arrives
        // through the source set above, so only the generated and synthesized sets are here.
        List<OwnedTree> globalUsings;
        int unevaluableUsings;
        using (Timings.Accumulate("ivt-usings"))
        {
            globalUsings = GlobalUsingTrees(scope, parseOptions, out unevaluableUsings);
        }
        owned.AddRange(globalUsings);

        List<MetadataReference> references;
        ReferenceReport referenceReport;
        using (var phase = Timings.Phase("reference-set"))
        {
            references = ReferenceSet(referenceRoot ?? root, scope, out referenceReport);
            phase.Detail($"references={references.Count} dlls-opened={referenceReport.Opened} " +
                         $"bytes-opened={referenceReport.Bytes}");
        }
        progress?.Invoke($"compiling against {references.Count} references");

        CompilationSet compilations;
        using (Timings.Phase("compile"))
        {
            compilations = CreateCompilations(root, scope, owned, references);
        }

        var graph = new Graph
        {
            Root = root,
            FormatVersion = Graph.CurrentFormatVersion,
            BuiltAt = DateTimeOffset.UtcNow,
            BuiltByVersion = AppVersion.Get(),
            BuiltFromCommit = RepositoryLocator.GitHead(root),
            Files = stamps,
            GlobalUsingSources = globalUsings.Count,
            RazorFileCount = CountRazorFiles(root, scope),
            RazorComponentsIndexed = razorSources.Count,
            RazorStaleSources = staleRazorSources,
            GeneratedSourcesIndexed = generatedSources.Count,
            IndexedAllProjects = includeAllProjects,
            SkippedProjects = scope.Excluded,
            SkippedProjectsReason = scope.Reason,
            ScopeDecision = scope.Decision,
            ExcludedLooseFiles = CountExcludedLooseFiles(root, scope),
            UnevaluableCompileItems = scope.UnevaluableCompileItems,
            UnevaluableInternalsVisibleTo = compilations.UnevaluableInternalsVisibleTo,
            UnevaluableUsings = unevaluableUsings,
            ProjectReferences = scope.References,
            ProjectCycles = compilations.Cycles,
            Dirs = dirs.Select(kv => new DirStamp { Path = kv.Key, Ticks = kv.Value }).ToList(),
            ReferenceCount = references.Count,
            RuntimeReferences = referenceReport.Runtime,
            OutputReferences = referenceReport.Output,
            OutputDirectories = referenceReport.OutputDirectories,
            ReferencesCapped = referenceReport.Capped > 0,
            ReferencesFailed = referenceReport.Failed,
            ShadowedOutputs = referenceReport.Shadowed
        };

        using (Timings.Phase("diagnostics"))
        {
            CaptureDiagnostics(compilations, graph);
        }

        // The two halves of synthesis were accumulated as they happened; one line, after the
        // diagnostics that follow them in the report's expected order.
        Timings.Flush("ivt-usings");

        var builder = new Builder(graph, compilations, new ProjectLocator(root));
        using (Timings.Phase("pass1")) builder.Pass1_Declarations(progress);
        using (Timings.Phase("pass2")) builder.Pass2_Bodies(progress);
        using (Timings.Phase("pass3")) builder.Pass3_Indirection(progress);

        builder.ExportDispatchTables();
        graph.UnresolvedCallSites = builder.UnresolvedCallSites;
        graph.TotalCallSites = builder.TotalCallSites;
        graph.AmbiguousDiRegistrations = builder.AmbiguousDiRegistrations;
        graph.AmbiguousMessageDispatches = builder.AmbiguousMessageDispatches;
        graph.UnmatchedMessageDispatches = builder.UnmatchedMessageDispatches;
        return graph;
    }

    /// <summary>
    /// How many .cs files sit outside every project while the repository has projects. Counted
    /// rather than silently skipped: a file the reader expected to find and cannot is worse than a
    /// line saying it was left out. Generated and designer files are not counted, because
    /// <see cref="EnumerateSourceFiles"/> never considered them in the first place.
    /// </summary>
    private static int CountExcludedLooseFiles(string root, ProjectScope scope)
    {
        if (!scope.HasProjects) return 0;

        var count = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (IsSkipped(root, file)) continue;

                var normalized = file.Replace('\\', '/');
                if (normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (normalized.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (normalized.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)) continue;

                if (scope.IsLoose(file) && !scope.Includes(file)) count++;
            }
        }
        catch
        {
            // An unreadable tree contributes no count rather than failing the index.
        }

        return count;
    }

    /// <summary>
    /// What the compiler thinks is wrong, per project.
    ///
    /// The indexer treats diagnostics as advisory and indexes whatever binds, which is the right
    /// default -- a graph from a half-compiling tree is still useful. But when resolution is poor
    /// the reason is sitting in these diagnostics and was never read. CS0246 means a reference is
    /// missing; CS0433 means the same type arrived from two assemblies, which happens when bin/
    /// holds a compiled copy of the very source being parsed. Those two call for opposite fixes,
    /// and guessing between them wasted a long time.
    ///
    /// One compilation per project also makes the project the unit that matters: a diagnostic is
    /// about one project's references, and a flat list mixed eight projects' errors into one pile.
    /// Each project keeps its own top eight by count; doctor decides how many to show.
    ///
    /// Declaration diagnostics only: method bodies produce thousands and none of them are about
    /// references.
    /// </summary>
    private static void CaptureDiagnostics(CompilationSet compilations, Graph graph)
    {
        foreach (var (project, compilation) in compilations.Named)
        {
            try
            {
                var interesting = compilation.GetDeclarationDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .GroupBy(d => d.Id)
                    .OrderByDescending(x => x.Count())
                    .Take(8);

                foreach (var group in interesting)
                {
                    var sample = group.First().GetMessage();
                    if (sample.Length > 160) sample = sample[..157] + "...";
                    graph.Diagnostics.Add(new CompilerNote
                    {
                        Id = group.Key,
                        Count = group.Count(),
                        Message = sample,
                        Project = project
                    });
                }
            }
            catch (Exception ex)
            {
                // Diagnostics are a nicety; failing to collect them must not fail an index.
                Dbg.Log($"diagnostics unavailable for '{project}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Nearest .csproj above a source file, resolved once per directory.
    ///
    /// Owned by the indexer rather than computed at the end, because pass 2 needs it: an
    /// unfiltered assembly scan is scoped by the project its marker type lives in, and a project
    /// stamp applied after the passes would be empty at the moment that decision is made.
    /// </summary>
    private sealed class ProjectLocator(string root)
    {
        private readonly Dictionary<string, string> _byDirectory = new(StringComparer.OrdinalIgnoreCase);

        public string For(string relativeFile)
        {
            if (relativeFile.Length == 0) return "";

            var directory = Path.GetDirectoryName(relativeFile) ?? "";
            if (_byDirectory.TryGetValue(directory, out var cached)) return cached;

            var project = "";
            var dir = new DirectoryInfo(Path.Combine(root, directory));
            var stop = Path.GetFullPath(root);

            while (dir != null && dir.FullName.StartsWith(stop, StringComparison.OrdinalIgnoreCase))
            {
                FileInfo? found = null;
                try { found = dir.EnumerateFiles("*.csproj").FirstOrDefault(); } catch { }

                if (found != null)
                {
                    project = Path.GetFileNameWithoutExtension(found.Name);
                    break;
                }

                dir = dir.Parent;
            }

            _byDirectory[directory] = project;
            return project;
        }
    }

    private static List<MetadataReference> ReferenceSet(string root, ProjectScope scope, out ReferenceReport report)
    {
        var list = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tally = new ReferenceReport();

        // Every bin/ under the repository is scanned for references, and every project in the
        // repository also builds into one of them. Left alone, the compilation ends up holding each
        // of the repository's own types twice: once from the source being indexed and once from the
        // assembly that source was compiled into.
        //
        // For ordinary calls the source declaration usually wins and nothing looks wrong. Extension
        // methods are where it shows, because their lookup gathers candidates from every assembly
        // in scope: the compiler finds AddFleetdeckData in the source and again in the reference,
        // cannot prefer either, and returns candidates with no symbol. What arrives in the index is
        // 'ambiguous-overload' clustered in whichever file calls the most extension methods --
        // which is always the composition root, so it reads like a problem with that one file.
        //
        // Only the projects actually being compiled are shadowed. A project the scope left out is
        // not in the compilation at all, so its assembly in bin/ is the only way anything that
        // calls into it can bind -- dropping that would trade one silent gap for a larger one.
        foreach (var name in OwnAssemblyNames(scope.LiveDirectories))
        {
            seen.Add(name);
            tally.Shadowed++;
        }

        void AddDir(string dir, int cap, bool runtime)
        {
            if (!Directory.Exists(dir)) return;
            var count = 0;
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
            {
                var name = Path.GetFileName(dll);

                // Deduplicate before reading the header: a shared framework and a bin/ copy of the
                // same assembly are the same file twice, and the header read is the expensive part.
                if (!seen.Add(name)) continue;

                // The byte tally is the diagnosis for a cold reference set: how much metadata the
                // run actually pulled through the file cache. A stat is not worth paying on every
                // ordinary index, so it is taken only when the timing report is on.
                if (Timings.Enabled)
                {
                    tally.Opened++;
                    try { tally.Bytes += new FileInfo(dll).Length; } catch { /* a length was not available */ }
                }

                // Native shims sit next to managed assemblies. Skipping them here rather than
                // letting the compiler complain later keeps the diagnostics in 'doctor' about the
                // caller's code instead of about the runtime's layout.
                if (!IsManagedAssembly(dll)) { tally.Native++; continue; }

                try
                {
                    list.Add(MetadataReference.CreateFromFile(dll));
                    if (runtime) tally.Runtime++; else tally.Output++;
                }
                catch
                {
                    tally.Failed++;
                }

                if (++count >= cap)
                {
                    tally.Capped++;
                    return;
                }
            }
        }

        string? runtimeDir;
        using (var runtime = Timings.Phase("runtime"))
        {
            runtimeDir = RuntimeLocator.FindSharedFramework();
            runtime.Detail(runtimeDir is null ? "not-found" : runtimeDir);
        }

        if (runtimeDir == null)
        {
            // Worth saying out loud. Every symptom downstream -- unbound calls, missing edges,
            // traces that stop early -- looks like a problem with the code being indexed.
            Console.Error.WriteLine(
                "warning: no .NET shared framework found, so framework types are absent from the " +
                "compilation and many calls will not bind. Set DOTNET_ROOT to your .NET install.");
        }
        else
        {
            AddDir(runtimeDir, 400, runtime: true);

            // The runtime directory is Microsoft.NETCore.App and nothing else. A web project's
            // ASP.NET Core types live in a sibling shared framework, and because the app is
            // framework-dependent they are never copied to bin/ either -- so without this they are
            // absent from the compilation entirely.
            //
            // What that looks like from the outside is not an error. Roslyn still binds most of the
            // file; it just cannot pick between overloads whose parameter types it has never seen, so
            // it returns candidates and no symbol. The index comes out with high call-resolution and
            // a pile of 'ambiguous-overload' concentrated in one project, which reads like a quirk of
            // that project's code rather than a missing reference.
            foreach (var dir in SiblingSharedFrameworks(runtimeDir))
            {
                AddDir(dir, 400, runtime: true);
            }
        }

        foreach (var bin in Directory.EnumerateDirectories(root, "bin", SearchOption.AllDirectories).Take(80))
        {
            tally.OutputDirectories++;

            // Only the project's own chosen framework is read. A stale net8 tree next to a net10 one
            // is build history the solution does not compile against, and loading its assemblies can
            // shadow the current ones with the same simple name.
            var chosen = ChosenFrameworkFor(root, bin);

            var kept = 0;
            foreach (var cfg in Directory.EnumerateDirectories(bin, "*", SearchOption.AllDirectories))
            {
                if (chosen is not null && IsStaleFrameworkDirectory(cfg, bin, chosen)) continue;

                AddDir(cfg, 200, runtime: false);
                if (++kept >= 20) break;
            }
        }

        report = tally;
        return list;
    }

    /// <summary>
    /// The target framework to read a bin/ tree through: the one the nearest csproj declares, or
    /// the newest on disk when that framework was never built. Null when no project owns the tree,
    /// in which case every framework directory is read as before.
    /// </summary>
    private static string? ChosenFrameworkFor(string root, string binDirectory)
    {
        var stop = Path.GetFullPath(root);
        var directory = new DirectoryInfo(Path.GetFullPath(binDirectory));

        while (directory is not null && directory.FullName.StartsWith(stop, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (directory.EnumerateFiles("*.csproj").Any())
                    return ProjectTfm.Choose(directory.FullName, "bin");
            }
            catch
            {
                // An unreadable directory is unknown rather than empty; keep walking upward.
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// True for a directory under one of the chosen framework's siblings. The segment immediately
    /// below the configuration names the framework, so "bin/Debug/net8.0" and "bin/Debug/net8.0/ref"
    /// are stale when net10.0 is chosen and exists; "bin/Debug" itself and "bin/Debug/net10.0" are not.
    /// </summary>
    private static bool IsStaleFrameworkDirectory(string candidate, string binDirectory, string chosen)
    {
        var relative = Path.GetRelativePath(binDirectory, candidate);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length < 2) return false;

        var framework = parts[1];
        return ProjectTfm.IsFrameworkName(framework) &&
               !framework.Equals(chosen, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other shared frameworks installed beside the running one, at the same version where
    /// possible and the highest available otherwise. ASP.NET Core and WindowsDesktop ship this way.
    /// </summary>
    internal static IEnumerable<string> SiblingSharedFrameworks(string runtimeDirectory)
    {
        var version = new DirectoryInfo(runtimeDirectory.TrimEnd(Path.DirectorySeparatorChar, '/'));
        var sharedRoot = version.Parent?.Parent;

        // The name check is not decoration. A runtime directory that is not laid out as
        // shared/<framework>/<version> -- a single-file publish, a self-contained deployment, an
        // unusual install -- walks two levels up to something arbitrary, and without this every
        // directory beside it would be handed to the compiler as a framework. On Linux, two levels
        // above a temp directory is the filesystem root.
        if (sharedRoot is not { Exists: true } ||
            !sharedRoot.Name.Equals("shared", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        foreach (var framework in sharedRoot.EnumerateDirectories())
        {
            if (framework.Name.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase)) continue;

            // Exact version match first, so the reference set matches the runtime the caller is on.
            var exact = Path.Combine(framework.FullName, version.Name);
            if (Directory.Exists(exact)) { yield return exact; continue; }

            var newest = framework.EnumerateDirectories()
                .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (newest != null) yield return newest.FullName;
        }
    }

    /// <summary>
    /// The output file names of the projects in this repository, so they can be kept out of the
    /// reference set. Taken from &lt;AssemblyName&gt; when the project sets one and from the file
    /// name otherwise, which is what MSBuild does.
    /// </summary>
    private static IEnumerable<string> OwnAssemblyNames(IEnumerable<string> projectDirectories)
    {
        foreach (var directory in projectDirectories)
        {
            string[] found;
            try { found = Directory.GetFiles(directory, "*.csproj"); }
            catch { continue; }

            foreach (var project in found) yield return AssemblyNameOf(project);
        }
    }

    /// <summary>
    /// A project's output file name: &lt;AssemblyName&gt; when it declares one, otherwise the
    /// project file name, which is what MSBuild falls back to.
    /// </summary>
    private static string AssemblyNameOf(string project)
    {
        var name = Path.GetFileNameWithoutExtension(project);

        string? text;
        try { text = File.ReadAllText(project); }
        catch { text = null; }

        var open = text?.IndexOf("<AssemblyName>", StringComparison.OrdinalIgnoreCase) ?? -1;
        if (open < 0) return name + ".dll";

        var close = text!.IndexOf("</AssemblyName>", open, StringComparison.OrdinalIgnoreCase);
        if (close <= open) return name + ".dll";

        var declared = text[(open + "<AssemblyName>".Length)..close].Trim();

        // An MSBuild property reference is not a name; fall back rather than guess at it.
        return declared.Length > 0 && !declared.Contains('$') ? declared + ".dll" : name + ".dll";
    }

    /// <summary>Where the reference set came from, and whether it was truncated.</summary>
    private sealed class ReferenceReport
    {
        public int Runtime;
        public int Output;
        public int OutputDirectories;
        public int Capped;
        public int Failed;
        public int Native;
        public int Shadowed;

        /// <summary>
        /// How many DLL files were opened to test whether they are managed, and the sum of their
        /// byte lengths. Only populated when CSMESH_TIMINGS=1: the count is what a cold-cache or
        /// scanner diagnosis needs, and an ordinary run should not pay a stat per reference for it.
        /// </summary>
        public int Opened;
        public long Bytes;
    }

    private sealed class Builder(Graph g, CompilationSet comps, ProjectLocator projects)
    {
        private static readonly SymbolDisplayFormat KeyFormat = SymbolDisplayFormat.FullyQualifiedFormat;

        /// <summary>
        /// A scan says a family is wired, not which pair. Below <see cref="Edge.TrustThreshold"/>
        /// so it is reported as inferred and never ranks above an explicit AddScoped.
        /// </summary>
        private const double ScanConfidence = 0.75;

        /// <summary>
        /// An unfiltered scan scoped only by project. Weaker than a filtered one because the
        /// assembly boundary is inferred from a file path rather than read from the scan itself.
        /// </summary>
        private const double BroadScanConfidence = 0.55;

        private static readonly string[] TestAttributes =
        {
            "Fact", "Theory", "Test", "TestCase", "TestMethod", "DataTestMethod", "Property"
        };

        private static readonly string[] HandlerInterfaces =
        {
            "IRequestHandler", "INotificationHandler", "ICommandHandler",
            "IQueryHandler", "IConsumer", "IHandleMessages"
        };

        private readonly Dictionary<string, int> _idByKey = new(StringComparer.Ordinal);

        /// <summary>Base type node id -> node ids of types deriving from or implementing it.</summary>
        private readonly Dictionary<int, List<int>> _implementorsByBase = new();

        /// <summary>
        /// Fully qualified request type -> every handler entry point that consumes it.
        /// The key is the semantic identity, never the short name: CompanyA.Commands.CreateOrder
        /// and CompanyB.Commands.CreateOrder must not share an entry, or Send() on one dispatches
        /// to the handler of the other. Request types the compiler could not bind are keyed
        /// "~Short" so they stay separable from resolved ones.
        /// </summary>
        private readonly Dictionary<string, List<int>> _handlersByRequest = new(StringComparer.Ordinal);

        /// <summary>Request short name -> the request keys that share it. Fallback lookup only.</summary>
        private readonly Dictionary<string, HashSet<string>> _requestKeysByShort = new(StringComparer.Ordinal);

        /// <summary>Service/implementation node id pairs registered in a DI container.</summary>
        private readonly HashSet<(int Service, int Implementation)> _diBoundPairs = new();

        private readonly HashSet<(int, int, EdgeKind)> _dedupe = new();

        /// <summary>
        /// The edge behind each deduped (from, to, kind), so a later member access can merge its
        /// role and site into the same edge instead of adding a second one. Without it a property
        /// read on one line and a write on another would be two edges, and "where is this written"
        /// would report the read as a writer too.
        /// </summary>
        private readonly Dictionary<(int, int, EdgeKind), Edge> _edgeByKey = new();
        private readonly List<(INamedTypeSymbol Type, TypeDeclarationSyntax Decl, int Id, SemanticModel Model)> _pendingHandlers = new();

        /// <summary>
        /// Members that override or implement a member declared in source, captured while the
        /// semantic models are still alive in pass 1 and emitted in pass 3 once the DI pairs are
        /// known (the di-bound note depends on pass 2).
        ///
        /// The relationship is read from the compiler -- OverriddenMethod/Property/Event and
        /// FindImplementationForInterfaceMember -- not matched by member name. Name matching cannot
        /// tell an override from a same-named neighbour: it linked every derived constructor to its
        /// base constructor, linked static methods that merely share a name, linked members hidden
        /// with 'new', and collapsed overloads because it compared no parameter types.
        /// </summary>
        private readonly List<(int BaseMember, int DerivedMember, int BaseType, int DerivedType, EdgeKind Kind)>
            _pendingMemberEdges = new();

        /// <summary>
        /// A sample, not a log. Capped per kind rather than in total: unbound calls into the BCL
        /// run into the hundreds and would otherwise crowd out the handful of DI and dispatch
        /// failures, which are the ones worth acting on.
        /// </summary>
        private static readonly Dictionary<string, int> UnresolvedCaps = new(StringComparer.Ordinal)
        {
            ["call"] = 250,
            ["type"] = 100,
            ["di"] = 75,
            ["mediatr"] = 75
        };

        /// <summary>
        /// Files the passes are allowed to read. Null means all of them, which is a full index.
        /// </summary>
        public HashSet<string>? OnlyFiles { get; set; }

        /// <summary>
        /// The assembly and project of the (compilation, tree) pair currently being bound. Set at
        /// the top of each unit's turn in pass 1 and pass 2: a synthetic node has no symbol to read
        /// either from, and RecordExternal must compare against the assembly of the compilation
        /// that owns this tree rather than any other compilation holding the same tree.
        /// </summary>
        private string _currentAssembly = "";
        private string _currentProject = "";

        /// <summary>
        /// Symbol key to the id it held before an incremental pass retired it. Empty on a full
        /// index, where every id is fresh anyway.
        /// </summary>
        public Dictionary<string, int> Recycle { get; set; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Loads what the surviving graph already knows, so an edited file can be bound against it
        /// without re-reading the files it depends on.
        ///
        /// The dedupe set is seeded too, and that is not optional. It is per-Builder, so on an
        /// incremental pass it starts empty while the edges it guards are still in the graph. Pass
        /// 3 re-derives interface and override edges from _implementorsByBase, whose base type is
        /// usually a node that was never retired, and every one of those would be appended twice.
        /// </summary>
        public void Seed()
        {
            foreach (var n in g.Nodes)
            {
                if (n.Key.Length == 0) continue;
                _idByKey[n.Key] = n.Id;
                if (n.Id >= g.NextNodeId) g.NextNodeId = n.Id + 1;
            }

            foreach (var id in Recycle.Values)
            {
                if (id >= g.NextNodeId) g.NextNodeId = id + 1;
            }

            foreach (var e in g.Edges) _dedupe.Add((e.From, e.To, e.Kind));

            foreach (var (request, handlers) in g.HandlersByRequest)
            {
                _handlersByRequest[request] = new List<int>(handlers);
            }

            foreach (var (shortName, keys) in g.RequestKeysByShort)
            {
                _requestKeysByShort[shortName] = new HashSet<string>(keys, StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// Writes the dispatch tables back onto the graph so the next incremental pass can seed
        /// from them. Called at the end of a full index and of a partial one.
        /// </summary>
        public void ExportDispatchTables()
        {
            g.HandlersByRequest = _handlersByRequest
                .ToDictionary(x => x.Key, x => new List<int>(x.Value), StringComparer.Ordinal);

            g.RequestKeysByShort = _requestKeysByShort
                .ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.Ordinal);
        }

        /// <summary>
        /// True for a tree an incremental pass has no reason to bind. Synthetic trees -- the
        /// global-using sets, whose paths are bracketed -- carry no declarations and no bodies.
        /// </summary>
        private bool Skip(SyntaxTree tree)
        {
            if (OnlyFiles == null) return false;
            if (tree.FilePath.Length == 0 || tree.FilePath[0] == '<') return true;
            return !OnlyFiles.Contains(Path.GetRelativePath(g.Root, tree.FilePath).Replace('\\', '/'));
        }

        public int UnresolvedCallSites { get; private set; }
        public int TotalCallSites { get; private set; }
        public int AmbiguousDiRegistrations { get; private set; }
        public int AmbiguousMessageDispatches { get; private set; }
        public int UnmatchedMessageDispatches { get; private set; }

        private int NodeFor(ISymbol sym, string kind, Location? loc = null)
        {
            var key = Key(sym);
            if (_idByKey.TryGetValue(key, out var existing)) return existing;

            var l = loc ?? sym.Locations.FirstOrDefault(x => x.IsInSource);
            var file = "";
            var line = 0;
            var endLine = 0;
            if (l is { IsInSource: true })
            {
                file = Path.GetRelativePath(g.Root, l.SourceTree!.FilePath);
                (line, endLine) = LineRange(l, file);
            }

            // The project is the one that declared the symbol, not the one nearest the file. They
            // differ for a base type resolved through a CompilationReference, and for a linked file
            // the two owners' nodes share a file but must carry different projects.
            var assembly = sym.ContainingAssembly?.Name ?? "";
            var project = comps.ProjectOfAssembly(assembly);
            if (project.Length == 0 && assembly.Length == 0) project = projects.For(file);

            return AddNode(key, FullName(sym), ShortName(sym), kind, file, line, endLine, project);
        }

        /// <summary>
        /// The line range to report for a location, honouring a "#line" directive when the compiler
        /// actually applied one.
        ///
        /// A generated Razor source carries one for @code content -- Razor maps a user's method or
        /// field straight back to its real line in the .razor file, and GetMappedLineSpan() reads
        /// that mapping for free. It does not cover the scaffolding Razor writes around that content
        /// (the class declaration itself, BuildRenderTree, [Inject] property backing fields): those
        /// sit under "#line hidden" on purpose, because none of them corresponds to one line of the
        /// source. For those, GetLineSpan()'s physical position is a line number in a ~100-line
        /// generated .g.cs the caller was never shown -- reported as a line in a ~10-line .razor
        /// file, it points past the end of a file the user can actually open. Line 1 is at least a
        /// place that file has.
        /// </summary>
        private static (int Line, int EndLine) LineRange(Location l, string file)
        {
            var mapped = l.GetMappedLineSpan();
            if (mapped.HasMappedPath)
            {
                return (mapped.StartLinePosition.Line + 1, mapped.EndLinePosition.Line + 1);
            }

            if (file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            {
                return (1, 1);
            }

            var span = l.GetLineSpan();
            return (span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
        }

        /// <summary>
        /// Creates a node for something that has no Roslyn symbol of its own: top-level statement
        /// bodies and minimal API route lambdas.
        /// </summary>
        private int SyntheticNode(string key, string name, string shortName, string kind, SyntaxNode at)
        {
            // Assembly-qualified like every symbol key. A top-level Program is synthesized per
            // compilation, and without the prefix two projects that share a source tree would merge
            // their entries into one node.
            key = _currentAssembly + "|" + key;
            if (_idByKey.TryGetValue(key, out var existing)) return existing;

            var file = at.SyntaxTree.FilePath.Length > 0
                ? Path.GetRelativePath(g.Root, at.SyntaxTree.FilePath)
                : "";
            var (line, endLine) = LineRange(at.GetLocation(), file);

            return AddNode(key, name, shortName, kind, file, line, endLine, _currentProject);
        }

        private int AddNode(string key, string name, string shortName, string kind,
                            string file, int line, int endLine, string project)
        {
            // A symbol that survived an edit gets its old id back, which is what keeps every edge
            // reaching it from an untouched file valid. Otherwise take the next free id -- never
            // the node count, which is only correct while nothing has ever been removed.
            var node = new Node
            {
                Id = Recycle.TryGetValue(key, out var reused) ? reused : g.NextNodeId++,
                Key = key,
                Project = project,
                Name = name,
                Short = shortName,
                Kind = kind,
                File = file,
                Line = line,
                EndLine = endLine
            };

            g.Add(node);
            _idByKey[key] = node.Id;
            return node.Id;
        }

        /// <summary>
        /// Uniquely identifies a symbol. Parameter types are fully qualified and method arity is
        /// included so overloads such as Handle(List&lt;int&gt;) and Handle(List&lt;string&gt;)
        /// never collapse into one node.
        ///
        /// The declaring assembly is part of the identity. One compilation per project means the
        /// same fully-qualified name can be declared in two assemblies, and without the prefix they
        /// merge into one node and one dispatch entry -- a silent false binding. ContainingAssembly
        /// uses the compilation name it was created with, so a project whose real name had to be
        /// disambiguated keys under the disambiguated name; the schema's separator is '|', which no
        /// C# identifier, namespace or generic parameter list contains.
        ///
        /// A member's identity carries its owner's fully-qualified name before the member segment,
        /// which is what lets a method node be traced back to its owner's key when pass 1 and pass 3
        /// key their tables by owner rather than by short name.
        /// </summary>
        private static string Key(ISymbol s)
        {
            // An extension method has two symbols. The declaration gives the original, whose first
            // parameter is the receiver; a call site gives the reduced form, where the receiver has
            // moved out of the parameter list. Keyed as written they differ, so pass 1 declared the
            // method and pass 2 created a second node for the same code -- and hung the call edge
            // on the phantom, leaving the real declaration with no callers and blast-radius with
            // nothing to report. ReducedFrom collapses them back onto one symbol.
            if (s is IMethodSymbol { ReducedFrom: { } original }) s = original;

            var assembly = s.ContainingAssembly?.Name ?? "";
            var container = s.ContainingType?.ToDisplayString(KeyFormat)
                            ?? s.ContainingNamespace?.ToDisplayString() ?? "";

            string self;
            if (s is IMethodSymbol m)
            {
                var parameters = string.Join(",", m.Parameters.Select(p =>
                    (p.RefKind == RefKind.None ? "" : p.RefKind.ToString().ToLowerInvariant() + " ")
                    + p.Type.ToDisplayString(KeyFormat)));
                self = $"{m.Name}`{m.Arity}({parameters})";
            }
            else
            {
                self = s.ToDisplayString(KeyFormat);
            }

            // A type's container is its namespace, so its own fully-qualified name is the whole
            // middle segment. A member's container is its owner type, which is the owner-qualified
            // middle segment. Both leave kind last, and '|' separates the two shapes.
            var identity = s.ContainingType is null ? self : container + "|" + self;
            return assembly + "|" + identity + "|" + s.Kind;
        }

        /// <summary>
        /// The owner prefix of a type key: assembly plus the type's fully-qualified name.
        ///
        /// A top-level type key is assembly|fqn|kind, so dropping the kind is enough; a nested type
        /// key also carries its containing type, assembly|outerFqn|innerFqn|kind, so the wanted
        /// segment is the last one before the kind, not simply everything before it. Both must
        /// reduce to assembly|innerFqn, which is the prefix the type's own member keys carry.
        /// </summary>
        private static string TypeOwnerPrefix(string key)
        {
            var parts = key.Split('|');
            return parts.Length < 2 ? key : parts[0] + "|" + parts[^2];
        }

        /// <summary>
        /// The owner prefix of a member key, or false for a key that is not
        /// assembly|ownerFqn|member|kind. A synthetic key such as <c>toplevel::</c> carries only
        /// the assembly, so it has no owner and is skipped rather than mis-attributed.
        /// </summary>
        private static bool TryMemberOwnerPrefix(string key, out string prefix)
        {
            var last = key.LastIndexOf('|');
            var secondLast = last > 0 ? key.LastIndexOf('|', last - 1) : -1;
            if (secondLast <= 0)
            {
                prefix = "";
                return false;
            }

            prefix = key[..secondLast];
            return true;
        }

        /// <summary>
        /// Nullable annotations are kept: whether a property is Guid or Guid? is usually the exact
        /// thing the caller opened the file to find out.
        /// </summary>
        private static readonly SymbolDisplayFormat SignatureFormat = SymbolDisplayFormat
            .MinimallyQualifiedFormat
            .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        private static string Display(ITypeSymbol? t) => t?.ToDisplayString(SignatureFormat) ?? "";

        private static string SignatureOf(IMethodSymbol? m)
        {
            if (m == null) return "";

            var parameters = string.Join(", ", m.Parameters.Select(p => $"{Display(p.Type)} {p.Name}"));
            return $"({parameters}) : {Display(m.ReturnType)}";
        }

        private static string FullName(ISymbol s)
        {
            if (s is INamedTypeSymbol) return s.ToDisplayString();
            if (s is IMethodSymbol or IPropertySymbol or IFieldSymbol)
                return $"{s.ContainingType?.ToDisplayString() ?? "?"}.{s.Name}";
            return s.ToDisplayString();
        }

        private static string ShortName(ISymbol s)
        {
            if (s is INamedTypeSymbol t) return t.Name;
            if (s is IMethodSymbol or IPropertySymbol or IFieldSymbol)
                return $"{s.ContainingType?.Name ?? "?"}.{s.Name}";
            return s.Name;
        }

        /// <summary>
        /// Types the code uses that are not declared here.
        ///
        /// "not found: PrivateKeyFile" is a false statement when the codebase constructs one on
        /// three lines. The symbol exists; its declaration is in a package. Saying so, with the
        /// call sites, is the difference between a typo and a boundary -- and only one of those is
        /// something the caller can fix.
        ///
        /// Framework assemblies are excluded: nobody runs 'csmesh trace string'.
        /// </summary>
        /// <summary>
        /// Walks a type and its generic arguments, recording any part that is declared elsewhere.
        /// A dependency named only as a parameter type is still a dependency the caller will ask
        /// about; List&lt;PrivateKeyFile&gt; must not hide it.
        /// </summary>
        private void RecordExternalIn(ITypeSymbol? type, SyntaxNode at, string owningAssembly, int depth = 0)
        {
            if (type == null || depth > 2) return;

            if (type is IArrayTypeSymbol array)
            {
                RecordExternalIn(array.ElementType, at, owningAssembly, depth + 1);
                return;
            }

            if (type is not INamedTypeSymbol named) return;

            if (!named.Locations.Any(l => l.IsInSource))
            {
                // An error type is a type the compiler could not bind. Reporting it as a package
                // dependency would be a guess dressed as a fact; it is an indexing failure and
                // belongs with the other ones.
                if (named.TypeKind == TypeKind.Error)
                {
                    RecordUnresolved("type", at, named.Name, "unbound-type");
                }
                else
                {
                    RecordExternal(named, at, owningAssembly);
                }
            }

            foreach (var argument in named.TypeArguments)
                RecordExternalIn(argument, at, owningAssembly, depth + 1);
        }

        private void RecordExternal(INamedTypeSymbol type, SyntaxNode at, string owningAssembly)
        {
            // string, int, object: never what someone is looking for.
            if (type.SpecialType != SpecialType.None) return;

            var assembly = type.ContainingAssembly?.Name ?? "";
            if (assembly.Length == 0) return;
            // The comparison is against the assembly of the compilation this tree is bound from,
            // so a type declared in a referenced project is not mistaken for one declared here.
            if (assembly == owningAssembly) return;
            if (FrameworkAssemblyPrefixes.Any(p => assembly.StartsWith(p, StringComparison.Ordinal))) return;

            var name = type.OriginalDefinition.Name;
            if (name.Length == 0) return;

            // The assembly is part of the match. Two packages can each declare a type of the same
            // simple name, and merging them by name made the site list name the wrong boundary.
            var existing = g.ExternalTypes.FirstOrDefault(x => x.Name == name && x.Assembly == assembly);
            if (existing == null)
            {
                if (g.ExternalTypes.Count >= 150) return;
                existing = new ExternalType { Name = name, Assembly = assembly };
                g.ExternalTypes.Add(existing);
            }

            if (existing.Sites.Count >= 6) return;

            var site = SiteOf(at);
            if (!existing.Sites.Contains(site, StringComparer.Ordinal)) existing.Sites.Add(site);
        }

        private static readonly string[] FrameworkAssemblyPrefixes =
        {
            "System", "Microsoft", "netstandard", "mscorlib", "WindowsBase", "PresentationCore"
        };

        /// <summary>
        /// Records where an edge was wanted and not made. Silence is the failure mode this exists
        /// to prevent: without it an agent reads a missing edge as "there is nothing there".
        /// </summary>
        private void RecordUnresolved(string kind, SyntaxNode at, string expression, string reason)
        {
            // The totals are counted before the cap, because a capped sample taken in traversal
            // order is not a sample of anything -- it is the first N files. Reasoning about
            // proportions from it produced a wrong conclusion once already.
            var tally = $"{kind}/{reason}";
            g.UnresolvedByReason[tally] = g.UnresolvedByReason.GetValueOrDefault(tally) + 1;

            var file = at.SyntaxTree.FilePath.Length > 0
                ? Path.GetRelativePath(g.Root, at.SyntaxTree.FilePath)
                : "";
            if (file.Length > 0)
            {
                var owner = ProjectOf(file);
                if (owner.Length > 0)
                    g.UnresolvedByProject[owner] = g.UnresolvedByProject.GetValueOrDefault(owner) + 1;
            }

            if (g.Unresolved.Count(u => u.Kind == kind) >= UnresolvedCaps.GetValueOrDefault(kind, 100)) return;

            var (line, _) = LineRange(at.GetLocation(), file);
            var text = expression.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (text.Length > 80) text = text[..77] + "...";

            g.Unresolved.Add(new UnresolvedSite
            {
                Kind = kind,
                File = file,
                Line = line,
                Expression = text,
                Reason = reason
            });
        }

        private void Link(int from, int to, EdgeKind kind, string? note = null,
                          double confidence = 1.0, string? source = null, SyntaxNode? at = null)
        {
            if (from == to) return;
            if (!_dedupe.Add((from, to, kind))) return;
            var edge = new Edge
            {
                From = from,
                To = to,
                Kind = kind,
                Note = note,
                // Left null at full confidence so the on-disk graph does not grow a field per edge.
                Confidence = confidence >= 1.0 ? null : confidence,
                Source = source,
                Site = at == null ? null : SiteOf(at)
            };
            g.Edges.Add(edge);
            _edgeByKey[(from, to, kind)] = edge;
        }

        /// <summary>Where an edge was declared, as a repo-relative file:line.</summary>
        private string ProjectOf(string relativeFile) => projects.For(relativeFile);

        private string SiteOf(SyntaxNode at)
        {
            var file = at.SyntaxTree.FilePath.Length > 0
                ? Path.GetRelativePath(g.Root, at.SyntaxTree.FilePath)
                : "";
            var (line, _) = LineRange(at.GetLocation(), file);

            return $"{file}:{line}";
        }

        // ---------------------------------------------------------------- pass 1

        public void Pass1_Declarations(Action<string>? progress)
        {
            progress?.Invoke("pass 1: declarations");

            foreach (var unit in comps.BindOrder)
            {
                var tree = unit.Tree;
                if (Skip(tree)) continue;
                var model = unit.Model();
                _currentAssembly = unit.Compilation.AssemblyName ?? "";
                _currentProject = unit.Project;

                // Enums and delegates derive from BaseTypeDeclarationSyntax / MemberDeclarationSyntax,
                // not from TypeDeclarationSyntax, so a loop over TypeDeclarationSyntax alone leaves
                // them out of the graph entirely. In C# an enum is where behaviour is decided --
                // a switch over OrderStatus is the branch point of a feature -- and "what breaks if
                // I add a member" is unanswerable for a symbol that does not exist.
                foreach (var enumDecl in tree.GetRoot().DescendantNodes().OfType<EnumDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(enumDecl) is not INamedTypeSymbol enumType) continue;

                    var enumId = NodeFor(enumType, "enum", enumDecl.GetLocation());
                    var enumNode = g.ById(enumId)!;
                    enumNode.Signature = $"enum : {enumType.EnumUnderlyingType?.Name ?? "int"}";

                    foreach (var member in enumDecl.Members)
                    {
                        if (model.GetDeclaredSymbol(member) is not IFieldSymbol field) continue;

                        var memberId = NodeFor(field, "enum-member", member.GetLocation());
                        g.ById(memberId)!.Signature = field.ConstantValue?.ToString() ?? "";
                        Link(enumId, memberId, EdgeKind.TypeUse, "member");
                    }
                }

                foreach (var delegateDecl in tree.GetRoot().DescendantNodes().OfType<DelegateDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(delegateDecl) is not INamedTypeSymbol del) continue;

                    var delegateId = NodeFor(del, "delegate", delegateDecl.GetLocation());
                    g.ById(delegateId)!.Signature = SignatureOf(del.DelegateInvokeMethod);
                }

                foreach (var typeDecl in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol type) continue;

                    var kind = type.TypeKind == TypeKind.Interface ? "interface" : "type";
                    var typeId = NodeFor(type, kind, typeDecl.GetLocation());
                    var typeNode = g.ById(typeId)!;

                    foreach (var t in TypeTags(type, typeDecl)) AddTag(typeNode, t);

                    RegisterBaseTypes(type, typeId);
                    _pendingHandlers.Add((type, typeDecl, typeId, model));

                    // Primary constructor parameters are declared on the type, not in a member.
                    if (typeDecl.ParameterList != null)
                    {
                        foreach (var parameter in typeDecl.ParameterList.Parameters)
                        {
                            if (parameter.Type != null &&
                                model.GetSymbolInfo(parameter.Type).Symbol is ITypeSymbol pt)
                            {
                                RecordExternalIn(pt, parameter.Type, _currentAssembly);
                            }
                        }
                    }

                    foreach (var member in typeDecl.Members)
                    {
                        switch (member)
                        {
                            case MethodDeclarationSyntax md when model.GetDeclaredSymbol(md) is { } ms:
                            {
                                var mId = NodeFor(ms, "method", md.GetLocation());
                                Link(typeId, mId, EdgeKind.TypeUse, "member");
                                var mNode = g.ById(mId)!;
                                mNode.Signature = SignatureOf(ms);
                                if (!ms.ReturnsVoid) RecordExternalIn(ms.ReturnType, md.ReturnType, _currentAssembly);
                                foreach (var parameter in ms.Parameters)
                                    RecordExternalIn(parameter.Type, md, _currentAssembly);
                                foreach (var t in MethodTags(md)) AddTag(mNode, t);
                                if (typeNode.Tags.Contains("controller")) AddTag(mNode, "action");
                                if (mNode.Tags.Contains("test")) AddTag(typeNode, "test");
                                break;
                            }
                            case ConstructorDeclarationSyntax cd when model.GetDeclaredSymbol(cd) is { } cs:
                                Link(typeId, NodeFor(cs, "method", cd.GetLocation()), EdgeKind.TypeUse, "ctor");
                                foreach (var parameter in cs.Parameters)
                                    RecordExternalIn(parameter.Type, cd, _currentAssembly);
                                break;
                            case PropertyDeclarationSyntax pd when model.GetDeclaredSymbol(pd) is { } ps:
                            {
                                var pId = NodeFor(ps, "property", pd.GetLocation());
                                Link(typeId, pId, EdgeKind.TypeUse, "member");
                                g.ById(pId)!.Signature = Display(ps.Type);
                                RecordExternalIn(ps.Type, pd.Type, _currentAssembly);

                                // DbSet<Order> is the only place a context and an entity are
                                // named together. Without this edge, "what does this context
                                // touch" answers with property names -- Orders, Customers -- and
                                // the entity types they expose are reachable only by reading the
                                // declarations, which is the lookup the graph exists to remove.
                                //
                                // Deliberately stops here. Which columns a LINQ query reads is an
                                // expression-tree question, not a symbol one, and guessing at it
                                // would put false edges in a graph whose value is that its edges
                                // are real.
                                if (ps.Type is INamedTypeSymbol { Name: "DbSet" or "DbQuery" } set &&
                                    set.TypeArguments.Length == 1 &&
                                    set.TypeArguments[0] is INamedTypeSymbol entity &&
                                    entity.Locations.Any(l => l.IsInSource))
                                {
                                    Link(typeId, NodeFor(entity, "type"), EdgeKind.TypeUse, "entity");
                                    AddTag(g.ById(typeId)!, "dbcontext");
                                }

                                break;
                            }
                            // Entities and records often carry plain fields. Without them a caller
                            // asking what a type holds gets half the answer.
                            case FieldDeclarationSyntax fd:
                            {
                                foreach (var variable in fd.Declaration.Variables)
                                {
                                    if (model.GetDeclaredSymbol(variable) is not IFieldSymbol fs) continue;
                                    var fId = NodeFor(fs, "field", variable.GetLocation());
                                    Link(typeId, fId, EdgeKind.TypeUse, "member");
                                    g.ById(fId)!.Signature = Display(fs.Type);
                                    RecordExternalIn(fs.Type, fd.Declaration.Type, _currentAssembly);
                                }

                                break;
                            }
                        }
                    }

                    // Every member node for this type exists now, so the compiler's own
                    // override/implementation relationships can be turned into edges.
                    CaptureMemberEdges(type, typeId);
                }
            }

            // A [Fact] on one method marks the whole class, and the class mark has to reach the
            // methods declared before it was applied. Without this pass a test class's members
            // look like production callers in blast-radius and diff.
            foreach (var node in g.Nodes.Where(n => n.Kind is "type" or "interface" && n.Tags.Contains("test")))
            {
                foreach (var e in g.Edges.Where(x => x.From == node.Id && x.Kind == EdgeKind.TypeUse))
                {
                    if (g.ById(e.To) is { } member) AddTag(member, "test");
                }
            }

            var methodsByOwner = MethodsByOwner();
            foreach (var (type, decl, id, handlerModel) in _pendingHandlers)
            {
                RegisterMessageHandler(type, decl, id, handlerModel, methodsByOwner);
            }

            Dbg.Log($"pass 1: {g.Nodes.Count} nodes, {_handlersByRequest.Count} message type(s) mapped, " +
                    $"{_implementorsByBase.Count} base type(s) with implementors");
        }

        /// <summary>
        /// Records inheritance using symbols rather than short type names, so Domain.Order and
        /// Data.Order are never treated as the same base type.
        /// </summary>
        private void RegisterBaseTypes(INamedTypeSymbol type, int typeId)
        {
            foreach (var iface in type.AllInterfaces)
            {
                AddImplementor(iface, typeId);
            }

            for (var b = type.BaseType; b != null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
            {
                AddImplementor(b, typeId);
            }
        }

        private void AddImplementor(INamedTypeSymbol baseType, int implId)
        {
            var definition = baseType.OriginalDefinition;
            if (!definition.Locations.Any(l => l.IsInSource)) return;

            var baseId = NodeFor(definition, definition.TypeKind == TypeKind.Interface ? "interface" : "type");
            if (baseId == implId) return;

            if (!_implementorsByBase.TryGetValue(baseId, out var list))
                _implementorsByBase[baseId] = list = new List<int>();
            if (!list.Contains(implId)) list.Add(implId);
        }

        /// <summary>
        /// The member-level edges of inheritance, read from the compiler while the model is alive.
        ///
        /// An override is <c>OverriddenMethod</c>/<c>OverriddenProperty</c>/<c>OverriddenEvent</c>,
        /// followed up the chain until it reaches a declaration in source (an intermediate override
        /// in a referenced project that is itself out of scope has no node). That relationship is
        /// exactly the set the compiler considers an override, so constructors (no overridden
        /// member), static members and methods hidden with <c>new</c> produce nothing -- the old
        /// name match drew all three as if they were overrides.
        ///
        /// An interface implementation is <c>FindImplementationForInterfaceMember</c>, which covers
        /// implicit and explicit implementations and an implementation inherited from a base class.
        /// The base member node is keyed by its open definition, because a member reached through a
        /// constructed interface carries the type arguments and would key differently from the
        /// declaration.
        /// </summary>
        private void CaptureMemberEdges(INamedTypeSymbol type, int typeId)
        {
            foreach (var member in type.GetMembers())
            {
                // An accessor is reached through its property or event; emitting it here as well
                // would draw the same override twice and mint a get_/set_ method node pass 1 never
                // declared. A compiler-synthesized member -- a record's Clone, Equals, PrintMembers,
                // EqualityContract -- is not source anyone wrote; the graph models what is declared.
                if (IsAccessor(member) || member.IsImplicitlyDeclared) continue;

                ISymbol? overridden = member switch
                {
                    IMethodSymbol m when !m.IsStatic && m.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor)
                        => NearestInSource(m.OverriddenMethod, x => x.OverriddenMethod),
                    IPropertySymbol p when !p.IsStatic => NearestInSource(p.OverriddenProperty, x => x.OverriddenProperty),
                    IEventSymbol e when !e.IsStatic => NearestInSource(e.OverriddenEvent, x => x.OverriddenEvent),
                    _ => null
                };

                if (overridden is null) continue;

                // The override is reached through the declared type, but an override of Base<int>
                // carries the constructed base. The node was declared on the open type, so both
                // the member and its owner are reduced to their original definitions or the
                // lookup would create a second Base<int> node beside Base<T>.
                var baseMember = overridden.OriginalDefinition;
                var baseOwner = baseMember.ContainingType?.OriginalDefinition;
                if (baseOwner is null) continue;

                var derivedKind = KindOf(member);
                var baseKind = KindOf(baseMember);
                if (derivedKind is null || baseKind is null) continue;

                var baseTypeId = NodeFor(baseOwner, TypeKindOf(baseOwner));
                _pendingMemberEdges.Add((
                    NodeFor(baseMember, baseKind),
                    NodeFor(member, derivedKind),
                    baseTypeId,
                    typeId,
                    EdgeKind.Override));
            }

            foreach (var iface in type.AllInterfaces)
            {
                if (!iface.OriginalDefinition.Locations.Any(l => l.IsInSource)) continue;

                var ifaceId = NodeFor(iface.OriginalDefinition, "interface");
                foreach (var member in iface.GetMembers())
                {
                    if (member.IsStatic || IsAccessor(member) || member.IsImplicitlyDeclared) continue;
                    var baseKind = KindOf(member);
                    if (baseKind is null) continue;

                    var found = type.FindImplementationForInterfaceMember(member);
                    if (found is null || !found.Locations.Any(l => l.IsInSource)) continue;
                    if (SymbolEqualityComparer.Default.Equals(found, member)) continue;

                    // The implementation reached through a constructed interface can be a member of
                    // a constructed base, SqlCommandRepositoryBase<UserBooking, DbContext>.AddAsync.
                    // The graph declares the open member, so reduce before keying.
                    var implementation = found.OriginalDefinition;
                    var implKind = KindOf(implementation);
                    if (implKind is null) continue;

                    _pendingMemberEdges.Add((
                        NodeFor(member.OriginalDefinition, baseKind),
                        NodeFor(implementation, implKind),
                        ifaceId,
                        typeId,
                        EdgeKind.Interface));
                }
            }
        }

        /// <summary>
        /// The nearest ancestor in the override chain whose declaring type is compiled from source,
        /// or null. An ancestor declared in metadata (a base class in a package) has no member node
        /// to point at, so the chain is followed past it to one that does.
        /// </summary>
        private static TSymbol? NearestInSource<TSymbol>(TSymbol? first, Func<TSymbol, TSymbol?> next)
            where TSymbol : class, ISymbol
        {
            for (var current = first; current is not null; current = next(current))
            {
                if (current.IsImplicitlyDeclared) continue;
                if (current.ContainingType is { } owner && owner.Locations.Any(l => l.IsInSource))
                    return current;
            }

            return null;
        }

        private static string? KindOf(ISymbol member) => member switch
        {
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IEventSymbol => "event",
            _ => null
        };

        /// <summary>
        /// True for a property or event accessor. Roslyn lists them among a type's members, but the
        /// graph models the property or event, not its get_/set_/add_/remove_ methods.
        /// </summary>
        private static bool IsAccessor(ISymbol member) =>
            member is IMethodSymbol { AssociatedSymbol: IPropertySymbol or IEventSymbol };

        private static string TypeKindOf(INamedTypeSymbol type) =>
            type.TypeKind == TypeKind.Interface ? "interface" : "type";

        private static void AddTag(Node n, string tag)
        {
            if (!n.Tags.Contains(tag)) n.Tags.Add(tag);
        }

        private static IEnumerable<string> TypeTags(INamedTypeSymbol type, TypeDeclarationSyntax decl)
        {
            var bases = BaseTypeNames(type, decl).ToList();

            if (type.Name.EndsWith("Controller", StringComparison.Ordinal) ||
                bases.Any(b => b.Contains("ControllerBase") || b == "Controller"))
                yield return "controller";

            if (bases.Any(b => b.StartsWith("IRequestHandler") || b.StartsWith("INotificationHandler") ||
                               b.StartsWith("ICommandHandler") || b.StartsWith("IQueryHandler")))
                yield return "handler";

            if (bases.Any(b => b.StartsWith("IRequest") || b.StartsWith("INotification") ||
                               b.StartsWith("ICommand") || b.StartsWith("IQuery")))
                yield return "message";

            if (bases.Any(b => b.Contains("DbContext"))) yield return "dbcontext";

            // A validator already reaches its command in the graph -- AbstractValidator<CreateOrder>
            // is a real type reference and the constructor edge follows from it. What was missing
            // was any way to tell that edge apart from an ordinary caller, so 'who touches this
            // command' listed the validator beside the handler with nothing to say which runs
            // first, or that one of them can reject the request before the other ever sees it.
            if (bases.Any(b => b.StartsWith("AbstractValidator", StringComparison.Ordinal) ||
                               b.StartsWith("IValidator", StringComparison.Ordinal)))
                yield return "validator";

            if (bases.Any(b => b.StartsWith("IConsumer") || b.StartsWith("IHandleMessages")))
                yield return "consumer";

            if (bases.Any(b => b.Contains("BackgroundService") || b.StartsWith("IHostedService")))
                yield return "hosted";

            if (type.Name.EndsWith("Repository", StringComparison.Ordinal)) yield return "repository";

            // Test code is in the graph on purpose -- a test is a real caller and dropping it
            // would understate a blast radius. But it is not production, and the two must be
            // separable: a test double should never outrank a registered implementation, and
            // "who calls this" means something different for a test than for a controller.
            if (type.Name.EndsWith("Tests", StringComparison.Ordinal) ||
                type.Name.EndsWith("Test", StringComparison.Ordinal) ||
                type.Name.EndsWith("Spec", StringComparison.Ordinal) ||
                type.Name.EndsWith("Specs", StringComparison.Ordinal) ||
                type.Name.EndsWith("Fixture", StringComparison.Ordinal))
                yield return "test";
            if (type.IsAbstract && type.TypeKind == TypeKind.Class) yield return "abstract";

            var route = AttrArg(decl.AttributeLists, "Route");
            if (route != null) yield return "route:" + route;

            // Set by Build() when this type came from a generated Razor source rather than a plain
            // .cs file: the tree's path is the .razor file the checksum line named, not a .cs path.
            if (decl.SyntaxTree.FilePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            {
                yield return "razor-component";

                // Razor's source generator writes the attribute fully qualified --
                // [global::Microsoft.AspNetCore.Components.RouteAttribute("/counter")] -- because
                // generated code never carries a using for it, so AttrArg's bare-name match would
                // silently miss every one of these.
                foreach (var page in RazorPageRoutes(decl)) yield return $"http:GET {page}";
            }
            // A Razor Page or MVC view compiles through the same generator to an internal, mangled
            // class name (Pages/Index.cshtml becomes Pages_Index) with no RouteAttribute at all --
            // routing there is by file convention or a Page directive string, not an attribute this
            // indexer can read reliably. Tagged separately from razor-component rather than folded
            // into it: a View is not a component, and inventing a route tag with no attribute behind
            // it would be a guess wearing the same "read straight off a symbol" tag other routes use.
            else if (decl.SyntaxTree.FilePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            {
                yield return "razor-view";
            }
        }

        /// <summary>
        /// @page routes on a generated Razor component, read the same way TryMinimalApiRoute reads
        /// a minimal API pattern: from the source, not from a naming convention. A component can
        /// carry more than one @page directive, and each is a real entrypoint on its own.
        /// </summary>
        private static IEnumerable<string> RazorPageRoutes(TypeDeclarationSyntax decl)
        {
            foreach (var al in decl.AttributeLists)
            {
                foreach (var a in al.Attributes)
                {
                    var n = a.Name.ToString();
                    if (n != "Route" && n != "RouteAttribute" &&
                        !n.EndsWith(".Route", StringComparison.Ordinal) &&
                        !n.EndsWith(".RouteAttribute", StringComparison.Ordinal))
                        continue;

                    var arg = a.ArgumentList?.Arguments.FirstOrDefault()?.ToString().Trim('"');
                    if (arg != null) yield return arg;
                }
            }
        }

        private static IEnumerable<string> MethodTags(MethodDeclarationSyntax md)
        {
            foreach (var verb in new[] { "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch" })
            {
                foreach (var al in md.AttributeLists)
                {
                    foreach (var a in al.Attributes)
                    {
                        var n = a.Name.ToString();
                        if (n != verb && n != verb + "Attribute") continue;
                        var arg = a.ArgumentList?.Arguments.FirstOrDefault()?.ToString().Trim('"');
                        yield return $"http:{verb[4..].ToUpperInvariant()} {arg ?? "/"}";
                    }
                }
            }

            if (md.AttributeLists.SelectMany(al => al.Attributes)
                  .Any(a => a.Name.ToString().Contains("Obsolete")))
            {
                yield return "obsolete";
            }

            // xUnit, NUnit and MSTest, by attribute rather than by naming convention.
            if (md.AttributeLists.SelectMany(al => al.Attributes)
                  .Any(a => TestAttributes.Contains(a.Name.ToString().Split('.').Last()
                                                     .Replace("Attribute", ""), StringComparer.Ordinal)))
            {
                yield return "test";
            }
        }

        private static string? AttrArg(SyntaxList<AttributeListSyntax> lists, string name)
        {
            foreach (var al in lists)
            {
                foreach (var a in al.Attributes)
                {
                    var n = a.Name.ToString();
                    if (n == name || n == name + "Attribute")
                        return a.ArgumentList?.Arguments.FirstOrDefault()?.ToString().Trim('"');
                }
            }
            return null;
        }

        /// <summary>
        /// Extracts base type and interface names from semantic symbols, falling back to syntax.
        /// </summary>
        private static IEnumerable<string> BaseTypeNames(INamedTypeSymbol type, TypeDeclarationSyntax decl)
        {
            foreach (var i in type.AllInterfaces) yield return Simplify(i);
            for (var b = type.BaseType; b != null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
                yield return Simplify(b);
            if (decl.BaseList != null)
                foreach (var t in decl.BaseList.Types)
                    yield return t.Type.ToString();
        }

        private static string Simplify(INamedTypeSymbol s) =>
            s.IsGenericType ? $"{s.Name}<{string.Join(",", s.TypeArguments.Select(a => a.Name))}>" : s.Name;

        /// <summary>
        /// Method nodes by the assembly-qualified key of the type that owns them.
        ///
        /// Keyed by owner key, not owner short name. Two projects can each declare Demo.IMediator
        /// or Demo.OrderHandler, and keying on the short name merged their methods into one bucket,
        /// so a handler entry point could be resolved to the other project's method. The owner key
        /// is derived from the member's own key, whose middle segment is the owner's fully-qualified
        /// name; a synthetic key has no owner and is skipped.
        /// </summary>
        private Dictionary<string, List<Node>> MethodsByOwner()
        {
            var ownerKeyByPrefix = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var n in g.Nodes)
            {
                if (n.Kind is not ("type" or "interface" or "enum" or "delegate")) continue;
                ownerKeyByPrefix[TypeOwnerPrefix(n.Key)] = n.Key;
            }

            var map = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
            foreach (var n in g.Nodes)
            {
                if (n.Kind != "method") continue;
                if (!TryMemberOwnerPrefix(n.Key, out var prefix)) continue;
                if (!ownerKeyByPrefix.TryGetValue(prefix, out var ownerKey)) continue;

                if (!map.TryGetValue(ownerKey, out var list)) map[ownerKey] = list = new List<Node>();
                list.Add(n);
            }

            return map;
        }

        /// <summary>
        /// Maps a message or request type to the handler entry point that will run for it.
        /// Several handlers may subscribe to the same notification, so all of them are recorded.
        /// Requests are keyed by fully qualified name; two same-named commands in different
        /// namespaces stay separate entries.
        /// </summary>
        private void RegisterMessageHandler(
            INamedTypeSymbol type,
            TypeDeclarationSyntax decl,
            int typeId,
            SemanticModel model,
            Dictionary<string, List<Node>> methodsByOwner)
        {
            var requests = new HashSet<string>(StringComparer.Ordinal);

            // Symbol path: works when the MediatR/MassTransit assembly resolved.
            foreach (var i in type.AllInterfaces)
            {
                if (!HandlerInterfaces.Contains(i.Name, StringComparer.Ordinal)) continue;
                if (i.TypeArguments.Length == 0) continue;
                if (i.TypeArguments[0] is INamedTypeSymbol named && named.Name.Length > 0)
                    requests.Add(RequestKey(named));
            }

            // Syntax path: the common case, since the abstraction package is often not referenced.
            // The base list is still resolved through the semantic model first, so a qualified or
            // using-imported request type keeps its real identity instead of collapsing to a name.
            if (decl.BaseList != null)
            {
                foreach (var baseType in decl.BaseList.Types)
                {
                    if (baseType.Type is not GenericNameSyntax gen) continue;
                    if (!HandlerInterfaces.Contains(gen.Identifier.Text, StringComparer.Ordinal)) continue;

                    var first = gen.TypeArgumentList.Arguments.FirstOrDefault();
                    if (first == null) continue;

                    if (model.GetSymbolInfo(first).Symbol is INamedTypeSymbol bound)
                    {
                        requests.Add(RequestKey(bound));
                        continue;
                    }

                    var shortName = first.ToString().Split('.').Last().Split('<').First();
                    if (shortName.Length > 0) requests.Add("~" + shortName);
                }
            }

            if (requests.Count == 0) return;

            var typeKey = g.ById(typeId)!.Key;
            var entry = methodsByOwner.GetValueOrDefault(typeKey)?
                .FirstOrDefault(n => n.Short.EndsWith(".Handle", StringComparison.Ordinal)
                                     || n.Short.EndsWith(".HandleAsync", StringComparison.Ordinal)
                                     || n.Short.EndsWith(".Consume", StringComparison.Ordinal));

            var target = entry?.Id ?? typeId;
            foreach (var request in requests)
            {
                if (!_handlersByRequest.TryGetValue(request, out var list))
                    _handlersByRequest[request] = list = new List<int>();
                if (!list.Contains(target)) list.Add(target);

                var shortKey = ShortOfRequestKey(request);
                if (!_requestKeysByShort.TryGetValue(shortKey, out var keys))
                    _requestKeysByShort[shortKey] = keys = new HashSet<string>(StringComparer.Ordinal);
                keys.Add(request);
            }
        }

        /// <summary>
        /// Identity of a request type. Uses the original definition so CreateOrder and
        /// CreateOrder&lt;T&gt; closed over something do not diverge. Assembly-qualified for the
        /// same reason <see cref="Key"/> is: two projects that declare the same request name must
        /// not share a dispatch entry, and a handler in one project must still match a request
        /// declared in another.
        /// </summary>
        private static string RequestKey(INamedTypeSymbol type) =>
            (type.OriginalDefinition.ContainingAssembly?.Name ?? "") + "|" +
            type.OriginalDefinition.ToDisplayString(KeyFormat);

        private static string ShortOfRequestKey(string key)
        {
            if (key.StartsWith('~')) return key[1..];

            var trimmed = key.Split('<')[0];

            // Drop the assembly qualifier and any global:: prefix before taking the last dot
            // segment. An assembly name contains dots, so splitting on '.' alone would return the
            // tail of the assembly rather than the request name.
            var bar = trimmed.LastIndexOf('|');
            if (bar >= 0) trimmed = trimmed[(bar + 1)..];

            var colons = trimmed.LastIndexOf("::", StringComparison.Ordinal);
            if (colons >= 0) trimmed = trimmed[(colons + 2)..];

            var dot = trimmed.LastIndexOf('.');
            return dot < 0 ? trimmed : trimmed[(dot + 1)..];
        }

        // ---------------------------------------------------------------- pass 2

        public void Pass2_Bodies(Action<string>? progress)
        {
            progress?.Invoke("pass 2: call edges");

            foreach (var unit in comps.BindOrder)
            {
                var tree = unit.Tree;
                if (Skip(tree)) continue;
                var model = unit.Model();
                _currentAssembly = unit.Compilation.AssemblyName ?? "";
                _currentProject = unit.Project;
                var root = tree.GetRoot();

                // Top-level statements have no containing method declaration. Without this branch
                // every Program.cs written in the modern style is invisible, which silently drops
                // all DI registrations and minimal API routes.
                var globals = root.ChildNodes().OfType<GlobalStatementSyntax>().ToList();
                if (globals.Count > 0)
                {
                    var stem = Path.GetFileNameWithoutExtension(tree.FilePath);
                    if (stem.Length == 0) stem = "Program";

                    var entryId = SyntheticNode(
                        $"toplevel::{tree.FilePath}",
                        $"{stem}.<top-level statements>",
                        $"{stem}.<top-level>",
                        "method",
                        globals[0]);

                    AddTag(g.ById(entryId)!, "startup");
                    foreach (var gs in globals) ScanBody(gs, model, entryId);
                }

                foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
                {
                    int ownerId;
                    switch (member)
                    {
                        case MethodDeclarationSyntax or ConstructorDeclarationSyntax:
                        {
                            if (model.GetDeclaredSymbol(member) is not IMethodSymbol m) continue;
                            if (!_idByKey.TryGetValue(Key(m), out ownerId)) continue;
                            break;
                        }
                        // Property accessor bodies used to be skipped entirely: GetDeclaredSymbol
                        // returns an IPropertySymbol here, never an IMethodSymbol.
                        case PropertyDeclarationSyntax pd:
                        {
                            if (model.GetDeclaredSymbol(pd) is not { } p) continue;
                            if (!_idByKey.TryGetValue(Key(p), out ownerId)) continue;
                            break;
                        }
                        default:
                            continue;
                    }

                    ScanBody(member, model, ownerId);
                }
            }

            Dbg.Log($"pass 2: {g.Edges.Count} edges, {UnresolvedCallSites} unresolved call site(s)");
        }

        private void ScanBody(SyntaxNode body, SemanticModel model, int defaultOwner)
        {
            // Route lambdas are attributed to their own node so that a trace through a minimal API
            // endpoint shows the endpoint, not the whole of Program.cs.
            Dictionary<SyntaxNode, int>? claims = null;
            foreach (var inv in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                TryMinimalApiRoute(inv, model, defaultOwner, ref claims);
            }

            int OwnerOf(SyntaxNode node)
            {
                if (claims == null) return defaultOwner;
                for (var cur = node; cur != null && cur != body; cur = cur.Parent)
                {
                    if (claims.TryGetValue(cur, out var id)) return id;
                }
                return defaultOwner;
            }

            foreach (var inv in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                // nameof(x) is an operator, not a call. Roslyn models it as an invocation with no
                // symbol, so counting it as a binding failure inflates the unresolved rate with
                // something that was never going to bind -- 180 of the first 250 samples on a
                // real solution were nameof.
                if (inv.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" }) continue;

                var owner = OwnerOf(inv);
                var info = model.GetSymbolInfo(inv);
                var target = info.Symbol as IMethodSymbol ?? info.CandidateSymbols.FirstOrDefault() as IMethodSymbol;

                TotalCallSites++;
                if (target == null && info.CandidateSymbols.Length == 0)
                {
                    UnresolvedCallSites++;

                    // The whole invocation, not just the callee. Arguments are where a
                    // source-generated or otherwise unbound symbol usually appears, and the point
                    // of recording a failure is to be able to find what it was about.
                    RecordUnresolved("call", inv, inv.ToString(), "no-candidate-symbol");
                }
                else if (info.Symbol == null && info.CandidateSymbols.Length > 1)
                {
                    // Counted as unresolved, not merely recorded. The edge below still points at
                    // the first candidate, but that is a coin toss between overloads: the call did
                    // not bind to a symbol, and leaving it out of UnresolvedCallSites let an
                    // ambiguous site sit in the denominator as if it had resolved. On a real
                    // solution 17 such sites coexisted with a reported 100.0%.
                    UnresolvedCallSites++;
                    RecordUnresolved("call", inv, inv.ToString(), "ambiguous-overload");
                }

                if (target != null && target.Locations.Any(l => l.IsInSource))
                {
                    Link(owner, NodeFor(target.OriginalDefinition, "method"), EdgeKind.Call);
                }

                TryMediator(inv, model, owner);
                TryDiRegistration(inv, model);
                TryConventionRegistration(inv, model);
            }

            // Member accesses and bare writes are walked in document order together, so the first
            // write site in the file wins the edge's Site no matter which syntax form it takes.
            foreach (var node in body.DescendantNodes())
            {
                switch (node)
                {
                    case MemberAccessExpressionSyntax ma:
                    {
                        var sym = model.GetSymbolInfo(ma).Symbol;
                        if (sym is IPropertySymbol or IFieldSymbol)
                            EmitMemberRole(OwnerOf(ma), sym, MemberRole(ma), ma);
                        break;
                    }

                    case IdentifierNameSyntax id when id.Parent is not MemberAccessExpressionSyntax:
                    {
                        var role = BareRole(id, model);
                        if (role is not null && model.GetSymbolInfo(id).Symbol is { } bare && !InInitializer(id))
                            EmitMemberRole(OwnerOf(id), bare, role.Value, id);
                        break;
                    }
                }
            }

            // Object initializers and with-expressions are their own change; leave their property
            // edges exactly as they were so the two commits stay separable.
            foreach (var assign in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assign.Left is IdentifierNameSyntax id &&
                    InInitializer(assign) &&
                    model.GetSymbolInfo(id).Symbol is IPropertySymbol prop &&
                    prop.Locations.Any(l => l.IsInSource))
                {
                    Link(OwnerOf(assign), NodeFor(prop.OriginalDefinition, "property"), EdgeKind.Call, "prop");
                }
            }

            foreach (var oc in body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(oc.Type).Symbol is not INamedTypeSymbol t) continue;

                if (t.Locations.Any(l => l.IsInSource))
                {
                    var kind = t.TypeKind == TypeKind.Interface ? "interface" : "type";
                    Link(OwnerOf(oc), NodeFor(t.OriginalDefinition, kind), EdgeKind.Construct);
                    continue;
                }

                RecordExternal(t, oc.Type, _currentAssembly);
            }

            foreach (var declaration in body.DescendantNodes().OfType<VariableDeclarationSyntax>())
            {
                if (model.GetSymbolInfo(declaration.Type).Symbol is INamedTypeSymbol vt &&
                    !vt.Locations.Any(l => l.IsInSource))
                {
                    RecordExternal(vt, declaration.Type, _currentAssembly);
                }
            }
        }

        /// <summary>
        /// The role a member access carries. An assignment reads like a write unless it is
        /// compound, and ++/-- is both; everything else is a read. ref/out are classified in their
        /// own change, so here they fall through to read.
        /// </summary>
        private static EdgeRole MemberRole(MemberAccessExpressionSyntax ma) => ma.Parent switch
        {
            AssignmentExpressionSyntax a when a.Left == ma =>
                a.Kind() == SyntaxKind.SimpleAssignmentExpression
                    ? EdgeRole.Write
                    : EdgeRole.Read | EdgeRole.Write,
            PostfixUnaryExpressionSyntax pu when pu.Operand == ma &&
                pu.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression
                => EdgeRole.Read | EdgeRole.Write,
            PrefixUnaryExpressionSyntax pr when pr.Operand == ma &&
                pr.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression
                => EdgeRole.Read | EdgeRole.Write,
            _ => EdgeRole.Read
        };

        /// <summary>
        /// The role a bare identifier target carries, or null when it is not a member write. Bare
        /// means written without a receiver ("Count = 1", "Count++"). The old code recorded the
        /// property form and dropped every field form, which is why constructor writes to readonly
        /// fields never appeared in the graph.
        /// </summary>
        private static EdgeRole? BareRole(IdentifierNameSyntax id, SemanticModel model)
        {
            switch (id.Parent)
            {
                case AssignmentExpressionSyntax a when a.Left == id:
                    if (model.GetSymbolInfo(id).Symbol is not (IPropertySymbol or IFieldSymbol)) return null;
                    return a.Kind() == SyntaxKind.SimpleAssignmentExpression
                        ? EdgeRole.Write
                        : EdgeRole.Read | EdgeRole.Write;

                case PostfixUnaryExpressionSyntax pu when pu.Operand == id &&
                    pu.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
                case PrefixUnaryExpressionSyntax pr when pr.Operand == id &&
                    pr.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression:
                    return model.GetSymbolInfo(id).Symbol is IPropertySymbol or IFieldSymbol
                        ? EdgeRole.Read | EdgeRole.Write
                        : null;

                default:
                    return null;
            }
        }

        private static bool InInitializer(SyntaxNode node) =>
            node.Ancestors().OfType<InitializerExpressionSyntax>().Any(init =>
                init.Parent is ObjectCreationExpressionSyntax
                    or ImplicitObjectCreationExpressionSyntax
                    or WithExpressionSyntax);

        /// <summary>
        /// Records a member access with its role on the single deduped Call edge. A read on one
        /// line and a write on another merge into Read|Write with the first write site, which keeps
        /// read+write from inflating into two edges the caller would have to reconcile.
        /// </summary>
        private void EmitMemberRole(int owner, ISymbol sym, EdgeRole role, SyntaxNode at)
        {
            if (!sym.Locations.Any(l => l.IsInSource)) return;

            var (kind, note) = sym switch
            {
                IPropertySymbol => ("property", "prop"),
                IFieldSymbol f when f.ContainingType?.TypeKind == TypeKind.Enum => ("enum-member", "enum-member"),
                IFieldSymbol => ("field", "field"),
                _ => ("", "")
            };
            if (kind.Length == 0) return;

            LinkMemberAccess(owner, NodeFor(sym.OriginalDefinition, kind), role, note, at);
        }

        private void LinkMemberAccess(int from, int to, EdgeRole role, string note, SyntaxNode at)
        {
            if (from == to) return;

            var key = (from, to, EdgeKind.Call);
            if (_edgeByKey.TryGetValue(key, out var edge))
            {
                edge.Role = (edge.Role ?? 0) | role;
                if ((role & EdgeRole.Write) != 0 && edge.Site is null) edge.Site = SiteOf(at);
                return;
            }

            if (!_dedupe.Add(key)) return;
            edge = new Edge
            {
                From = from,
                To = to,
                Kind = EdgeKind.Call,
                Note = note,
                Role = role,
                // The write location, not the read. A read-only edge keeps Site null.
                Site = (role & EdgeRole.Write) != 0 ? SiteOf(at) : null
            };
            g.Edges.Add(edge);
            _edgeByKey[key] = edge;
        }

        private static readonly Dictionary<string, string> MapVerbs = new(StringComparer.Ordinal)
        {
            ["MapGet"] = "GET",
            ["MapPost"] = "POST",
            ["MapPut"] = "PUT",
            ["MapDelete"] = "DELETE",
            ["MapPatch"] = "PATCH",
            ["Map"] = "ANY"
        };

        /// <summary>
        /// Recognises minimal API endpoint registrations such as app.MapGet("/orders", Handler).
        /// A lambda handler gets its own synthetic node; a method group is tagged in place.
        /// </summary>
        private void TryMinimalApiRoute(
            InvocationExpressionSyntax inv,
            SemanticModel model,
            int owner,
            ref Dictionary<SyntaxNode, int>? claims)
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma) return;
            if (!MapVerbs.TryGetValue(ma.Name.Identifier.Text, out var verb)) return;

            var args = inv.ArgumentList.Arguments;
            if (args.Count == 0) return;

            // The framework signature is Map*(pattern, handler), and the old code read exactly
            // that: two arguments, a literal first. Codebases routinely wrap it. The Clean
            // Architecture template -- and anything modelled on it -- declares its own
            // Map*(this IEndpointRouteBuilder, Delegate handler, string pattern = "") so that the
            // OpenAPI operation id comes from the method name, and calls it as
            // groupBuilder.MapPost(CreateTodoItem). One argument, no literal, both checks fail, and
            // an entire HTTP surface goes missing without a single unresolved site to show for it.
            //
            // So neither position is assumed. The pattern is whichever argument is a string
            // literal, the handler is whichever resolves to something callable, and either may be
            // absent.
            var literal = args
                .Select(a => a.Expression)
                .OfType<LiteralExpressionSyntax>()
                .FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression));

            var pattern = literal?.Token.ValueText ?? string.Empty;

            var handler = args
                .Select(a => a.Expression)
                .FirstOrDefault(x => x is AnonymousFunctionExpressionSyntax
                                     || (x is not LiteralExpressionSyntax && IsCallable(x, model)));

            if (handler == null) return;

            // A pattern only describes a URL when it is the whole of one. Inside a group the
            // prefix lives on the MapGroup call, and templates like this one build it by
            // reflection over the type -- app.MapGroup(type.GetProperty("RoutePrefix") ?? default)
            // -- so there is no string to read at any point in the source.
            //
            // Rather than print a path that is a guess, the verb is recorded and the path is not.
            // The endpoint still becomes an entrypoint, which is what blast-radius and entrypoints
            // actually need; a route tag would look identical to one that was read off a literal,
            // and a confident wrong URL is worse here than an absent one.
            var routed = pattern.StartsWith('/');
            var tag = routed ? $"http:{verb} {pattern}" : $"endpoint:{verb}";
            var note = routed ? $"{verb} {pattern}" : verb;

            if (handler is AnonymousFunctionExpressionSyntax lambda)
            {
                var stem = Path.GetFileNameWithoutExtension(inv.SyntaxTree.FilePath);
                if (stem.Length == 0) stem = "Endpoints";

                var label = $"{stem}.{note}";
                var span = inv.GetLocation().SourceSpan;
                var routeId = SyntheticNode(
                    $"route::{inv.SyntaxTree.FilePath}:{span.Start}",
                    label,
                    label,
                    "method",
                    inv);

                AddTag(g.ById(routeId)!, tag);
                Link(owner, routeId, EdgeKind.Route, note);

                claims ??= new Dictionary<SyntaxNode, int>();
                claims[lambda] = routeId;
                return;
            }

            var info = model.GetSymbolInfo(handler);
            var method = info.Symbol as IMethodSymbol ?? info.CandidateSymbols.FirstOrDefault() as IMethodSymbol;
            if (method == null || !method.Locations.Any(l => l.IsInSource)) return;

            var targetId = NodeFor(method.OriginalDefinition, "method");
            AddTag(g.ById(targetId)!, tag);
            Link(owner, targetId, EdgeKind.Route, note);
        }

        /// <summary>
        /// Whether an expression names something that could be an endpoint handler.
        ///
        /// A method group binds to a method symbol; a local or field holding a delegate binds to
        /// something whose type is a delegate. Both are handlers. Deliberately narrow: without
        /// this, any Map* overload taking a string and an options object would look like a route.
        /// </summary>
        private static bool IsCallable(ExpressionSyntax expression, SemanticModel model)
        {
            var info = model.GetSymbolInfo(expression);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

            if (symbol is IMethodSymbol) return true;

            return model.GetTypeInfo(expression).Type?.TypeKind == TypeKind.Delegate;
        }

        /// <summary>
        /// Resolves mediator request types from Send/Publish invocations to their handlers.
        /// Publish fans out, so every registered handler is linked.
        ///
        /// Matching is by semantic identity. When the request type binds, only an exact identity
        /// match or a handler whose own request never bound is accepted -- a handler registered
        /// for a different namespace with the same class name is deliberately not linked, since
        /// that is a false dispatch rather than a missing edge.
        /// </summary>
        private void TryMediator(InvocationExpressionSyntax inv, SemanticModel model, int fromId)
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma) return;
            var name = ma.Name.Identifier.Text;

            // Invoke/InvokeAsync is Wolverine's request-response form; the rest are MediatR's and
            // MassTransit's. Adding them costs nothing, because the argument still has to be a
            // type declared in this repository before anything is linked.
            if (name is not ("Send" or "Publish" or "SendAsync" or "PublishAsync"
                             or "Invoke" or "InvokeAsync")) return;

            // A generic argument on the call names the message directly, and it outranks the type
            // of what was passed. MassTransit's message-initializer form is the reason:
            //   bus.Publish<OrderSubmitted>(new { OrderId = id })
            // passes an anonymous type, so reading the argument's type yields the anonymous type
            // and the dispatch resolves to nothing -- silently, since an anonymous type is not
            // declared in source and the guard below simply returns.
            INamedTypeSymbol? requestSymbol = null;
            if (ma.Name is GenericNameSyntax generic &&
                generic.TypeArgumentList.Arguments.Count == 1 &&
                model.GetSymbolInfo(generic.TypeArgumentList.Arguments[0]).Symbol is INamedTypeSymbol explicitMessage)
            {
                requestSymbol = explicitMessage;
            }

            var arg = inv.ArgumentList.Arguments.FirstOrDefault();
            if (arg == null && requestSymbol == null) return;

            // A dispatch carries the message and then only plumbing: a CancellationToken, a
            // configure lambda, a context. It never takes a host and a port.
            //
            // TcpLogClient.SendAsync(Payload, _settings.Host, _settings.Port) got through the
            // source-declared guard because Payload is declared in the repository, and was
            // reported as a message with no handler -- sending the reader looking for a consumer
            // that was never meant to exist. Same family as the HttpClient.SendAsync case, and
            // the name is no more the signal here than it was there.
            foreach (var extra in inv.ArgumentList.Arguments.Skip(1))
            {
                var type = model.GetTypeInfo(extra.Expression).Type;
                if (type != null && IsPrimitiveArgument(type)) return;
            }

            if (requestSymbol == null && arg != null)
            {
                if (arg.Expression is ObjectCreationExpressionSyntax oc)
                    requestSymbol = model.GetSymbolInfo(oc.Type).Symbol as INamedTypeSymbol;
                requestSymbol ??= model.GetTypeInfo(arg.Expression).Type as INamedTypeSymbol;
            }

            // HttpClient.SendAsync, HttpMessageHandler.SendAsync, a channel's Publish: the method
            // name is not the signal. A mediator dispatch carries a request type declared in this
            // repository; an HTTP call carries HttpRequestMessage. Matching on the name alone
            // reported test HTTP calls as messages with no handler.
            if (requestSymbol != null && !requestSymbol.OriginalDefinition.Locations.Any(l => l.IsInSource))
            {
                return;
            }

            string shortName;
            if (requestSymbol is { Name.Length: > 0 })
            {
                var key = RequestKey(requestSymbol);
                if (_handlersByRequest.TryGetValue(key, out var exact))
                {
                    Emit(exact, requestSymbol.Name, 1.0, "semantic-request");
                    return;
                }

                shortName = requestSymbol.Name;

                // The handler side could not bind its own request type; a name match is the best
                // available evidence, so link it but say so.
                if (_handlersByRequest.TryGetValue("~" + shortName, out var unbound))
                {
                    Emit(unbound, shortName, 0.7, "short-name-match");
                    return;
                }

                if (_requestKeysByShort.ContainsKey(shortName))
                {
                    AmbiguousMessageDispatches++;
                    RecordUnresolved("mediatr", inv, inv.ToString(), "ambiguous-request-name");
                }
                else
                {
                    UnmatchedMessageDispatches++;
                    RecordUnresolved("mediatr", inv, inv.ToString(), "no-handler");
                }

                return;
            }

            shortName = ma.Name is GenericNameSyntax bare && bare.TypeArgumentList.Arguments.Count == 1
                ? bare.TypeArgumentList.Arguments[0].ToString().Split('.').Last().Split('<').First()
                : arg?.Expression is ObjectCreationExpressionSyntax raw
                    ? raw.Type.ToString().Split('.').Last().Split('<').First()
                    : "";
            if (shortName.Length == 0) return;

            if (!_requestKeysByShort.TryGetValue(shortName, out var candidates))
            {
                UnmatchedMessageDispatches++;
                RecordUnresolved("mediatr", inv, inv.ToString(), "no-handler");
                return;
            }

            if (candidates.Count > 1)
            {
                AmbiguousMessageDispatches++;
                RecordUnresolved("mediatr", inv, inv.ToString(), "ambiguous-request-name");
                Dbg.Log($"mediator: '{shortName}' matches {candidates.Count} request types; dispatch skipped");
                return;
            }

            if (_handlersByRequest.TryGetValue(candidates.First(), out var only))
                Emit(only, shortName, 0.7, "short-name-match");

            void Emit(List<int> handlers, string display, double confidence, string source)
            {
                foreach (var handlerId in handlers)
                {
                    Link(fromId, handlerId, EdgeKind.Mediatr, $"via {name}({display})", confidence, source, inv);
                }
            }
        }

        /// <summary>
        /// Strings, numbers and the like. Plumbing arguments -- tokens, lambdas, contexts -- are
        /// none of these, so this separates a dispatch from a transport call without needing a
        /// list of transport types to exclude.
        /// </summary>
        private static bool IsPrimitiveArgument(ITypeSymbol type) =>
            type.SpecialType is SpecialType.System_String or SpecialType.System_Boolean
                or SpecialType.System_Char or SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64
                or SpecialType.System_Single or SpecialType.System_Double
                or SpecialType.System_Decimal;

        /// <summary>
        /// Lifetime by registration method name. TryAdd* is the form library authors use so a host
        /// can override the default, and Keyed* is the .NET 8 multi-implementation form; both are
        /// as much a real binding as Add*. Extension-method registrations such as
        /// services.AddApplication() need no special case: pass 2 walks the extension method body
        /// too, so the Add* calls inside it are seen where they are written.
        /// </summary>
        private static readonly Dictionary<string, string> DiLifetimes = new(StringComparer.Ordinal)
        {
            ["AddScoped"] = "scoped",
            ["AddSingleton"] = "singleton",
            ["AddTransient"] = "transient",
            ["TryAddScoped"] = "scoped",
            ["TryAddSingleton"] = "singleton",
            ["TryAddTransient"] = "transient",
            ["AddKeyedScoped"] = "scoped",
            ["AddKeyedSingleton"] = "singleton",
            ["AddKeyedTransient"] = "transient",
            ["TryAddKeyedScoped"] = "scoped",
            ["TryAddKeyedSingleton"] = "singleton",
            ["TryAddKeyedTransient"] = "transient",
            ["AddHostedService"] = "hosted"
        };

        /// <summary>
        /// Tracks dependency injection service registrations, resolving the type arguments through
        /// the semantic model so that same-named types in different namespaces stay distinct.
        /// Handles generic arguments, typeof() pairs, keyed registrations and factory lambdas whose
        /// body constructs the implementation directly.
        /// </summary>
        private void TryDiRegistration(InvocationExpressionSyntax inv, SemanticModel model)
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma) return;
            if (ma.Name is not SimpleNameSyntax simple) return;
            if (!DiLifetimes.TryGetValue(simple.Identifier.Text, out var lifetime)) return;

            var args = inv.ArgumentList.Arguments;

            var types = simple is GenericNameSyntax gen
                ? gen.TypeArgumentList.Arguments.ToList()
                : args.Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().Select(t => t.Type).ToList();

            // AddSingleton(new Cache()) and AddScoped(sp => Build(sp)) name no type anywhere the
            // syntax can reach. The container still gets a service; csmesh cannot say which.
            if (types.Count == 0)
            {
                RecordUnresolved("di", inv, inv.Expression.ToString(), "no-type-argument");
                return;
            }

            if (lifetime == "hosted")
            {
                if (Resolve(types[0]) is { } hosted) AddTag(g.ById(hosted.Id)!, "hosted");
                return;
            }

            var key = simple.Identifier.Text.Contains("Keyed", StringComparison.Ordinal)
                ? args.Select(a => a.Expression).OfType<LiteralExpressionSyntax>()
                      .Select(l => l.Token.ValueText).FirstOrDefault(v => v.Length > 0)
                : null;

            if (types.Count == 1)
            {
                var self = Resolve(types[0]);
                if (self == null) return;

                // services.AddScoped<IStore>(sp => new SqlStore(...)): the service is the type
                // argument and the implementation only exists inside the lambda.
                var produced = FactoryImplementation(args, model);
                if (produced is { } impl && impl.Id != self.Value.Id)
                {
                    Bind(self.Value.Id, impl.Id, impl.Confidence, impl.Source);
                    return;
                }

                Tag(self.Value.Id);
                return;
            }

            if (types.Count != 2) return;

            var service = Resolve(types[0]);
            var implementation = Resolve(types[1]);

            // Resolve already recorded why. Half a pair is still a binding that will not be drawn,
            // and the implementation is tagged so it is at least visible as wired.
            if (implementation == null) return;

            if (service == null)
            {
                Tag(implementation.Value.Id);
                return;
            }

            var confidence = service.Value.Semantic && implementation.Value.Semantic ? 1.0 : 0.65;
            var source = confidence >= 1.0 ? "semantic-registration" : "short-name-match";
            Bind(service.Value.Id, implementation.Value.Id, confidence, source);
            return;

            (int Id, bool Semantic)? Resolve(TypeSyntax t) => ResolveTypeNode(t, model);

            void Tag(int id)
            {
                var node = g.ById(id)!;
                AddTag(node, "di:" + lifetime);
                if (key != null) AddTag(node, "keyed:" + key);
            }

            void Bind(int service, int impl, double confidence, string source)
            {
                Tag(impl);
                if (service == impl) return;

                var note = key == null ? lifetime : $"{lifetime} keyed:{key}";
                Link(service, impl, EdgeKind.DiBinding, note, confidence, source, inv);

                // Only a confident binding is allowed to rank an implementation first in 'impl'
                // and in a trace; a name-matched guess should not outrank the real registration.
                if (confidence >= Edge.TrustThreshold) _diBoundPairs.Add((service, impl));
            }
        }

        /// <summary>Provider methods whose type argument names the type that will come back.</summary>
        private static readonly string[] ResolutionMethods =
        {
            "GetRequiredService", "GetService", "GetRequiredKeyedService", "GetKeyedService"
        };

        /// <summary>
        /// Pulls the implementation out of a factory registration, from either shape it takes.
        ///
        /// A direct construction -- sp =&gt; new SqlStore(...) -- is unambiguous. The other shape is
        /// the alias: register the concrete type once, then expose it through each interface with
        /// sp =&gt; sp.GetRequiredService&lt;SqlStore&gt;(). This used to be refused on the grounds that
        /// an alias points at another registration rather than being a binding of its own.
        ///
        /// True, and beside the point. The question csmesh exists to answer is which class runs
        /// when the container is asked for the interface, and the alias states the answer outright.
        /// Refusing to read it means 'impl ITenantContext' falls back to ranking every implementor
        /// by name -- guessing at something written down two lines away. On a codebase that wires
        /// itself this way it is not an edge case; it is most of the container.
        ///
        /// Scored the same as a direct construction, and for the same reason: both name the type
        /// outright and both sit inside a lambda that could in principle branch. Below
        /// <see cref="Edge.TrustThreshold"/> it would not rank first in 'impl', which would leave
        /// the container's actual answer sitting third in an alphabetical list -- the exact failure
        /// the edge was added to prevent. The source is recorded separately so the provenance
        /// stays visible without costing the ranking.
        /// </summary>
        private (int Id, double Confidence, string Source)? FactoryImplementation(
            SeparatedSyntaxList<ArgumentSyntax> args, SemanticModel model)
        {
            foreach (var arg in args)
            {
                if (arg.Expression is not AnonymousFunctionExpressionSyntax lambda) continue;

                var body = lambda.ExpressionBody
                           ?? lambda.Block?.DescendantNodes()
                               .OfType<ReturnStatementSyntax>()
                               .Select(r => r.Expression)
                               .LastOrDefault(e => e != null);

                if (body is ObjectCreationExpressionSyntax creation)
                {
                    if (ResolveTypeNode(creation.Type, model) is { } made)
                    {
                        return (made.Id, 0.9, "factory-lambda");
                    }

                    continue;
                }

                if (AliasTarget(body) is not { } alias) continue;
                if (ResolveTypeNode(alias, model) is { } resolved)
                {
                    return (resolved.Id, 0.9, "factory-alias");
                }
            }

            return null;
        }

        /// <summary>
        /// The type argument of a provider resolution call, or null when the expression is
        /// something else. Handles ActivatorUtilities.CreateInstance&lt;T&gt;(sp) as well, which is
        /// a construction dressed as a call.
        /// </summary>
        private static TypeSyntax? AliasTarget(ExpressionSyntax? body)
        {
            if (body is not InvocationExpressionSyntax call) return null;
            if (call.Expression is not MemberAccessExpressionSyntax access) return null;
            if (access.Name is not GenericNameSyntax generic) return null;
            if (generic.TypeArgumentList.Arguments.Count != 1) return null;

            var name = generic.Identifier.Text;
            var known = ResolutionMethods.Contains(name, StringComparer.Ordinal)
                        || name == "CreateInstance";

            return known ? generic.TypeArgumentList.Arguments[0] : null;
        }

        /// <summary>
        /// Maps a type argument to a graph node, preferring semantic resolution and falling back to
        /// an unambiguous short name match when the type could not be bound. The flag says which
        /// path was taken, so a guess can be recorded at lower confidence instead of passing for
        /// a compiler-verified fact.
        /// </summary>
        private (int Id, bool Semantic)? ResolveTypeNode(TypeSyntax syntax, SemanticModel model)
        {
            var symbol = model.GetSymbolInfo(syntax).Symbol as INamedTypeSymbol;

            if (symbol != null && symbol.OriginalDefinition.Locations.Any(l => l.IsInSource))
            {
                var definition = symbol.OriginalDefinition;
                var id = NodeFor(definition, definition.TypeKind == TypeKind.Interface ? "interface" : "type");
                return (id, true);
            }

            // The compiler knows this type and it is not in the source being indexed: a package, or
            // a project the scope left out. The registration is real, the binding cannot be drawn,
            // and until now nothing said so -- 'unresolved --kind di' answered "none", which reads
            // as "DI is fully understood" rather than "DI was never attempted here".
            if (symbol != null)
            {
                RecordUnresolved("di", syntax, syntax.ToString(), "type-outside-index");
                Dbg.Log($"di: '{syntax}' resolves to {symbol.ContainingAssembly?.Name ?? "an assembly"} outside the index");
                return null;
            }

            var shortName = syntax.ToString().Split('.').Last().Split('<').First();
            Node? match = null;
            foreach (var n in g.Nodes)
            {
                if (n.Kind is not ("interface" or "type")) continue;
                if (!string.Equals(n.Short, shortName, StringComparison.Ordinal)) continue;
                if (match != null)
                {
                    AmbiguousDiRegistrations++;
                    RecordUnresolved("di", syntax, syntax.ToString(), "ambiguous-type-name");
                    Dbg.Log($"di: '{shortName}' is ambiguous across namespaces; registration skipped");
                    return null;
                }
                match = n;
            }

            if (match != null) return (match.Id, false);

            // Neither the compiler nor a name match knew it. Usually a missing reference, which
            // means the whole registration is invisible rather than merely unlinked.
            RecordUnresolved("di", syntax, syntax.ToString(), "type-not-found");
            return null;
        }

        /// <summary>
        /// Registration by convention rather than by name.
        ///
        /// Scrutor's Scan, MediatR's assembly registration, FluentValidation and AutoMapper all
        /// bind whole families of types with a single call that names none of them. On a Clean
        /// Architecture solution this is not an edge case -- it is how the container is wired, and
        /// an indexer that only reads AddScoped&lt;A, B&gt;() reports "no DI bindings resolved" for
        /// the entire codebase. That reads as a project with no dependency injection rather than
        /// one this tool could not follow, which is the worse of the two failures.
        ///
        /// These bindings are recorded at reduced confidence on purpose: the assembly filter, the
        /// lifetime and the exclusion rules are evaluated at startup, not here. What is asserted is
        /// "this interface is wired to its implementations by a scan", not "this exact pair was
        /// registered".
        /// </summary>
        private void TryConventionRegistration(InvocationExpressionSyntax inv, SemanticModel model)
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma) return;
            if (ma.Name is not SimpleNameSyntax simple) return;

            var name = simple.Identifier.Text;

            if (name == "Scan")
            {
                // Mapster's TypeAdapterConfig also has Scan(assemblies), and it reads mapping
                // profiles rather than registering services. Matching on the method name alone
                // reported it as a container registration, and 'doctor' then claimed the DI
                // container was wired by scanning when nothing of the sort had happened.
                if (!IsServiceCollection(ma.Expression, model)) return;

                RegisterScrutorScan(inv, model);
                return;
            }

            if (name.StartsWith("AddValidatorsFrom", StringComparison.Ordinal))
            {
                Note(name, inv);
                BindFamily(["AbstractValidator", "IValidator"], "transient", inv);
                return;
            }

            if (name == "AddAutoMapper")
            {
                Note(name, inv);
                BindFamily(["Profile"], "singleton", inv);
                return;
            }

            if (name is "AddMediatR" or "AddMassTransit" or "AddRebus")
            {
                // No binding to add: dispatch is resolved from request types, not from the
                // container. Recorded only so doctor can say the container is wired by scanning.
                Note(name, inv);
            }
        }

        /// <summary>
        /// Whether the receiver of a registration-shaped call is actually a service collection.
        /// Falls back to the written name when the type will not bind, because a repository that
        /// declares its own IServiceCollection is still describing a container.
        /// </summary>
        private static bool IsServiceCollection(ExpressionSyntax receiver, SemanticModel model)
        {
            var type = model.GetTypeInfo(receiver).Type;

            if (type != null && type.TypeKind != TypeKind.Error)
            {
                if (Named(type)) return true;
                return type.AllInterfaces.Any(Named);
            }

            var text = receiver.ToString();
            return text.Contains("service", StringComparison.OrdinalIgnoreCase);

            static bool Named(ITypeSymbol t) =>
                t.Name is "IServiceCollection" or "ServiceCollection";
        }

        private void Note(string helper, SyntaxNode at)
        {
            var entry = $"{helper} @ {SiteOf(at)}";
            if (!g.ScanRegistrations.Contains(entry, StringComparer.Ordinal))
                g.ScanRegistrations.Add(entry);
        }

        /// <summary>
        /// services.Scan(s =&gt; s.FromAssemblyOf&lt;T&gt;().AddClasses(c =&gt; c.AssignableTo&lt;IFoo&gt;())
        ///                     .AsImplementedInterfaces().WithScopedLifetime())
        ///
        /// The lambda is a fluent chain, so the parts are read out of it independently rather than
        /// matched as a shape: builders vary, and a chain that does not match a template exactly
        /// should still contribute what it does say.
        /// </summary>
        private void RegisterScrutorScan(InvocationExpressionSyntax inv, SemanticModel model)
        {
            Note("Scan", inv);

            var lifetime = "scoped";
            var filters = new List<TypeSyntax>();

            foreach (var call in inv.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax inner) continue;

                var member = inner.Name;
                var memberName = member is GenericNameSyntax gn ? gn.Identifier.Text
                    : (member as SimpleNameSyntax)?.Identifier.Text;

                switch (memberName)
                {
                    case "WithSingletonLifetime": lifetime = "singleton"; break;
                    case "WithTransientLifetime": lifetime = "transient"; break;
                    case "WithScopedLifetime": lifetime = "scoped"; break;
                    case "AssignableTo" when member is GenericNameSyntax g1:
                        filters.AddRange(g1.TypeArgumentList.Arguments);
                        break;
                    case "AssignableTo":
                        filters.AddRange(call.ArgumentList.Arguments
                            .Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().Select(t => t.Type));
                        break;
                }
            }

            if (filters.Count == 0)
            {
                // AsImplementedInterfaces() with no AssignableTo filter binds everything in an
                // assembly. FromAssemblyOf<T> still says which assembly, and Node.Project makes
                // that a real constraint rather than a guess about the whole solution, so the
                // scan is applied within that project only and recorded below a filtered one.
                var marker = MarkerProject(inv, model);
                if (marker == null)
                {
                    RecordUnresolved("di", inv, inv.Expression.ToString(), "assembly-scan-unfiltered");
                    return;
                }

                BindProjectImplementations(marker, lifetime, inv);
                return;
            }

            foreach (var filter in filters)
            {
                if (ResolveTypeNode(filter, model) is not { } resolved) continue;
                BindImplementors(resolved.Id, lifetime, inv, "assembly-scan");
            }
        }

        /// <summary>
        /// The project a FromAssemblyOf&lt;T&gt; / FromAssemblyContaining&lt;T&gt; marker points at.
        /// </summary>
        private string? MarkerProject(InvocationExpressionSyntax inv, SemanticModel model)
        {
            foreach (var call in inv.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax ma) continue;
                if (ma.Name is not GenericNameSyntax gen) continue;
                if (!gen.Identifier.Text.StartsWith("FromAssembly", StringComparison.Ordinal)) continue;

                var argument = gen.TypeArgumentList.Arguments.FirstOrDefault();
                if (argument == null) continue;
                if (ResolveTypeNode(argument, model) is not { } resolved) continue;

                var project = g.ById(resolved.Id)?.Project ?? "";
                if (project.Length > 0) return project;
            }

            return null;
        }

        /// <summary>
        /// Every concrete class in one project, bound to the interfaces it implements. This is what
        /// AsImplementedInterfaces does at startup; the difference is that the container evaluates
        /// it against a real assembly and this evaluates it against a project stamp, so it is
        /// recorded well below the threshold at which anything is treated as a fact.
        /// </summary>
        private void BindProjectImplementations(string project, string lifetime, SyntaxNode at)
        {
            foreach (var (baseId, implementors) in _implementorsByBase)
            {
                if (g.ById(baseId) is not { Kind: "interface" }) continue;

                foreach (var implId in implementors)
                {
                    var impl = g.ById(implId);
                    if (impl == null || impl.Project != project) continue;
                    if (impl.Kind == "interface" || impl.Tags.Contains("abstract")) continue;
                    if (impl.Tags.Contains("test")) continue;

                    AddTag(impl, "di:" + lifetime);
                    Link(baseId, implId, EdgeKind.DiBinding, lifetime, BroadScanConfidence, "assembly-scan-broad", at);
                }
            }
        }

        /// <summary>
        /// Binds every implementor of any base type whose simple name matches one of the given
        /// prefixes. Used for helpers that register a family identified by a well-known base type
        /// rather than by a type argument.
        /// </summary>
        private void BindFamily(string[] baseNames, string lifetime, SyntaxNode at)
        {
            foreach (var baseId in _implementorsByBase.Keys.ToList())
            {
                var node = g.ById(baseId);
                if (node == null) continue;

                var simple = node.Short.Split('<')[0];
                if (!baseNames.Contains(simple, StringComparer.Ordinal)) continue;

                BindImplementors(baseId, lifetime, at, "assembly-scan");
            }
        }

        private void BindImplementors(int baseId, string lifetime, SyntaxNode at, string source)
        {
            if (!_implementorsByBase.TryGetValue(baseId, out var implementors)) return;

            foreach (var implId in implementors)
            {
                var impl = g.ById(implId);
                if (impl == null || impl.Kind == "interface") continue;
                if (impl.Tags.Contains("abstract")) continue;

                AddTag(impl, "di:" + lifetime);

                // A scan does not name a pair, so it must not outrank an explicit registration.
                // ScanConfidence sits below TrustThreshold on purpose: _diBoundPairs stays reserved
                // for bindings the compiler confirmed.
                Link(baseId, implId, EdgeKind.DiBinding, lifetime, ScanConfidence, source, at);
            }
        }

        // ---------------------------------------------------------------- pass 3

        public void Pass3_Indirection(Action<string>? progress)
        {
            progress?.Invoke("pass 3: interface and override edges");

            // Member-level override and interface edges were read from the compiler in pass 1.
            // They are emitted here only because the di-bound note is decided by pass 2's
            // container registrations, which do not exist yet when the models are alive.
            foreach (var (baseMember, derivedMember, baseType, derivedType, kind) in _pendingMemberEdges)
            {
                var note = _diBoundPairs.Contains((baseType, derivedType)) ? "di-bound" : null;
                Link(baseMember, derivedMember, kind, note);
            }

            // One type-level edge per (base, implementor) pair, so "what implements this" is
            // answerable even for a type whose members produced no member edge of their own.
            foreach (var (baseId, implementors) in _implementorsByBase)
            {
                var baseNode = g.ById(baseId);
                if (baseNode == null) continue;

                // Interfaces dispatch; base classes are overridden. Both answer "what actually runs".
                var edgeKind = baseNode.Kind == "interface" ? EdgeKind.Interface : EdgeKind.Override;

                foreach (var implId in implementors)
                {
                    if (g.ById(implId) == null) continue;

                    var note = _diBoundPairs.Contains((baseId, implId)) ? "di-bound" : null;
                    Link(baseId, implId, edgeKind, note);
                }
            }
        }
    }
}