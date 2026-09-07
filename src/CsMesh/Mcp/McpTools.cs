using System.Text.Json;
using CsMesh.Commands;
using CsMesh.Common;

namespace CsMesh.Mcp;

/// <summary>
/// The tool catalogue, and the translation from a tool call to a csmesh command.
///
/// Nothing here reimplements a query. Each tool runs the same command the shell would and captures
/// what it printed, so an answer over MCP and an answer in a terminal cannot drift apart -- which
/// they would within one release if the two paths were written separately.
/// </summary>
public static class McpTools
{
    private sealed record Tool(
        string Name,
        string Kind,
        string Description,
        string? Argument,
        string? SecondArgument = null,
        bool Depth = true);

    /// <summary>
    /// Descriptions are written for a model choosing between tools, not for a human reading a man
    /// page. Each says what the tool answers that reading files would not, because a model that
    /// cannot tell these apart falls back to grep -- the exact round trip csmesh exists to remove.
    /// </summary>
    private static readonly Tool[] Catalogue =
    [
        new("where", "where",
            "Find where a symbol lives when you only half-know its name. Searches names, namespaces, "
            + "file paths and route templates, and ranks by how many entrypoints reach each hit. "
            + "Start here when a task names a concept rather than an identifier.",
            "symbol"),

        new("trace", "trace",
            "Call tree from a method or endpoint downwards, following the indirection a static call "
            + "graph loses: interface calls resolve to their DI-bound implementation, and mediator "
            + "dispatch resolves to its handler. Use instead of opening file after file.",
            "symbol"),

        new("impl", "impl",
            "Implementations of an interface or abstract type, with the DI lifetime of each and the "
            + "line where it was registered. Answers which one actually runs in production, which "
            + "grep cannot, since the registration names no implementation at the call site.",
            "symbol"),

        new("blast_radius", "blast",
            "What breaks if a member changes: callers, the HTTP endpoints above them, and the tests "
            + "covering them. Use before editing a signature. Accurate for common method names, "
            + "where a text search returns mostly unrelated types.",
            "symbol"),

        new("entrypoints", "entrypoints",
            "Every way execution enters the codebase -- HTTP routes, hosted services, message "
            + "consumers, CLI commands -- optionally filtered by a word. Use to orient in an "
            + "unfamiliar repository.",
            "filter", Depth: false),

        new("context", "context",
            "One symbol in full: injected dependencies, DI lifetime, members with signatures, and "
            + "the routes bound to it. Cheaper than reading the file when you need the shape rather "
            + "than the bodies.",
            "symbol"),

        new("path", "path",
            "Whether and how execution reaches one symbol from another. Answers 'does this endpoint "
            + "ever touch the database' without reading the chain by hand.",
            "from", "to"),

        new("cycles", "cycles",
            "Dependency cycles between types and namespaces. Design problems rather than control flow.",
            null),

        new("unresolved", "unresolved",
            "Call sites the compiler could not bind, by cause. Read this when a trace looks emptier "
            + "than the code suggests -- every entry is a missing edge, usually a solution that was "
            + "never built.",
            "kind", Depth: false),

        new("changes", "changes",
            "What moved structurally since the last full index: bindings that vanished, handlers "
            + "that changed, edges added or lost. Not a text diff.",
            null),

        new("silence", "silence",
            "Why an expected edge is absent. Use after a trace or impl returned less than expected, "
            + "instead of assuming the code is not there.",
            "symbol"),

        new("map", "map",
            "Project topology in dependency order, with type and entrypoint counts and test projects "
            + "separated. The first call in an unfamiliar repository.",
            null, Depth: false),

        new("doctor", "doctor",
            "Health of the index itself: whether one exists, whether it is behind the working tree, "
            + "whether it was built by this version, and how well references resolved. Run this "
            + "first when an answer looks wrong or empty.",
            null, Depth: false),

        new("index", "index",
            "Build or refresh the index. Incremental when few files changed. Every other tool reads "
            + "what this writes, so run it before anything else in a fresh checkout, and again when "
            + "answers are reported as stale.",
            null, Depth: false)
    ];

    public static List<ToolDescriptor> Descriptors()
    {
        var tools = new List<ToolDescriptor>();

        foreach (var tool in Catalogue)
        {
            var schema = new JsonSchema();

            if (tool.Argument != null)
            {
                schema.Properties[tool.Argument] = new JsonSchemaProperty
                {
                    Type = "string",
                    Description = ArgumentHelp(tool)
                };

                // Only the tools that cannot answer anything without a subject demand one.
                // entrypoints, unresolved and index all have a meaningful bare form.
                if (tool.Kind is not ("entrypoints" or "unresolved")) schema.Required = [tool.Argument];
            }

            if (tool.SecondArgument != null)
            {
                schema.Properties[tool.SecondArgument] = new JsonSchemaProperty
                {
                    Type = "string",
                    Description = "Fully qualified or short name of the destination symbol."
                };

                schema.Required = [tool.Argument!, tool.SecondArgument];
            }

            if (tool.Kind is not ("doctor" or "index"))
            {
                schema.Properties["budget"] = new JsonSchemaProperty
                {
                    Type = "integer",
                    Description = "Approximate token ceiling for the answer. Raise only after narrowing with 'under'."
                };

                schema.Properties["under"] = new JsonSchemaProperty
                {
                    Type = "string",
                    Description = "Restrict to a subtree, e.g. src/Payments. The cheapest way to cut a large answer."
                };
            }

            if (tool.Depth)
            {
                schema.Properties["depth"] = new JsonSchemaProperty
                {
                    Type = "integer",
                    Description = "How many levels to walk."
                };
            }

            if (tool.Kind == "index")
            {
                schema.Properties["full"] = new JsonSchemaProperty
                {
                    Type = "boolean",
                    Description = "Force a complete rebuild instead of patching the existing graph."
                };
            }

            schema.Properties["repo"] = new JsonSchemaProperty
            {
                Type = "string",
                Description = "Optional repository root path or workspace folder. Overrides detected workspace root."
            };

            tools.Add(new ToolDescriptor
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = schema
            });
        }

