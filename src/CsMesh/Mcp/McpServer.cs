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

    public static int Run(string root)
    {
        // stdout is the transport. Anything a command prints is captured in McpTools; anything
        // csmesh logs about itself goes to stderr, which the spec leaves free for exactly this.
        Dbg.Log($"mcp: serving {root}");

        // Single-threaded by design: one frame is read, dispatched and answered before the next
        // is looked at. McpTools swaps Console.Out around each command to capture its output, and
        // that is only safe while nothing else can be writing. Anything that makes this loop
        // concurrent breaks the capture first and silently -- output would land in the wrong
        // reply, or in the transport.
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            RpcRequest? request;
            try
            {
                request = JsonSerializer.Deserialize(line, McpJsonContext.Default.RpcRequest);
            }
            catch (JsonException ex)
            {
                Dbg.Log($"mcp: unparseable frame: {ex.Message}");
                Write(new RpcResponse { Error = new RpcError { Code = ParseError, Message = "parse error" } });
                continue;
            }

            if (request == null) continue;

            try
            {
                Dispatch(root, request);
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

    private static void Dispatch(string root, RpcRequest request)
    {
        switch (request.Method)
        {
            case "initialize":
                Write(Reply(request, Element(Initialize(request), McpJsonContext.Default.InitializeResult)));
                break;

            case "tools/list":
                var list = new ToolsListResult { Tools = McpTools.Descriptors() };
                Write(Reply(request, Element(list, McpJsonContext.Default.ToolsListResult)));
                break;

            case "tools/call":
                var name = request.Params?.TryGetProperty("name", out var n) == true ? n.GetString() : null;
                JsonElement? arguments = request.Params?.TryGetProperty("arguments", out var a) == true ? a : null;

                var result = McpTools.Invoke(root, name ?? string.Empty, arguments);
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
            case "notifications/initialized":
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
