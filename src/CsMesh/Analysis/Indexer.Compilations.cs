using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CsMesh.Analysis;

/// <summary>
/// The compilation(s) an index binds against, assembled from the per-project picture scope and the
/// ownership model already computed.
///
/// The whole-solution compilation silently let a global using, a name, or an assembly boundary from
/// one project reach every other: a project that declares the same fully-qualified name as its
/// neighbour collided, and a namespace imported in one project appeared in all of them. One
/// compilation per in-scope project is what stops that, because a project can only see the projects
/// it references.
/// </summary>
public static partial class Indexer
{
    /// <summary>
    /// One syntax tree and the project directories whose compilation must contain it. A multiply
    /// owned tree is a linked or shared file the build compiles into each of those assemblies.
    /// </summary>
    internal readonly record struct OwnedTree(SyntaxTree Tree, IReadOnlyList<string> Owners);

    /// <summary>
    /// The compilations built for one index, the tree each is bound from, and the reference cycles
    /// broken to create them in order.
    /// </summary>
    private sealed class CompilationSet
    {
        public List<CSharpCompilation> Compilations { get; } = [];

        /// <summary>
        /// Every tree to bind, in the order the builder walks them: grouped by the project they are
        /// bound from, and inside a project in the order the trees were collected. A tree owned by
        /// several projects appears once, under the first owner in topological order.
        /// </summary>
        public List<SyntaxTree> BindOrder { get; } = [];

        /// <summary>The compilation a tree is bound from, which owns its semantic model.</summary>
        public Dictionary<SyntaxTree, CSharpCompilation> CompilationOf { get; } = new();

        /// <summary>Reference cycles broken to order the projects, as "dependent -&gt; dependency".</summary>
        public List<string> Cycles { get; } = [];

        /// <summary>Each compilation with the name to report against it, for diagnostics.</summary>
        public List<(string Project, CSharpCompilation Compilation)> Named { get; } = [];

        /// <summary>InternalsVisibleTo items naming a property this parser does not evaluate.</summary>
        public int UnevaluableInternalsVisibleTo { get; set; }

        public SemanticModel ModelOf(SyntaxTree tree) => CompilationOf[tree].GetSemanticModel(tree);

        /// <summary>The assembly name of the compilation a tree belongs to, or empty.</summary>
        public string AssemblyNameOf(SyntaxTree tree) =>
            CompilationOf.TryGetValue(tree, out var compilation) ? compilation.AssemblyName ?? "" : "";
    }

    /// <summary>One in-scope project and the trees its compilation holds.</summary>
    private sealed class ProjectUnit
    {
        public string Directory { get; init; } = string.Empty;
        public string Csproj { get; init; } = string.Empty;

        /// <summary>The MSBuild assembly name, before any disambiguation.</summary>
        public string RealName { get; set; } = "csmesh.index";

        /// <summary>The name the compilation is actually created with.</summary>
        public string AssemblyName { get; set; } = "csmesh.index";

        public List<SyntaxTree> Trees { get; } = [];

        /// <summary>In-scope projects this one declares a ProjectReference to.</summary>
        public List<string> DirectReferences { get; } = [];
    }

    private static CSharpCompilationOptions IndexCompilationOptions() =>
        new(OutputKind.ConsoleApplication, allowUnsafe: true);

    /// <summary>
    /// What a file's owners are, with the empty-string pseudo project for a repository that has no
    /// csproj at all -- there is one compilation and no project boundary to place the file against.
    /// </summary>
    private static IReadOnlyList<string> OwnershipOf(ProjectScope scope, string file)
    {
        if (!scope.HasProjects) return new[] { "" };

        var owners = scope.Owners(file);
        return owners.Count == 0 ? new[] { "" } : owners;
    }

