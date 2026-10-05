using System.Text.Json;
using CsMesh.Common;
using CsMesh.Skill;

namespace CsMesh.Mcp;

/// <summary>
/// Serves csmesh over MCP on stdio: newline-delimited JSON-RPC 2.0, one message per line.
///
/// The point is not that a shell call is expensive. It is that a shell call is untyped -- the
/// agent has to know the command exists, spell the flags, and read an exit code out of a
/// subprocess. Over MCP the catalogue arrives with schemas, so the model picks a tool the same way
/// it picks any other, and the descriptions can say what each answers that reading files would not.
///
/// Everything is stateless. The index lives on disk, so nothing is held between calls and a crash
/// costs nothing.
/// </summary>
public static class McpServer
{
    /// <summary>
    /// Versions this build knows how to speak. The client's choice is echoed when it names one of
    /// these, because a client asking for a revision we already satisfy should not be refused over
    /// a date; otherwise the newest is offered and the client decides.
    /// </summary>
    private static readonly string[] Supported = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private const int ParseError = -32700;
    private const int MethodNotFound = -32601;
    private const int InternalError = -32603;

    private static string _activeRoot = string.Empty;
    private static bool _clientSupportsRoots;
    private static string? _pendingRootsReqId;
    private static int _reqCounter;

    public static int Run(string root)
    {
        _activeRoot = root;
        _clientSupportsRoots = false;
        _pendingRootsReqId = null;
        _reqCounter = 0;

        // stdout is the transport. Anything a command prints is captured in McpTools; anything
        // csmesh logs about itself goes to stderr, which the spec leaves free for exactly this.
        Dbg.Log($"mcp: serving {_activeRoot}");

        // Single-threaded by design: one frame is read, dispatched and answered before the next
        // is looked at. McpTools swaps Console.Out around each command to capture its output, and
        // that is only safe while nothing else can be writing. Anything that makes this loop
        // concurrent breaks the capture first and silently -- output would land in the wrong
        // reply, or in the transport.
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = TryParse(line, out var parseError);
            if (parseError != null)
            {
                Dbg.Log($"mcp: unparseable frame: {parseError}");
                Write(new RpcResponse { Error = new RpcError { Code = ParseError, Message = "parse error" } });
                continue;
            }

            if (doc == null) continue;

            var rootElem = doc.RootElement;

            // Responses to server-initiated requests (e.g. roots/list) have no "method" member.
            if (!rootElem.TryGetProperty("method", out _))
            {
                if (rootElem.TryGetProperty("id", out var idElem))
                {
                    var id = idElem.ValueKind == JsonValueKind.String ? idElem.GetString() : idElem.GetRawText();
                    if (id == _pendingRootsReqId)
                    {
                        _pendingRootsReqId = null;
                        if (rootElem.TryGetProperty("result", out var resElem))
                        {
                            HandleRootsResult(resElem);
                        }
                        else if (rootElem.TryGetProperty("error", out var errElem))
                        {
                            Dbg.Log($"mcp: client rejected roots/list: {errElem.GetRawText()}");
                        }
                    }
                }
                continue;
            }

            RpcRequest? request;
            try
            {
                request = JsonSerializer.Deserialize(rootElem, McpJsonContext.Default.RpcRequest);
            }
            catch (JsonException ex)
            {
                Dbg.Log($"mcp: unparseable request: {ex.Message}");
                Write(new RpcResponse { Error = new RpcError { Code = ParseError, Message = "parse error" } });
                continue;
            }

            if (request == null) continue;

            try
            {
                Dispatch(request);
            }
            catch (Exception ex)
            {
                // One bad call must not take the server down with it: the client would see the
                // pipe close and report csmesh as broken rather than the one tool that failed.
                Dbg.Log($"mcp: {request.Method} threw: {ex}");

                if (request.Id != null)
                {
                    Write(new RpcResponse
                    {
                        Id = request.Id,
                        Error = new RpcError { Code = InternalError, Message = ex.Message }
                    });
                }
            }
        }

