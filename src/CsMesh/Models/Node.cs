namespace CsMesh.Models;

/// <summary>
/// Represents a symbol node in the code graph (e.g. type, interface, method, property, route, or message handler).
/// </summary>
public sealed class Node
{
    public int Id { get; set; }

    /// <summary>
    /// Fully qualified display name (e.g. Acme.Api.PaymentController.Post).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Short symbol name used for matching and display (e.g. PaymentController.Post).
    /// </summary>
    public string Short { get; set; } = string.Empty;

    /// <summary>
    /// The symbol kind: method | type | interface | property.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Source file path relative to repository root.
    /// </summary>
    public string File { get; set; } = string.Empty;

    /// <summary>
    /// 1-based source line number where the declaration starts.
    /// </summary>
    public int Line { get; set; }

    /// <summary>
    /// 1-based line where the declaration ends. Without it a git hunk cannot be attributed to a
    /// symbol: a diff gives line ranges, and "nearest declaration above" guesses wrong on nested
    /// types, local functions and multi-class files. Falls back to <see cref="Line"/> for graphs
    /// written before this existed.
    /// </summary>
    public int EndLine { get; set; }

    /// <summary>True when the given 1-based line falls inside this declaration.</summary>
    public bool Covers(int line) => line >= Line && line <= Math.Max(EndLine, Line);

    /// <summary>
    /// The shape of the symbol: a method's parameters and return type, a property's or field's
    /// type with its nullable annotation, an enum's underlying type. Empty when it does not apply.
    /// Answers "what does this look like" without opening the file, which is the question a caller
    /// usually has right after finding out where something lives.
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>
    /// The .csproj this symbol belongs to, without extension. Empty when no project file was
    /// found above the source file. This is data, not policy: a blast radius that crosses four
    /// projects is a different size of change from one that stays inside a single assembly, and
    /// the caller cannot tell from a list of type names.
    /// </summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>
    /// Semantic tags associated with the symbol (e.g. route:GET /payments, handler, controller, di:scoped).
    /// </summary>
    public List<string> Tags { get; set; } = [];
}