    /// <summary>
    /// Builds the compilation(s) both the full index and the incremental refresh bind against.
    ///
    /// A repository with no csproj keeps the single whole-solution compilation named csmesh.index,
    /// exactly as before. Otherwise there is one compilation per in-scope project, created in
    /// topological order over ProjectReference, each carrying a CompilationReference to every
    /// project in its transitive closure and reusing that project's cached compilation instance.
    /// A CompilationReference exposes the referenced compilation's own declarations but not its
    /// references, so the closure is added explicitly rather than relying on the direct edge.
    /// </summary>
    private static CompilationSet CreateCompilations(
        string root,
        ProjectScope scope,
        IReadOnlyList<OwnedTree> trees,
        IReadOnlyList<MetadataReference> references)
    {
        var set = new CompilationSet();

        if (!scope.HasProjects || scope.LiveDirectories.Count == 0)
        {
            var single = CSharpCompilation.Create(
                "csmesh.index", trees.Select(t => t.Tree), references, IndexCompilationOptions());

            set.Compilations.Add(single);
            set.Named.Add(("", single));

            foreach (var tree in trees)
            {
                set.BindOrder.Add(tree.Tree);
                set.CompilationOf[tree.Tree] = single;
            }

            return set;
        }

        var units = new Dictionary<string, ProjectUnit>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in scope.LiveDirectories)
        {
            units[directory] = new ProjectUnit
            {
                Directory = directory,
                Csproj = ProjectTfm.Single(directory) ?? ""
            };
        }

        foreach (var unit in units.Values)
        {
            if (unit.Csproj.Length == 0) continue;

            foreach (var reference in ProjectScope.ReferencedProjects(unit.Csproj))
            {
                var parent = Path.GetDirectoryName(reference);
                if (parent is not null && units.ContainsKey(parent)) unit.DirectReferences.Add(parent);
            }
        }

        AssignAssemblyNames(root, units);

        var order = TopologicalOrder(root, units, set.Cycles);
        var rank = order
            .Select((directory, index) => (directory, index))
            .ToDictionary(x => x.directory, x => x.index, StringComparer.OrdinalIgnoreCase);

        // A tree goes into every owner's compilation, but is bound once, from the first owner in
        // topological order. Binding it twice would declare the same symbols twice and, with keys
        // that do not yet carry the assembly, merge two projects' ideas of one name. Until commit E
        // makes the key assembly-aware, one binding is also the only correct reading.
        foreach (var entry in trees)
        {
            var owners = entry.Owners
                .Where(units.ContainsKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(owner => rank[owner])
                .ToList();

            if (owners.Count == 0) owners.Add(order[0]);

            foreach (var owner in owners) units[owner].Trees.Add(entry.Tree);
        }

        AddInternalsVisibleTo(root, units, set);

        var created = new Dictionary<string, CSharpCompilation>(StringComparer.OrdinalIgnoreCase);
        var closureCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in order)
        {
            var unit = units[directory];

            var scopedReferences = new List<MetadataReference>(references);
            foreach (var dependency in Closure(directory, units, closureCache))
            {
                if (created.TryGetValue(dependency, out var dependencyCompilation))
                    scopedReferences.Add(dependencyCompilation.ToMetadataReference());
            }

            var compilation = CSharpCompilation.Create(
                unit.AssemblyName, unit.Trees, scopedReferences, IndexCompilationOptions());

            created[directory] = compilation;
            set.Compilations.Add(compilation);
            set.Named.Add((Relative(root, directory), compilation));

            foreach (var tree in unit.Trees)
            {
                if (set.CompilationOf.ContainsKey(tree)) continue;
                set.BindOrder.Add(tree);
                set.CompilationOf[tree] = compilation;
            }
        }