        return tools;
    }

    private static string ArgumentHelp(Tool tool) => tool.Kind switch
    {
        "where" => "A name, part of a name, a namespace, a path fragment or a route. Need not be exact.",
        "entrypoints" => "Optional word to filter entrypoints by. Omit for all of them.",
        "unresolved" => "Optional cause to filter by, e.g. di or mediatr. Omit for all.",
        "path" => "Fully qualified or short name of the starting symbol.",
        _ => "Fully qualified or short name, e.g. OrderService.SaveAsync."
    };

    /// <summary>
    /// Runs the command behind a tool and returns what it printed.
    ///
    /// Output is captured rather than read from a return value because the commands write as they
    /// go. Capturing is also what keeps stdout clean: this process multiplexes JSON-RPC frames on
    /// the same stream, and a single line escaping from a query would corrupt the transport.
    /// </summary>
    public static ToolCallResult Invoke(string root, string name, JsonElement? arguments)
    {
        var tool = Catalogue.FirstOrDefault(t => t.Name == name);
        if (tool == null)
        {
            return Failure($"unknown tool '{name}'.");
        }

        var explicitRepo = Text(arguments, "repo");
        var effectiveRoot = !string.IsNullOrWhiteSpace(explicitRepo)
            ? RepositoryLocator.FindRoot(explicitRepo)
            : root;

        var argv = new List<string>();

        if (tool.Argument != null && Text(arguments, tool.Argument) is { Length: > 0 } first)
        {
            argv.Add(first);
        }

        if (tool.SecondArgument != null && Text(arguments, tool.SecondArgument) is { Length: > 0 } second)
        {
            argv.Add(second);
        }

        foreach (var flag in new[] { "budget", "depth" })
        {
            if (Number(arguments, flag) is { } value) argv.AddRange([$"--{flag}", value.ToString()]);
        }

        if (Text(arguments, "under") is { Length: > 0 } under) argv.AddRange(["--under", under]);
        if (Boolean(arguments, "full")) argv.Add("--full");

        var opt = new Options([.. argv]);

        var original = Console.Out;
        var buffer = new StringWriter();
        int exit;

        try
        {
            Console.SetOut(buffer);

            exit = tool.Kind switch
            {
                "doctor" => DoctorCommand.Execute(effectiveRoot, opt),
                "index" => IndexCommand.Execute(effectiveRoot, opt),
                _ => QueryCommand.Execute(effectiveRoot, opt, tool.Kind)
            };
        }
        catch (Exception ex)
        {
            Console.SetOut(original);
            Dbg.Log($"tool '{name}' threw: {ex}");
            return Failure($"csmesh failed while running '{name}': {ex.Message}");
        }
        finally
        {
            Console.SetOut(original);
        }

        var text = buffer.ToString().TrimEnd();

        // No index is the one outcome the model cannot act on from the text alone, because every
        // tool would keep returning nothing and none of them would say why in a way that names the
        // fix. Everything else -- a miss, an ambiguity, a truncated walk -- already explains itself.
        if (exit == Exit.NoIndex)
        {
            return Failure(text.Length > 0
                ? text + "\n\nRun the 'index' tool first."
                : "No usable index. Run the 'index' tool first.");
        }

        return new ToolCallResult
        {
            Content = [new ToolContent { Text = text.Length > 0 ? text : "(no output)" }]
        };
    }

    private static ToolCallResult Failure(string message) => new()
    {
        IsError = true,
        Content = [new ToolContent { Text = message }]
    };

    private static string? Text(JsonElement? arguments, string name) =>
        arguments?.ValueKind == JsonValueKind.Object &&
        arguments.Value.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement? arguments, string name)
    {
        if (arguments?.ValueKind != JsonValueKind.Object) return null;
        if (!arguments.Value.TryGetProperty(name, out var value)) return null;

        // Clients are inconsistent about whether an integer arrives as a number or a string.
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), out var n) => n,
            _ => null
        };
    }

    private static bool Boolean(JsonElement? arguments, string name) =>
        arguments?.ValueKind == JsonValueKind.Object &&
        arguments.Value.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;
}
