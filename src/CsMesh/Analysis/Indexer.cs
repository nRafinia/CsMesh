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

        graph.UnresolvedCallSites = builder.UnresolvedCallSites;
        graph.TotalCallSites = builder.TotalCallSites;
        graph.AmbiguousDiRegistrations = builder.AmbiguousDiRegistrations;
        graph.AmbiguousMessageDispatches = builder.AmbiguousMessageDispatches;
        graph.UnmatchedMessageDispatches = builder.UnmatchedMessageDispatches;
        return graph;
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
            if (l is { IsInSource: true })
            {
                file = Path.GetRelativePath(g.Root, l.SourceTree!.FilePath);
                line = l.GetLineSpan().StartLinePosition.Line + 1;
            }

            return AddNode(key, FullName(sym), ShortName(sym), kind, file, line);
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

            return AddNode(key, name, shortName, kind, file, span.StartLinePosition.Line + 1);
        }

        private int AddNode(string key, string name, string shortName, string kind, string file, int line)
        {
            var node = new Node
            {
                Id = g.Nodes.Count,
                Name = name,
                Short = shortName,
                Kind = kind,
                File = file,
                Line = line
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

        private static string FullName(ISymbol s)
        {
            if (s is INamedTypeSymbol) return s.ToDisplayString();
            if (s is IMethodSymbol or IPropertySymbol)
                return $"{s.ContainingType?.ToDisplayString() ?? "?"}.{s.Name}";
            return s.ToDisplayString();
        }

        private static string ShortName(ISymbol s)
        {
            if (s is INamedTypeSymbol t) return t.Name;
            if (s is IMethodSymbol or IPropertySymbol)
                return $"{s.ContainingType?.Name ?? "?"}.{s.Name}";
            return s.Name;
        }

        private void Link(int from, int to, EdgeKind kind, string? note = null,
                          double confidence = 1.0, string? source = null)
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
                Source = source
            });
        }

        // ---------------------------------------------------------------- pass 1

        public void Pass1_Declarations(Action<string>? progress)
        {
            progress?.Invoke("pass 1: declarations");

            foreach (var tree in comp.SyntaxTrees)
            {
                var model = comp.GetSemanticModel(tree);

                foreach (var typeDecl in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol type) continue;

                    var kind = type.TypeKind == TypeKind.Interface ? "interface" : "type";
                    var typeId = NodeFor(type, kind, typeDecl.GetLocation());
                    var typeNode = g.ById(typeId)!;

                    foreach (var t in TypeTags(type, typeDecl)) AddTag(typeNode, t);

                    RegisterBaseTypes(type, typeId);
                    _pendingHandlers.Add((type, typeDecl, typeId));

                    foreach (var member in typeDecl.Members)
                    {
                        switch (member)
                        {
                            case MethodDeclarationSyntax md when model.GetDeclaredSymbol(md) is { } ms:
                            {
                                var mId = NodeFor(ms, "method", md.GetLocation());
                                Link(typeId, mId, EdgeKind.TypeUse, "member");
                                var mNode = g.ById(mId)!;
                                foreach (var t in MethodTags(md)) AddTag(mNode, t);
                                if (typeNode.Tags.Contains("controller")) AddTag(mNode, "action");
                                break;
                            }
                            case ConstructorDeclarationSyntax cd when model.GetDeclaredSymbol(cd) is { } cs:
                                Link(typeId, NodeFor(cs, "method", cd.GetLocation()), EdgeKind.TypeUse, "ctor");
                                break;
                            case PropertyDeclarationSyntax pd when model.GetDeclaredSymbol(pd) is { } ps:
                                Link(typeId, NodeFor(ps, "property", pd.GetLocation()), EdgeKind.TypeUse, "member");
                                break;
                        }
                    }
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
                if (target == null && info.CandidateSymbols.Length == 0) UnresolvedCallSites++;

                if (target != null && target.Locations.Any(l => l.IsInSource))
                {
                    Link(owner, NodeFor(target.OriginalDefinition, "method"), EdgeKind.Call);
                }

                TryMediator(inv, model, owner);
                TryDiRegistration(inv, model);
            }

            foreach (var ma in body.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (model.GetSymbolInfo(ma).Symbol is IPropertySymbol prop && prop.Locations.Any(l => l.IsInSource))
                {
                    Link(OwnerOf(ma), NodeFor(prop.OriginalDefinition, "property"), EdgeKind.Call, "prop");
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
                if (model.GetSymbolInfo(oc.Type).Symbol is INamedTypeSymbol t && t.Locations.Any(l => l.IsInSource))
                {
                    var kind = t.TypeKind == TypeKind.Interface ? "interface" : "type";
                    Link(OwnerOf(oc), NodeFor(t.OriginalDefinition, kind), EdgeKind.Construct);
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

                if (_requestKeysByShort.ContainsKey(shortName)) AmbiguousMessageDispatches++;
                else UnmatchedMessageDispatches++;
                return;
            }

            shortName = arg.Expression is ObjectCreationExpressionSyntax raw
                ? raw.Type.ToString().Split('.').Last().Split('<').First()
                : "";
            if (shortName.Length == 0) return;

            if (!_requestKeysByShort.TryGetValue(shortName, out var candidates))
            {
                UnmatchedMessageDispatches++;
                return;
            }

            if (candidates.Count > 1)
            {
                AmbiguousMessageDispatches++;
                Dbg.Log($"mediator: '{shortName}' matches {candidates.Count} request types; dispatch skipped");
                return;
            }

            if (_handlersByRequest.TryGetValue(candidates.First(), out var only))
                Emit(only, shortName, 0.7, "short-name-match");

            void Emit(List<int> handlers, string display, double confidence, string source)
            {
                foreach (var handlerId in handlers)
                {
                    Link(fromId, handlerId, EdgeKind.Mediatr, $"via {name}({display})", confidence, source);
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
                Link(service, impl, EdgeKind.DiBinding, note, confidence, source);

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
                    Dbg.Log($"di: '{shortName}' is ambiguous across namespaces; registration skipped");
                    return null;
                }
                match = n;
            }

            return match == null ? null : (match.Id, false);
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