        return set;
    }

    /// <summary>
    /// Gives every in-scope project the assembly name its csproj produces, without the output file
    /// extension. Two projects that share one -- the eight nested fixture csprojs all named Fixture
    /// are the case on this repository under --all -- would give Roslyn two assemblies of one
    /// identity, so each colliding project gets its project-relative path appended after an '@'.
    /// The rule is deterministic: the same tree always yields the same names, in any order.
    /// </summary>
    private static void AssignAssemblyNames(string root, Dictionary<string, ProjectUnit> units)
    {
        var byDirectory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units.Values)
        {
            byDirectory[unit.Directory] = unit.Csproj.Length == 0
                ? "csmesh.index"
                : Path.GetFileNameWithoutExtension(AssemblyNameOf(unit.Csproj));
        }

        var counts = byDirectory.Values
            .GroupBy(name => name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var unit in units.Values)
        {
            var name = byDirectory[unit.Directory];
            unit.RealName = name;
            unit.AssemblyName = counts[name] == 1
                ? name
                : DisambiguatedName(name, Relative(root, unit.Directory));
        }
    }

    /// <summary>
    /// A deterministic, valid assembly name for a project whose natural name is shared. The relative
    /// path is legible but its separators are not legal in an assembly name, so anything but a
    /// letter, digit, dot, underscore or dash becomes an underscore; a short hash of the exact path
    /// keeps two paths that sanitise to the same text distinct. Roslyn rejects a '/' in a name with
    /// CS8203, which is what an earlier rule using the raw path produced.
    /// </summary>
    private static string DisambiguatedName(string name, string relativePath)
    {
        var readable = new string(relativePath
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_')
            .ToArray());

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(relativePath)))[..6];

        return $"{name}_{readable}_{hash}";
    }

    /// <summary>
    /// Synthesizes each project's InternalsVisibleTo attributes into that project's compilation.
    ///
    /// IVT is matched against the friend's assembly name -- which for a source compilation is its
    /// compilation name -- so a friend whose real name was disambiguated (two projects share it)
    /// must be named by the compilation name it actually got, not the real one the csproj wrote.
    /// ResolveFriend does that translation when the real name picks out exactly one in-scope
    /// project; an ambiguous or external friend keeps the written text, since there is no single
    /// compilation to point at. The target's own name being disambiguated does not change its
    /// attributes, so a friend still reads its internals through the CompilationReference.
    /// </summary>
    private static void AddInternalsVisibleTo(
        string root, Dictionary<string, ProjectUnit> units, CompilationSet set)
    {
        var compilationNameByRealName = units.Values
            .GroupBy(unit => unit.RealName, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().AssemblyName, StringComparer.Ordinal);

        foreach (var unit in units.Values)
        {
            if (unit.Csproj.Length == 0) continue;

            var (friends, unevaluable) = InternalsVisibleTo.Read(unit.Csproj);
            set.UnevaluableInternalsVisibleTo += unevaluable;
            if (friends.Count == 0) continue;

            var attributes = friends
                .Select(friend => InternalsVisibleTo.Resolve(friend, compilationNameByRealName))
                .Select(friend => $"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{Escape(friend)}\")]");

            // Preview, because every other tree in the project is parsed with it and Roslyn refuses
            // a compilation whose trees disagree on the language version.
            unit.Trees.Add(CSharpSyntaxTree.ParseText(
                string.Join("\n", attributes),
                new CSharpParseOptions(LanguageVersion.Preview),
                path: $"<internals-visible-to:{Relative(root, unit.Directory)}>"));
        }
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Dependencies before dependents. When no project is ready every remaining project is in a
    /// reference cycle, so the ordinal-smallest is emitted and one of its still-pending references
    /// is recorded and dropped: the compilation for that edge is simply not added, which is the
    /// only thing a cycle permits, and doctor says which one.
    /// </summary>
    private static List<string> TopologicalOrder(
        string root, Dictionary<string, ProjectUnit> units, List<string> cycles)
    {
        var remaining = units.Keys.ToList();
        var order = new List<string>(units.Count);
        var satisfied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(directory => units[directory].DirectReferences
                    .All(reference => !units.ContainsKey(reference) || satisfied.Contains(reference)))
                .OrderBy(directory => Relative(root, directory), StringComparer.Ordinal)
                .ToList();

            if (ready.Count == 0)
            {
                var pick = remaining.OrderBy(directory => Relative(root, directory), StringComparer.Ordinal).First();
                var broken = units[pick].DirectReferences
                    .Where(reference => remaining.Contains(reference, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(reference => Relative(root, reference), StringComparer.Ordinal)
                    .FirstOrDefault();

                if (broken is not null)
                {
                    units[pick].DirectReferences.RemoveAll(
                        reference => reference.Equals(broken, StringComparison.OrdinalIgnoreCase));
                    cycles.Add($"{Relative(root, broken)} -> {Relative(root, pick)}");
                }

                ready = [pick];
            }

            foreach (var directory in ready)
            {
                order.Add(directory);
                satisfied.Add(directory);
                remaining.Remove(directory);
            }
        }

        return order;
    }

    /// <summary>
    /// Every project reachable from <paramref name="start"/> through ProjectReference, memoised. A
    /// visited set makes a cycle terminate rather than recurse.
    /// </summary>
    private static List<string> Closure(
        string start, Dictionary<string, ProjectUnit> units, Dictionary<string, List<string>> cache)
    {
        if (cache.TryGetValue(start, out var cached)) return cached;

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var pending = new Stack<string>(units[start].DirectReferences);

        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (!units.ContainsKey(next) || !seen.Add(next)) continue;

            result.Add(next);
            foreach (var reference in units[next].DirectReferences) pending.Push(reference);
        }

        cache[start] = result;
        return result;
    }

    private static string Relative(string root, string directory) =>
        Path.GetRelativePath(root, directory).Replace('\\', '/');
}
