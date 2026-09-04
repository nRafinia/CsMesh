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
public static class Indexer
{
    private static readonly string[] SkipDirs =
    {
        "/bin/", "/obj/", "/node_modules/", "/.git/", "/.vs/", "/.idea/", "/.svn/",
        "/packages/", "/TestResults/", "/artifacts/", "/.csmesh/"
    };

    public static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (SkipDirs.Any(d => normalized.Contains(d, StringComparison.OrdinalIgnoreCase))) continue;
            if (normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (normalized.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (normalized.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)) continue;
            yield return file;
        }
    }

    public static Graph Build(string root, Action<string>? progress = null)
    {
        var files = EnumerateSourceFiles(root).ToList();
        progress?.Invoke($"parsing {files.Count} files");

        var trees = new List<SyntaxTree>(files.Count);
        var stamps = new List<FileStamp>(files.Count);
        var dirs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }

            trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: file));
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

        // The repository root itself may gain a new source file without any tracked directory changing.
        if (!dirs.ContainsKey("."))
        {
            try { dirs["."] = Directory.GetLastWriteTimeUtc(root).Ticks; } catch { }
        }

        var references = ReferenceSet(root);
        progress?.Invoke($"compiling against {references.Count} references");

        // ConsoleApplication so that top-level statements bind to a real entry point instead of
        // being rejected outright. Diagnostics are advisory here; we never require a clean build.
        var compilation = CSharpCompilation.Create(
            "csmesh.index",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, allowUnsafe: true));

        var graph = new Graph
        {
            Root = root,
            FormatVersion = Graph.CurrentFormatVersion,
            BuiltAt = DateTimeOffset.UtcNow,
            BuiltFromCommit = RepositoryLocator.GitHead(root),
            Files = stamps,
            Dirs = dirs.Select(kv => new DirStamp { Path = kv.Key, Ticks = kv.Value }).ToList(),
            ReferenceCount = references.Count
        };

        var builder = new Builder(graph, compilation);
        builder.Pass1_Declarations(progress);
        builder.Pass2_Bodies(progress);
        builder.Pass3_Indirection(progress);

        AssignProjects(graph);

        graph.UnresolvedCallSites = builder.UnresolvedCallSites;
        graph.TotalCallSites = builder.TotalCallSites;
        graph.AmbiguousDiRegistrations = builder.AmbiguousDiRegistrations;
        graph.AmbiguousMessageDispatches = builder.AmbiguousMessageDispatches;
        graph.UnmatchedMessageDispatches = builder.UnmatchedMessageDispatches;
        return graph;
    }

    /// <summary>
    /// Stamps each node with the nearest .csproj above its source file. Resolved once per
    /// directory rather than once per node; a large solution has thousands of nodes and dozens of
    /// directories.
    /// </summary>
    private static void AssignProjects(Graph graph)
    {
        var byDirectory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.Nodes)
        {
            if (node.File.Length == 0) continue;

            var relative = Path.GetDirectoryName(node.File) ?? "";
            if (byDirectory.TryGetValue(relative, out var cached))
            {
                node.Project = cached;
                continue;
            }

            var project = "";
            var dir = new DirectoryInfo(Path.Combine(graph.Root, relative));
            var stop = Path.GetFullPath(graph.Root);

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

            byDirectory[relative] = project;
            node.Project = project;
        }
    }

    private static List<MetadataReference> ReferenceSet(string root)
    {
        var list = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDir(string dir, int cap)
        {
            if (!Directory.Exists(dir)) return;
            var count = 0;
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
            {
                var name = Path.GetFileName(dll);
                if (!seen.Add(name)) continue;
                try { list.Add(MetadataReference.CreateFromFile(dll)); } catch { }
                if (++count >= cap) return;
            }
        }

        AddDir(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), 400);

        foreach (var bin in Directory.EnumerateDirectories(root, "bin", SearchOption.AllDirectories).Take(80))
        {
            foreach (var cfg in Directory.EnumerateDirectories(bin, "*", SearchOption.AllDirectories).Take(20))
            {
                AddDir(cfg, 200);
            }
        }

        return list;
    }

    private sealed class Builder(Graph g, CSharpCompilation comp)
    {
        private static readonly SymbolDisplayFormat KeyFormat = SymbolDisplayFormat.FullyQualifiedFormat;

        /// <summary>
        /// A scan says a family is wired, not which pair. Below <see cref="Edge.TrustThreshold"/>
        /// so it is reported as inferred and never ranks above an explicit AddScoped.
        /// </summary>
        private const double ScanConfidence = 0.75;

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
        private readonly List<(INamedTypeSymbol Type, TypeDeclarationSyntax Decl, int Id)> _pendingHandlers = new();

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
                var span = l.GetLineSpan();
                file = Path.GetRelativePath(g.Root, l.SourceTree!.FilePath);
                line = span.StartLinePosition.Line + 1;
                endLine = span.EndLinePosition.Line + 1;
            }

            return AddNode(key, FullName(sym), ShortName(sym), kind, file, line, endLine);
        }

        /// <summary>
        /// Creates a node for something that has no Roslyn symbol of its own: top-level statement
        /// bodies and minimal API route lambdas.
        /// </summary>
        private int SyntheticNode(string key, string name, string shortName, string kind, SyntaxNode at)
        {
            if (_idByKey.TryGetValue(key, out var existing)) return existing;

            var span = at.GetLocation().GetLineSpan();
            var file = at.SyntaxTree.FilePath.Length > 0
                ? Path.GetRelativePath(g.Root, at.SyntaxTree.FilePath)
                : "";

            return AddNode(key, name, shortName, kind, file,
                           span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
        }

        private int AddNode(string key, string name, string shortName, string kind, string file, int line, int endLine)
        {
            var node = new Node
            {
                Id = g.Nodes.Count,
                Name = name,
                Short = shortName,
                Kind = kind,
                File = file,
                Line = line,
                EndLine = endLine
            };

            g.Nodes.Add(node);
            _idByKey[key] = node.Id;
            return node.Id;
        }

        /// <summary>
        /// Uniquely identifies a symbol. Parameter types are fully qualified and method arity is
        /// included so overloads such as Handle(List&lt;int&gt;) and Handle(List&lt;string&gt;)
        /// never collapse into one node.
        /// </summary>
        private static string Key(ISymbol s)
        {
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

            return container + "::" + self + "|" + s.Kind;
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
        private void RecordExternalIn(ITypeSymbol? type, SyntaxNode at, int depth = 0)
        {
            if (type == null || depth > 2) return;

            if (type is IArrayTypeSymbol array)
            {
                RecordExternalIn(array.ElementType, at, depth + 1);
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
                    RecordExternal(named, at);
                }
            }

            foreach (var argument in named.TypeArguments) RecordExternalIn(argument, at, depth + 1);
        }

        private void RecordExternal(INamedTypeSymbol type, SyntaxNode at)
        {
            // string, int, object: never what someone is looking for.
            if (type.SpecialType != SpecialType.None) return;

            var assembly = type.ContainingAssembly?.Name ?? "";
            if (assembly.Length == 0) return;
            if (assembly == comp.AssemblyName) return;
            if (FrameworkAssemblyPrefixes.Any(p => assembly.StartsWith(p, StringComparison.Ordinal))) return;

            var name = type.OriginalDefinition.Name;
            if (name.Length == 0) return;

            var existing = g.ExternalTypes.FirstOrDefault(x => x.Name == name);
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
            if (g.Unresolved.Count(u => u.Kind == kind) >= UnresolvedCaps.GetValueOrDefault(kind, 100)) return;

            var span = at.GetLocation().GetLineSpan();
            var text = expression.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (text.Length > 80) text = text[..77] + "...";

            g.Unresolved.Add(new UnresolvedSite
            {
                Kind = kind,
                File = at.SyntaxTree.FilePath.Length > 0
                    ? Path.GetRelativePath(g.Root, at.SyntaxTree.FilePath)
                    : "",
                Line = span.StartLinePosition.Line + 1,
                Expression = text,
                Reason = reason
            });
        }

        private void Link(int from, int to, EdgeKind kind, string? note = null,
                          double confidence = 1.0, string? source = null, SyntaxNode? at = null)
        {
            if (from == to) return;
            if (!_dedupe.Add((from, to, kind))) return;
            g.Edges.Add(new Edge
            {
                From = from,
                To = to,
                Kind = kind,
                Note = note,
                // Left null at full confidence so the on-disk graph does not grow a field per edge.
                Confidence = confidence >= 1.0 ? null : confidence,
                Source = source,
                Site = at == null ? null : SiteOf(at)
            });
        }

        /// <summary>Where an edge was declared, as a repo-relative file:line.</summary>
        private string SiteOf(SyntaxNode at)
        {
            var file = at.SyntaxTree.FilePath.Length > 0
                ? Path.GetRelativePath(g.Root, at.SyntaxTree.FilePath)
                : "";

            return $"{file}:{at.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
        }

        // ---------------------------------------------------------------- pass 1

        public void Pass1_Declarations(Action<string>? progress)
        {
            progress?.Invoke("pass 1: declarations");

            foreach (var tree in comp.SyntaxTrees)
            {
                var model = comp.GetSemanticModel(tree);

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
                    _pendingHandlers.Add((type, typeDecl, typeId));

                    // Primary constructor parameters are declared on the type, not in a member.
                    if (typeDecl.ParameterList != null)
                    {
                        foreach (var parameter in typeDecl.ParameterList.Parameters)
                        {
                            if (parameter.Type != null &&
                                model.GetSymbolInfo(parameter.Type).Symbol is ITypeSymbol pt)
                            {
                                RecordExternalIn(pt, parameter.Type);
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
                                RecordExternalIn(ms.ReturnType, md.ReturnType);
                                foreach (var parameter in ms.Parameters) RecordExternalIn(parameter.Type, md);
                                foreach (var t in MethodTags(md)) AddTag(mNode, t);
                                if (typeNode.Tags.Contains("controller")) AddTag(mNode, "action");
                                if (mNode.Tags.Contains("test")) AddTag(typeNode, "test");
                                break;
                            }
                            case ConstructorDeclarationSyntax cd when model.GetDeclaredSymbol(cd) is { } cs:
                                Link(typeId, NodeFor(cs, "method", cd.GetLocation()), EdgeKind.TypeUse, "ctor");
                                foreach (var parameter in cs.Parameters) RecordExternalIn(parameter.Type, cd);
                                break;
                            case PropertyDeclarationSyntax pd when model.GetDeclaredSymbol(pd) is { } ps:
                            {
                                var pId = NodeFor(ps, "property", pd.GetLocation());
                                Link(typeId, pId, EdgeKind.TypeUse, "member");
                                g.ById(pId)!.Signature = Display(ps.Type);
                                RecordExternalIn(ps.Type, pd.Type);
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
                                    RecordExternalIn(fs.Type, fd.Declaration.Type);
                                }

                                break;
                            }
                        }
                    }
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
            foreach (var (type, decl, id) in _pendingHandlers)
            {
                RegisterMessageHandler(type, decl, id, methodsByOwner);
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

        private Dictionary<string, List<Node>> MethodsByOwner()
        {
            var map = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
            foreach (var n in g.Nodes)
            {
                if (n.Kind != "method") continue;
                var dot = n.Short.LastIndexOf('.');
                if (dot <= 0) continue;
                var owner = n.Short[..dot];
                if (!map.TryGetValue(owner, out var list)) map[owner] = list = new List<Node>();
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
                var model = comp.GetSemanticModel(decl.SyntaxTree);
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

            var typeShort = g.ById(typeId)!.Short;
            var entry = methodsByOwner.GetValueOrDefault(typeShort)?
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
        /// CreateOrder&lt;T&gt; closed over something do not diverge.
        /// </summary>
        private static string RequestKey(INamedTypeSymbol type) =>
            type.OriginalDefinition.ToDisplayString(KeyFormat);

        private static string ShortOfRequestKey(string key)
        {
            if (key.StartsWith('~')) return key[1..];
            var trimmed = key.Split('<')[0];
            var dot = trimmed.LastIndexOf('.');
            return dot < 0 ? trimmed : trimmed[(dot + 1)..];
        }

        // ---------------------------------------------------------------- pass 2

        public void Pass2_Bodies(Action<string>? progress)
        {
            progress?.Invoke("pass 2: call edges");

            foreach (var tree in comp.SyntaxTrees)
            {
                var model = comp.GetSemanticModel(tree);
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
                    // An edge is still drawn, to the first candidate. That is a coin toss between
                    // overloads, so say where it happened rather than let it pass as a fact.
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

            foreach (var ma in body.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                var symbol = model.GetSymbolInfo(ma).Symbol;

                if (symbol is IPropertySymbol prop && prop.Locations.Any(l => l.IsInSource))
                {
                    Link(OwnerOf(ma), NodeFor(prop.OriginalDefinition, "property"), EdgeKind.Call, "prop");
                    continue;
                }

                // OrderStatus.Cancelled read in a switch arm. This is the edge that answers
                // "if I add a member to this enum, which switches do I have to revisit".
                if (symbol is IFieldSymbol field && field.Locations.Any(l => l.IsInSource))
                {
                    var kind = field.ContainingType?.TypeKind == TypeKind.Enum ? "enum-member" : "field";
                    Link(OwnerOf(ma), NodeFor(field.OriginalDefinition, kind), EdgeKind.Call, kind);
                }
            }

            foreach (var assign in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assign.Left is IdentifierNameSyntax id &&
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

                RecordExternal(t, oc.Type);
            }

            foreach (var declaration in body.DescendantNodes().OfType<VariableDeclarationSyntax>())
            {
                if (model.GetSymbolInfo(declaration.Type).Symbol is INamedTypeSymbol vt &&
                    !vt.Locations.Any(l => l.IsInSource))
                {
                    RecordExternal(vt, declaration.Type);
                }
            }
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
            if (args.Count < 2) return;
            if (args[0].Expression is not LiteralExpressionSyntax lit) return;

            var pattern = lit.Token.ValueText;
            if (pattern.Length == 0) return;
            var tag = $"http:{verb} {pattern}";

            var handler = args[1].Expression;

            if (handler is AnonymousFunctionExpressionSyntax lambda)
            {
                var stem = Path.GetFileNameWithoutExtension(inv.SyntaxTree.FilePath);
                if (stem.Length == 0) stem = "Endpoints";

                var span = inv.GetLocation().SourceSpan;
                var routeId = SyntheticNode(
                    $"route::{inv.SyntaxTree.FilePath}:{span.Start}",
                    $"{stem}.{verb} {pattern}",
                    $"{stem}.{verb} {pattern}",
                    "method",
                    inv);

                AddTag(g.ById(routeId)!, tag);
                Link(owner, routeId, EdgeKind.Route, $"{verb} {pattern}");

                claims ??= new Dictionary<SyntaxNode, int>();
                claims[lambda] = routeId;
                return;
            }

            var info = model.GetSymbolInfo(handler);
            var method = info.Symbol as IMethodSymbol ?? info.CandidateSymbols.FirstOrDefault() as IMethodSymbol;
            if (method == null || !method.Locations.Any(l => l.IsInSource)) return;

            var targetId = NodeFor(method.OriginalDefinition, "method");
            AddTag(g.ById(targetId)!, tag);
            Link(owner, targetId, EdgeKind.Route, $"{verb} {pattern}");
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
            if (name is not ("Send" or "Publish" or "SendAsync" or "PublishAsync")) return;

            var arg = inv.ArgumentList.Arguments.FirstOrDefault();
            if (arg == null) return;

            INamedTypeSymbol? requestSymbol = null;
            if (arg.Expression is ObjectCreationExpressionSyntax oc)
                requestSymbol = model.GetSymbolInfo(oc.Type).Symbol as INamedTypeSymbol;
            requestSymbol ??= model.GetTypeInfo(arg.Expression).Type as INamedTypeSymbol;

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

            shortName = arg.Expression is ObjectCreationExpressionSyntax raw
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

            if (types.Count == 0) return;

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
                if (produced is { } impl && impl != self.Value.Id)
                {
                    Bind(self.Value.Id, impl, 0.9, "factory-lambda");
                    return;
                }

                Tag(self.Value.Id);
                return;
            }

            if (types.Count != 2) return;

            var service = Resolve(types[0]);
            var implementation = Resolve(types[1]);
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

        /// <summary>
        /// Pulls the constructed implementation out of a factory registration. Only a direct
        /// object creation counts; sp =&gt; sp.GetRequiredService&lt;T&gt;() is an alias to another
        /// registration, not a binding of its own.
        /// </summary>
        private int? FactoryImplementation(SeparatedSyntaxList<ArgumentSyntax> args, SemanticModel model)
        {
            foreach (var arg in args)
            {
                if (arg.Expression is not AnonymousFunctionExpressionSyntax lambda) continue;

                var creation = lambda.ExpressionBody as ObjectCreationExpressionSyntax
                               ?? lambda.Block?.DescendantNodes()
                                   .OfType<ReturnStatementSyntax>()
                                   .Select(r => r.Expression)
                                   .OfType<ObjectCreationExpressionSyntax>()
                                   .LastOrDefault();

                if (creation == null) continue;
                if (ResolveTypeNode(creation.Type, model) is { } resolved) return resolved.Id;
            }

            return null;
        }

        /// <summary>
        /// Maps a type argument to a graph node, preferring semantic resolution and falling back to
        /// an unambiguous short name match when the type could not be bound. The flag says which
        /// path was taken, so a guess can be recorded at lower confidence instead of passing for
        /// a compiler-verified fact.
        /// </summary>
        private (int Id, bool Semantic)? ResolveTypeNode(TypeSyntax syntax, SemanticModel model)
        {
            if (model.GetSymbolInfo(syntax).Symbol is INamedTypeSymbol sym &&
                sym.OriginalDefinition.Locations.Any(l => l.IsInSource))
            {
                var definition = sym.OriginalDefinition;
                var id = NodeFor(definition, definition.TypeKind == TypeKind.Interface ? "interface" : "type");
                return (id, true);
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

            return match == null ? null : (match.Id, false);
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
                // AsImplementedInterfaces() over a whole assembly with no AssignableTo filter binds
                // everything to everything. Guessing here would be worse than admitting the gap.
                RecordUnresolved("di", inv, inv.Expression.ToString(), "assembly-scan-unfiltered");
                return;
            }

            foreach (var filter in filters)
            {
                if (ResolveTypeNode(filter, model) is not { } resolved) continue;
                BindImplementors(resolved.Id, lifetime, inv, "assembly-scan");
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

            // One lookup instead of a linear scan per member per implementor.
            var methodByShort = new Dictionary<string, Node>(StringComparer.Ordinal);
            var methodsByOwner = new Dictionary<string, List<Node>>(StringComparer.Ordinal);

            foreach (var n in g.Nodes)
            {
                if (n.Kind != "method") continue;
                methodByShort.TryAdd(n.Short, n);

                var dot = n.Short.LastIndexOf('.');
                if (dot <= 0) continue;
                var owner = n.Short[..dot];
                if (!methodsByOwner.TryGetValue(owner, out var list)) methodsByOwner[owner] = list = new List<Node>();
                list.Add(n);
            }

            foreach (var (baseId, implementors) in _implementorsByBase)
            {
                var baseNode = g.ById(baseId);
                if (baseNode == null) continue;

                // Interfaces dispatch; base classes are overridden. Both answer "what actually runs".
                var edgeKind = baseNode.Kind == "interface" ? EdgeKind.Interface : EdgeKind.Override;
                var members = methodsByOwner.GetValueOrDefault(baseNode.Short) ?? [];

                foreach (var implId in implementors)
                {
                    var implNode = g.ById(implId);
                    if (implNode == null) continue;

                    var preferred = _diBoundPairs.Contains((baseId, implId));
                    var note = preferred ? "di-bound" : null;

                    foreach (var m in members)
                    {
                        var memberName = m.Short[(baseNode.Short.Length + 1)..];
                        if (!methodByShort.TryGetValue(implNode.Short + "." + memberName, out var target)) continue;
                        Link(m.Id, target.Id, edgeKind, note);
                    }

                    Link(baseId, implId, edgeKind, note);
                }
            }
        }
    }
}
