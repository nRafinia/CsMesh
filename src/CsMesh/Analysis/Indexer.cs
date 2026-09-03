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
        "/bin/", "/obj/", "/node_modules/", "/.git/", "/.vs/", "/packages/", "/TestResults/", "/.csmesh/"
    };

    public static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (SkipDirs.Any(d => normalized.Contains(d, StringComparison.OrdinalIgnoreCase))) continue;
            if (normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) continue;
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

        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }

            trees.Add(CSharpSyntaxTree.ParseText(text, path: file));
            var fileInfo = new FileInfo(file);
            stamps.Add(new FileStamp
            {
                Path = Path.GetRelativePath(root, file),
                Ticks = fileInfo.LastWriteTimeUtc.Ticks,
                Size = fileInfo.Length
            });
        }

        var references = ReferenceSet(root);
        progress?.Invoke($"compiling against {references.Count} references");

        var compilation = CSharpCompilation.Create(
            "csmesh.index",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var graph = new Graph
        {
            Root = root,
            BuiltAt = DateTimeOffset.UtcNow,
            BuiltFromCommit = RepositoryLocator.GitHead(root),
            Files = stamps
        };

        var builder = new Builder(graph, compilation);
        builder.Pass1_Declarations(progress);
        builder.Pass2_Bodies(progress);
        builder.Pass3_Indirection(progress);

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

        foreach (var bin in Directory.EnumerateDirectories(root, "bin", SearchOption.AllDirectories).Take(40))
        {
            foreach (var cfg in Directory.EnumerateDirectories(bin, "*", SearchOption.AllDirectories).Take(20))
            {
                AddDir(cfg, 60);
            }
        }

        return list;
    }

    private sealed class Builder
    {
        private readonly Graph _g;
        private readonly CSharpCompilation _comp;
        private readonly Dictionary<string, int> _idByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<int>> _typeImplementors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _handlerByRequest = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _diBindings = new(StringComparer.Ordinal);
        private readonly HashSet<(int, int, EdgeKind)> _dedupe = new();
        private readonly List<(INamedTypeSymbol, TypeDeclarationSyntax, int)> _pendingHandlers = new();

        public Builder(Graph g, CSharpCompilation comp)
        {
            _g = g;
            _comp = comp;
        }

        private int NodeFor(ISymbol sym, string kind, Location? loc = null)
        {
            var key = Key(sym);
            if (_idByKey.TryGetValue(key, out var existing)) return existing;

            var l = loc ?? sym.Locations.FirstOrDefault(x => x.IsInSource);
            var file = "";
            var line = 0;
            if (l != null && l.IsInSource)
            {
                file = Path.GetRelativePath(_g.Root, l.SourceTree!.FilePath);
                line = l.GetLineSpan().StartLinePosition.Line + 1;
            }

            var node = new Node
            {
                Id = _g.Nodes.Count,
                Name = FullName(sym),
                Short = ShortName(sym),
                Kind = kind,
                File = file,
                Line = line
            };

            _g.Nodes.Add(node);
            _idByKey[key] = node.Id;
            return node.Id;
        }

        /// <summary>
        /// Constructs a unique key including containing type, signature, and symbol kind.
        /// </summary>
        private static string Key(ISymbol s)
        {
            var container = s.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            ?? s.ContainingNamespace?.ToDisplayString() ?? "";
            var self = s is IMethodSymbol m
                ? m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.Type.Name)) + ")"
                : s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

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

        private void Link(int from, int to, EdgeKind kind, string? note = null)
        {
            if (from == to) return;
            if (!_dedupe.Add((from, to, kind))) return;
            _g.Edges.Add(new Edge { From = from, To = to, Kind = kind, Note = note });
        }

        public void Pass1_Declarations(Action<string>? progress)
        {
            progress?.Invoke("pass 1: declarations");

            foreach (var tree in _comp.SyntaxTrees)
            {
                var model = _comp.GetSemanticModel(tree);
                var rootNode = tree.GetRoot();

                foreach (var typeDecl in rootNode.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol type) continue;

                    var kind = type.TypeKind == TypeKind.Interface ? "interface" : "type";
                    var typeId = NodeFor(type, kind, typeDecl.GetLocation());
                    var typeNode = _g.ById(typeId)!;

                    foreach (var t in TypeTags(type, typeDecl)) AddTag(typeNode, t);

                    foreach (var baseName in BaseTypeNames(type, typeDecl))
                    {
                        if (!_typeImplementors.TryGetValue(baseName, out var list))
                            _typeImplementors[baseName] = list = new List<int>();
                        list.Add(typeId);
                    }

                    _pendingHandlers.Add((type, typeDecl, typeId));

                    foreach (var member in typeDecl.Members)
                    {
                        if (member is MethodDeclarationSyntax md &&
                            model.GetDeclaredSymbol(md) is IMethodSymbol ms)
                        {
                            var mId = NodeFor(ms, "method", md.GetLocation());
                            Link(typeId, mId, EdgeKind.TypeUse, "member");
                            foreach (var t in MethodTags(md)) AddTag(_g.ById(mId)!, t);

                            if (typeNode.Tags.Any(t => t.StartsWith("controller")))
                                AddTag(_g.ById(mId)!, "action");
                        }
                        else if (member is ConstructorDeclarationSyntax cd &&
                                 model.GetDeclaredSymbol(cd) is IMethodSymbol cs)
                        {
                            var mId = NodeFor(cs, "method", cd.GetLocation());
                            Link(typeId, mId, EdgeKind.TypeUse, "ctor");
                        }
                        else if (member is PropertyDeclarationSyntax pd &&
                                 model.GetDeclaredSymbol(pd) is IPropertySymbol ps)
                        {
                            var pId = NodeFor(ps, "property", pd.GetLocation());
                            Link(typeId, pId, EdgeKind.TypeUse, "member");
                        }
                    }
                }
            }

            foreach (var (type, decl, id) in _pendingHandlers)
            {
                RegisterMediatrHandler(type, decl, id);
            }

            Dbg.Log($"pass 1: {_g.Nodes.Count} nodes, {_handlerByRequest.Count} message handlers mapped");
        }

        private static void AddTag(Node n, string tag)
        {
            if (!n.Tags.Contains(tag)) n.Tags.Add(tag);
        }

        private static IEnumerable<string> TypeTags(INamedTypeSymbol type, TypeDeclarationSyntax decl)
        {
            var bases = BaseTypeNames(type, decl).ToList();
            if (type.Name.EndsWith("Controller") || bases.Any(b => b.Contains("ControllerBase") || b == "Controller"))
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
            if (type.Name.EndsWith("Repository")) yield return "repository";

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
                        if (n == verb || n == verb + "Attribute")
                        {
                            var arg = a.ArgumentList?.Arguments.FirstOrDefault()?.ToString().Trim('"');
                            yield return $"http:{verb[4..].ToUpperInvariant()} {arg ?? "/"}";
                        }
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

        private void RegisterMediatrHandler(INamedTypeSymbol type, TypeDeclarationSyntax decl, int typeId)
        {
            foreach (var b in BaseTypeNames(type, decl))
            {
                var open = b.IndexOf('<');
                if (open < 0) continue;
                var head = b[..open];
                if (head is not ("IRequestHandler" or "INotificationHandler" or "ICommandHandler"
                                 or "IQueryHandler" or "IConsumer" or "IHandleMessages")) continue;

                var args = b[(open + 1)..].TrimEnd('>').Split(',', StringSplitOptions.TrimEntries);
                if (args.Length == 0) continue;
                var request = args[0].Split('.').Last();
                if (request.Length == 0) continue;

                var handle = _g.Nodes.FirstOrDefault(n =>
                    n.Kind == "method" &&
                    n.Short.StartsWith(_g.ById(typeId)!.Short + ".", StringComparison.Ordinal) &&
                    (n.Short.EndsWith(".Handle") || n.Short.EndsWith(".HandleAsync") || n.Short.EndsWith(".Consume")));

                _handlerByRequest[request] = handle?.Id ?? typeId;
            }
        }

        public void Pass2_Bodies(Action<string>? progress)
        {
            progress?.Invoke("pass 2: call edges");

            foreach (var tree in _comp.SyntaxTrees)
            {
                var model = _comp.GetSemanticModel(tree);
                foreach (var body in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
                {
                    if (body is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax))
                        continue;
                    if (model.GetDeclaredSymbol(body) is not IMethodSymbol owner) continue;
                    if (!_idByKey.TryGetValue(Key(owner), out var fromId)) continue;

                    foreach (var inv in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        var target = model.GetSymbolInfo(inv).Symbol as IMethodSymbol
                                     ?? model.GetSymbolInfo(inv).CandidateSymbols.FirstOrDefault() as IMethodSymbol;

                        if (target != null && target.Locations.Any(l => l.IsInSource))
                        {
                            var toId = NodeFor(target.OriginalDefinition, "method");
                            Link(fromId, toId, EdgeKind.Call);
                        }

                        TryMediator(inv, model, fromId);
                        TryDiRegistration(inv, model, fromId);
                    }

                    foreach (var ma in body.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                    {
                        if (model.GetSymbolInfo(ma).Symbol is IPropertySymbol prop &&
                            prop.Locations.Any(l => l.IsInSource))
                        {
                            var toId = NodeFor(prop.OriginalDefinition, "property");
                            Link(fromId, toId, EdgeKind.Call, "prop");
                        }
                    }

                    foreach (var init in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    {
                        if (init.Left is IdentifierNameSyntax idn &&
                            model.GetSymbolInfo(idn).Symbol is IPropertySymbol p2 &&
                            p2.Locations.Any(l => l.IsInSource))
                        {
                            var toId = NodeFor(p2.OriginalDefinition, "property");
                            Link(fromId, toId, EdgeKind.Call, "prop");
                        }
                    }

                    foreach (var oc in body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                    {
                        if (model.GetSymbolInfo(oc.Type).Symbol is INamedTypeSymbol t &&
                            t.Locations.Any(l => l.IsInSource))
                        {
                            var toId = NodeFor(t, t.TypeKind == TypeKind.Interface ? "interface" : "type");
                            Link(fromId, toId, EdgeKind.Construct);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Resolves mediator request types from Send/Publish invocations to their corresponding handlers.
        /// </summary>
        private void TryMediator(InvocationExpressionSyntax inv, SemanticModel model, int fromId)
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma) return;
            var name = ma.Name.Identifier.Text;
            if (name is not ("Send" or "Publish" or "SendAsync" or "PublishAsync")) return;
            var arg = inv.ArgumentList.Arguments.FirstOrDefault();
            if (arg == null) return;

            string? requestType = null;
            if (arg.Expression is ObjectCreationExpressionSyntax oc)
                requestType = oc.Type.ToString().Split('.').Last().Split('<').First();
            else if (model.GetTypeInfo(arg.Expression).Type is { } t && t.Name.Length > 0)
                requestType = t.Name;

            if (requestType == null) return;
            if (!_handlerByRequest.TryGetValue(requestType, out var handlerId)) return;
            Link(fromId, handlerId, EdgeKind.Mediatr, $"via {name}({requestType})");
        }

        /// <summary>
        /// Tracks dependency injection service registrations (AddScoped, AddSingleton, AddTransient).
        /// </summary>
        private void TryDiRegistration(InvocationExpressionSyntax inv, SemanticModel model, int fromId)
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma) return;
            var name = ma.Name.Identifier.Text;
            if (!name.StartsWith("Add")) return;
            if (ma.Name is not GenericNameSyntax gen) return;
            var args = gen.TypeArgumentList.Arguments;
            if (args.Count != 2) return;

            var svc = args[0].ToString().Split('.').Last();
            var impl = args[1].ToString().Split('.').Last();
            var lifetime = name switch
            {
                "AddScoped" => "scoped",
                "AddSingleton" => "singleton",
                "AddTransient" => "transient",
                _ => "di"
            };
            _diBindings[svc] = impl;

            var svcNode = _g.Nodes.FirstOrDefault(n => n.Short == svc && n.Kind is "interface" or "type");
            var implNode = _g.Nodes.FirstOrDefault(n => n.Short == impl && n.Kind == "type");
            if (svcNode != null && implNode != null)
            {
                Link(svcNode.Id, implNode.Id, EdgeKind.DiBinding, lifetime);
                AddTag(implNode, "di:" + lifetime);
            }
        }

        public void Pass3_Indirection(Action<string>? progress)
        {
            progress?.Invoke("pass 3: interface and override edges");

            foreach (var ifaceNode in _g.Nodes.Where(n => n.Kind == "interface").ToList())
            {
                if (!_typeImplementors.TryGetValue(ifaceNode.Short, out var impls)) continue;
                var members = _g.Nodes.Where(n => n.Kind == "method" &&
                    n.Short.StartsWith(ifaceNode.Short + ".", StringComparison.Ordinal)).ToList();

                foreach (var implId in impls.Distinct())
                {
                    var implNode = _g.ById(implId);
                    if (implNode == null) continue;
                    var preferred = _diBindings.TryGetValue(ifaceNode.Short, out var bound) && bound == implNode.Short;

                    foreach (var m in members)
                    {
                        var memberName = m.Short[(ifaceNode.Short.Length + 1)..];
                        var target = _g.Nodes.FirstOrDefault(n => n.Kind == "method" &&
                            n.Short == implNode.Short + "." + memberName);
                        if (target == null) continue;
                        Link(m.Id, target.Id, EdgeKind.Interface, preferred ? "di-bound" : null);
                    }

                    Link(ifaceNode.Id, implId, EdgeKind.Interface, preferred ? "di-bound" : null);
                }
            }
        }
    }
}
