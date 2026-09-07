using System.Text.Json;
using CsMesh.Common;

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
            return Directory.Exists(Path.Combine(dirPath, ".csmesh"))
                   || Directory.Exists(Path.Combine(dirPath, ".csgraph"))
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

    private static InitializeResult Initialize(RpcRequest request)
    {
        var asked = request.Params?.TryGetProperty("protocolVersion", out var v) == true ? v.GetString() : null;

        return new InitializeResult
        {
            ProtocolVersion = asked != null && Supported.Contains(asked) ? asked : Supported[0],
            ServerInfo = new ServerInfo { Version = AppVersion.Get() },
            Instructions =
                "csmesh answers structural questions about a C# codebase from a Roslyn symbol graph: "
                + "call paths through interfaces and mediator dispatch, which implementation a DI "
                + "container binds, and what a change would reach.\n\n"
                + "Every tool reads an index on disk. Run 'index' once in a fresh checkout, and again "
                + "when an answer says it is stale. 'doctor' says whether the index is usable.\n\n"
                + "In standalone desktop or multi-repo environments where workspace detection is not available, "
                + "specify the 'repo' argument on tool calls to point to the repository.\n\n"
                + "Not in the graph: string literals, config keys, log messages, and anything outside "
                + "a .cs file. Use ordinary text search for those.\n\n"
                + "Answers end with a suggested next step written as a shell command, e.g. "
                + "'next: csmesh entrypoints orders'. Each maps to the tool of the same name, so call "
                + "'entrypoints' with filter='orders' rather than reaching for a terminal."
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
