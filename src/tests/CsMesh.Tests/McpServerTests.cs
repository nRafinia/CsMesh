using System.Text.Json;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Mcp;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The MCP surface is a contract with a client csmesh does not control, and most of the ways it
/// breaks are silent: a server that connects and lists nothing, a notification answered when it
/// should not be, a schema serialised under the wrong casing. None of those produce an error the
/// user can act on, so they have to be caught here.
/// </summary>
[Collection("console-capture")]
public sealed class McpServerTests
{
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }

        public Sandbox(bool indexed = true)
        {
            Root = Path.Combine(Path.GetTempPath(), "csmesh-mcp-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(Root, "src"));

            File.WriteAllText(Path.Combine(Root, "src", "Thing.cs"), """
                namespace Demo;
                public sealed class Thing { public void Go() { } }
                public sealed class Caller { public void Run(Thing t) => t.Go(); }
                """);

            if (indexed) IndexCommand.Execute(Root, new Options([]));
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    /// <summary>
    /// Drives the server the way a client does: frames in on stdin, frames out on stdout.
    /// Exercising Run rather than the handlers is deliberate -- the framing and the reply-or-stay-
    /// silent decision are part of what can break.
    /// </summary>
    private static List<JsonElement> Exchange(string root, params string[] frames)
    {
        var stdin = Console.In;
        var stdout = Console.Out;
        var buffer = new StringWriter();

        try
        {
            Console.SetIn(new StringReader(string.Join("\n", frames) + "\n"));
            Console.SetOut(buffer);
            McpServer.Run(root);
        }
        finally
        {
            Console.SetIn(stdin);
            Console.SetOut(stdout);
        }

        return buffer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
    }

    private static string Call(string root, string tool, string argumentsJson = "{}")
    {
        var frame = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"NAME","arguments":ARGS}}"""
            .Replace("NAME", tool, StringComparison.Ordinal)
            .Replace("ARGS", argumentsJson, StringComparison.Ordinal);

        var replies = Exchange(root, frame);

        return replies[0].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    [Fact]
    public void InitializeEchoesAProtocolVersionTheClientAskedFor()
    {
        using var sandbox = new Sandbox();

        var replies = Exchange(sandbox.Root,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""");

        var result = replies[0].GetProperty("result");

        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("csmesh", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal(AppVersion.Get(), result.GetProperty("serverInfo").GetProperty("version").GetString());
    }

    /// <summary>An unknown revision must still get a version we can speak, not the client's.</summary>
    [Fact]
    public void InitializeFallsBackWhenTheClientAsksForSomethingUnknown()
    {
        using var sandbox = new Sandbox();

        var replies = Exchange(sandbox.Root,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");

        Assert.NotEqual("1999-01-01", replies[0].GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    /// <summary>
    /// MCP field names are camelCase while every csmesh payload is snake_case. Emitting
    /// input_schema would produce a server that connects, lists tools and offers no usable
    /// arguments -- with no error anywhere to explain it.
    /// </summary>
    [Fact]
    public void ToolSchemasAreSerialisedInCamelCase()
    {
        using var sandbox = new Sandbox();

        var replies = Exchange(sandbox.Root, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var tools = replies[0].GetProperty("result").GetProperty("tools");

        Assert.True(tools.GetArrayLength() > 0);

        var first = tools[0];
        Assert.True(first.TryGetProperty("inputSchema", out _));
        Assert.False(first.TryGetProperty("input_schema", out _));
    }

    [Fact]
    public void EveryToolDeclaresAnObjectSchemaAndADescription()
    {
        foreach (var tool in McpTools.Descriptors())
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Name));
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.Equal("object", tool.InputSchema.Type);

            foreach (var required in tool.InputSchema.Required ?? [])
            {
                Assert.True(tool.InputSchema.Properties.ContainsKey(required),
                    $"{tool.Name} requires '{required}' but does not declare it");
            }
        }
    }

    /// <summary>
    /// Notifications carry no id and answering one is a protocol error, so this must not fall
    /// through to the method-not-found branch.
    /// </summary>
    [Fact]
    public void NotificationsGetNoReply()
    {
        using var sandbox = new Sandbox();

        Assert.Empty(Exchange(sandbox.Root, """{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
    }

    [Fact]
    public void AnUnknownMethodWithAnIdGetsAJsonRpcError()
    {
        using var sandbox = new Sandbox();

        var replies = Exchange(sandbox.Root, """{"jsonrpc":"2.0","id":7,"method":"totally/unknown"}""");

        Assert.Equal(-32601, replies[0].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(7, replies[0].GetProperty("id").GetInt32());
    }

    /// <summary>A malformed frame must not close the pipe; the client would blame csmesh entirely.</summary>
    [Fact]
    public void AMalformedFrameIsReportedAndTheServerKeepsGoing()
    {
        using var sandbox = new Sandbox();

        var replies = Exchange(sandbox.Root,
            "{ this is not json",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        Assert.Equal(-32700, replies[0].GetProperty("error").GetProperty("code").GetInt32());
        Assert.True(replies[1].GetProperty("result").GetProperty("tools").GetArrayLength() > 0);
    }

    [Fact]
    public void ATraceCallReturnsTheSameTextTheCommandWouldPrint()
    {
        using var sandbox = new Sandbox();

        var text = Call(sandbox.Root, "trace", """{"symbol":"Caller.Run"}""");

        Assert.Contains("Caller.Run", text, StringComparison.Ordinal);
        Assert.Contains("Thing.Go", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Clients are inconsistent about whether an integer arrives as a number or a string, and a
    /// budget silently dropped would look like csmesh ignoring the caller.
    /// </summary>
    [Fact]
    public void BudgetIsHonouredWhetherItArrivesAsANumberOrAString()
    {
        using var sandbox = new Sandbox();

        var asNumber = Call(sandbox.Root, "where", """{"symbol":"Thing","budget":40}""");
        var asString = Call(sandbox.Root, "where", """{"symbol":"Thing","budget":"40"}""");

        Assert.Equal(asNumber, asString);

        var generous = Call(sandbox.Root, "where", """{"symbol":"Thing","budget":800}""");
        Assert.NotEqual(generous, asNumber);
    }

    /// <summary>
    /// A miss is an answer, not a failure. Flagging it would teach the model to stop reading the
    /// part that tells it what to do next.
    /// </summary>
    [Fact]
    public void AMissIsNotReportedAsAnError()
    {
        using var sandbox = new Sandbox();

        var replies = Exchange(sandbox.Root,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"where","arguments":{"symbol":"NothingLikeThis"}}}""");

        var result = replies[0].GetProperty("result");

        Assert.False(result.TryGetProperty("isError", out var flag) && flag.GetBoolean());
    }

    /// <summary>
    /// The one outcome the model cannot act on from the text alone: every tool would keep
    /// returning nothing and none would name the fix.
    /// </summary>
    [Fact]
    public void AMissingIndexIsAnErrorThatNamesTheToolToRun()
    {
        using var sandbox = new Sandbox(indexed: false);

        var replies = Exchange(sandbox.Root,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"where","arguments":{"symbol":"Thing"}}}""");

        var result = replies[0].GetProperty("result");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("index", result.GetProperty("content")[0].GetProperty("text").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnknownToolIsAnErrorRatherThanASilentEmptyAnswer()
    {
        using var sandbox = new Sandbox();

        var replies = Exchange(sandbox.Root,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"nonexistent","arguments":{}}}""");

        Assert.True(replies[0].GetProperty("result").GetProperty("isError").GetBoolean());
    }

    /// <summary>
    /// stdout is the transport. A command printing straight to it would interleave with the
    /// frames and corrupt the stream, so every reply has to be exactly one parseable line.
    /// </summary>
    [Fact]
    public void EveryReplyIsExactlyOneParseableFrame()
    {
        using var sandbox = new Sandbox();

        var replies = Exchange(sandbox.Root,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"doctor","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"map","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"index","arguments":{}}}""");

        Assert.Equal(3, replies.Count);

        foreach (var reply in replies)
        {
            Assert.Equal("2.0", reply.GetProperty("jsonrpc").GetString());
            Assert.True(reply.TryGetProperty("result", out _));
        }
    }
}
