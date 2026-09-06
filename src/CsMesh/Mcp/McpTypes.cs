using System.Text.Json;
using System.Text.Json.Serialization;

namespace CsMesh.Mcp;

/// <summary>
/// JSON-RPC 2.0 envelopes and the subset of MCP shapes csmesh answers.
///
/// These live apart from the CLI's models and have their own serializer context because the wire
/// format is not ours to choose: MCP fields are camelCase (jsonrpc, inputSchema, isError) while
/// every csmesh payload is snake_case. One context cannot be both, and quietly emitting
/// input_schema would produce a server that connects, lists nothing, and gives no reason.
/// </summary>
public sealed class RpcRequest
{
    public string Jsonrpc { get; set; } = "2.0";

    /// <summary>
    /// Kept as raw JSON because the spec allows a string or a number and the reply has to echo
    /// back exactly what arrived. Absent for notifications, which take no reply at all.
    /// </summary>
    public JsonElement? Id { get; set; }

    public string Method { get; set; } = string.Empty;
    public JsonElement? Params { get; set; }
}

public sealed class RpcResponse
{
    public string Jsonrpc { get; set; } = "2.0";
    public JsonElement? Id { get; set; }
    public JsonElement? Result { get; set; }
    public RpcError? Error { get; set; }
}

public sealed class RpcError
{
    public int Code { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class InitializeResult
{
    public string ProtocolVersion { get; set; } = string.Empty;
    public ServerCapabilities Capabilities { get; set; } = new();
    public ServerInfo ServerInfo { get; set; } = new();

    /// <summary>
    /// Shown by clients before any tool runs. Used here to say the one thing that decides whether
    /// the tools return anything useful: they read an index, and the index has to exist.
    /// </summary>
    public string? Instructions { get; set; }
}

public sealed class ServerCapabilities
{
    public ToolsCapability Tools { get; set; } = new();
}

public sealed class ToolsCapability
{
    /// <summary>The catalogue is fixed at build time, so there is nothing to notify about.</summary>
    public bool ListChanged { get; set; }
}

public sealed class ServerInfo
{
    public string Name { get; set; } = "csmesh";
    public string Version { get; set; } = string.Empty;
}

public sealed class ToolsListResult
{
    public List<ToolDescriptor> Tools { get; set; } = [];
}

public sealed class ToolDescriptor
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public JsonSchema InputSchema { get; set; } = new();
}

public sealed class JsonSchema
{
    public string Type { get; set; } = "object";
    public Dictionary<string, JsonSchemaProperty> Properties { get; set; } = new();
    public List<string>? Required { get; set; }
}

public sealed class JsonSchemaProperty
{
    public string Type { get; set; } = "string";
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("default")]
    public int? Default { get; set; }
}

public sealed class ToolCallResult
{
    public List<ToolContent> Content { get; set; } = [];

    /// <summary>
    /// Reserved for csmesh failing to answer, not for csmesh answering "nothing matched".
    /// A miss, an ambiguity and an over-budget walk are all real answers an agent acts on, and
    /// flagging them as errors would teach the model to stop reading the one part that tells it
    /// what to do next.
    /// </summary>
    public bool IsError { get; set; }
}

public sealed class ToolContent
{
    public string Type { get; set; } = "text";
    public string Text { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcResponse))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(ToolsListResult))]
[JsonSerializable(typeof(ToolCallResult))]
[JsonSerializable(typeof(JsonElement))]
internal partial class McpJsonContext : JsonSerializerContext;