        Dbg.Log("mcp: stdin closed, exiting");
        return Exit.Ok;
    }

    private static JsonDocument? TryParse(string line, out string? error)
    {
        try
        {
            error = null;
            return JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static void Dispatch(RpcRequest request)
    {
        switch (request.Method)
        {
            case "initialize":
                if (request.Params?.TryGetProperty("capabilities", out var caps) == true &&
                    caps.TryGetProperty("roots", out _))
                {
                    _clientSupportsRoots = true;
                    Dbg.Log("mcp: client advertised roots capability");
                }
                Write(Reply(request, Element(Initialize(request), McpJsonContext.Default.InitializeResult)));
                break;

            case "notifications/initialized":
                if (_clientSupportsRoots)
                {
                    RequestRootsList();
                }
                break;

            case "notifications/roots/list_changed" or "roots/list_changed":
                if (_clientSupportsRoots)
                {
                    RequestRootsList();
                }
                break;

            case "tools/list":
                var list = new ToolsListResult { Tools = McpTools.Descriptors() };
                Write(Reply(request, Element(list, McpJsonContext.Default.ToolsListResult)));
                break;

            case "tools/call":
                var name = request.Params?.TryGetProperty("name", out var n) == true ? n.GetString() : null;
                JsonElement? arguments = request.Params?.TryGetProperty("arguments", out var a) == true ? a : null;

                var result = McpTools.Invoke(_activeRoot, name ?? string.Empty, arguments);
                Write(Reply(request, Element(result, McpJsonContext.Default.ToolCallResult)));
                break;

            case "ping":
                // The spec says an empty object. Returning a typed payload with an empty array
                // happens to satisfy a lenient client and gives a strict one something to reject,
                // and a liveness check is the worst place to be interesting.
                Write(Reply(request, JsonDocument.Parse("{}").RootElement.Clone()));
                break;

            // Notifications carry no id and take no reply. Answering one is a protocol error, so
            // these are absorbed rather than falling through to method-not-found.
            case "notifications/cancelled":
                break;

            default:
                if (request.Id == null) break;

                Write(new RpcResponse
                {
                    Id = request.Id,
                    Error = new RpcError { Code = MethodNotFound, Message = $"unknown method '{request.Method}'" }
                });
                break;
        }
    }

    private static void RequestRootsList()
    {
        var id = $"csmesh-roots-{Interlocked.Increment(ref _reqCounter)}";
        _pendingRootsReqId = id;
        Dbg.Log($"mcp: requesting roots/list with id {id}");
        Console.Out.WriteLine($$"""{"jsonrpc":"2.0","id":"{{id}}","method":"roots/list"}""");
        Console.Out.Flush();
    }

    private static void HandleRootsResult(JsonElement resultElem)
    {
        try
        {
            var rootsResult = JsonSerializer.Deserialize(resultElem, McpJsonContext.Default.RootsListResult);
            if (rootsResult?.Roots is { Count: > 0 } roots)
            {
                string? fallbackRoot = null;

                foreach (var r in roots)
                {
                    if (string.IsNullOrWhiteSpace(r.Uri)) continue;

                    var localPath = TryConvertUriToLocalPath(r.Uri);
                    if (string.IsNullOrEmpty(localPath) || !Directory.Exists(localPath)) continue;

                    var discovered = RepositoryLocator.FindRoot(localPath);
                    fallbackRoot ??= discovered;

                    // Prefer a root that actually contains a C# project, solution, or existing csmesh index
                    if (HasDotNetOrMesh(discovered))
                    {
                        _activeRoot = discovered;
                        Dbg.Log($"mcp: updated active root to '{_activeRoot}' from client root '{r.Uri}'");
                        return;
                    }
                }

                if (fallbackRoot != null)
                {
                    _activeRoot = fallbackRoot;
                    Dbg.Log($"mcp: updated active root to fallback '{_activeRoot}'");
                }
            }
        }
        catch (Exception ex)
        {
            Dbg.Log($"mcp: failed to process roots/list result: {ex.Message}");
        }
    }

    private static bool HasDotNetOrMesh(string dirPath)
    {
        try
        {
            var dir = new DirectoryInfo(dirPath);
            return RepositoryLocator.HasGraphMarker(dirPath)
                   || dir.EnumerateFiles("*.sln").Any()
                   || dir.EnumerateFiles("*.slnx").Any()
                   || dir.EnumerateFiles("*.csproj", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static string? TryConvertUriToLocalPath(string uriOrPath)
    {
        if (string.IsNullOrWhiteSpace(uriOrPath)) return null;

        if (Uri.TryCreate(uriOrPath, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        if (Path.IsPathRooted(uriOrPath))
        {
            return uriOrPath;
        }

        return null;
    }

    /// <summary>
    /// The MCP-specific paragraphs: what the transport adds over the shared rules. The grep directive
    /// and the command table are appended by reference from <see cref="SkillText"/>, never copied, so
    /// a rule changed for a file-reading agent reaches an MCP-only session too.
    /// </summary>
    private const string McpPreamble =
        "csmesh answers structural questions about a C# codebase from a Roslyn symbol graph: "
        + "call paths through interfaces and mediator dispatch, which implementation a DI "
        + "container binds, and what a change would reach.\n\n"
        + "Every tool reads an index on disk. Run 'index' once in a fresh checkout, and again "
        + "when an answer says it is stale. 'doctor' says whether the index is usable.\n\n"
        + "Not in the graph: config keys and log messages; use text search for those.\n\n"
        + "Answers end with a suggested next step written as a shell command, e.g. "
        + "'next: csmesh entrypoints orders'. Each maps to the tool of the same name, so call "
        + "'entrypoints' with filter='orders' rather than reaching for a terminal.\n\n"
        + "When a name is declared in more than one project, the answer exits 3 and lists each "
        + "candidate with its project. Re-run the tool with 'project' set to one of them rather "
        + "than guessing. Two overloads of one member in one project are separated by the "
        + "parameter list instead: pass 'Type.Member(int, string)', as the candidate list prints it.\n\n"
        + "Parameters: budget caps the answer (raise it only after narrowing with 'under'); "
        + "'under' scopes a subtree (e.g. src/Payments) and is the cheapest way to cut a large "
        + "answer; 'depth' sets how many levels to walk; 'heal' rebinds changed files before "
        + "answering (default true) and false answers from the current graph, marking changed "
        + "rows [STALE]; 'repo' is an optional repository root or workspace folder that overrides "
        + "the detected root, and is how you point at the repository when workspace detection is "
        + "unavailable.";

    /// <summary>
    /// The token ceiling the assembled instructions must stay under, measured with
    /// <see cref="BudgetWriter.Estimate"/> on LF-normalized text so a CRLF checkout and an LF one
    /// agree. This text sits in the context of every MCP session for the whole session, paid for on
    /// every turn whichever tool is called; the catalogue it shares the context with is bounded by
    /// <see cref="CatalogueTokenCeiling"/>, not licence to grow this further.
    /// </summary>
    public const int InstructionsTokenCeiling = 1000;

    /// <summary>
    /// Combined ceiling for the resident instructions plus the tool catalogue, LF-normalized. Before
    /// this batch the two were 892 + 5,059 = 5,951 tokens; the pre-directive figure was 309 + 5,059 =
    /// 5,368; after the shared parameter descriptions were written once it measures 966 + 2,899 =
    /// 3,865. The catalogue is read by a model once per session just as the instructions are, and the
    /// shared parameter descriptions repeated across tools were what carried it up.
    /// </summary>
    public const int CatalogueTokenCeiling = 4000;

    /// <summary>
    /// LF, whatever line endings the build checkout gave the SkillText literals, so the initialize
    /// frame is byte-identical from a Windows and a Linux build.
    /// </summary>
    private static string Lf(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>
    /// The text sent in the <c>initialize</c> result: the MCP-specific paragraphs plus the shared
    /// "prefer csmesh over grep" directive and command table, by reference.
    /// </summary>
    public static readonly string InstructionText =
        Lf(McpPreamble + "\n\n" + SkillText.RulesGrepDirective + "\n\n" + SkillText.RulesMatchTable);

    private static InitializeResult Initialize(RpcRequest request)
    {
        var asked = request.Params?.TryGetProperty("protocolVersion", out var v) == true ? v.GetString() : null;

        return new InitializeResult
        {
            ProtocolVersion = asked != null && Supported.Contains(asked) ? asked : Supported[0],
            ServerInfo = new ServerInfo { Version = AppVersion.Get() },
            Instructions = InstructionText
        };
    }

    private static RpcResponse Reply(RpcRequest request, JsonElement result) =>
        new() { Id = request.Id, Result = result };

    private static JsonElement Element<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonSerializer.SerializeToElement(value, info);

    private static void Write(RpcResponse response)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(response, McpJsonContext.Default.RpcResponse));
        Console.Out.Flush();
    }
}
