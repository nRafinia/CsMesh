using System.Text.Json.Serialization;

namespace CsMesh.Telemetry;

/// <summary>
/// The read contract for usage.jsonl, scoped to telemetry so the global graph serializer is not
/// loosened.
///
/// A public release wrote records before the snake_case policy existed (item 2 of the schema work),
/// so a PascalCase line must still bind. Case-insensitive matching is set here and only here:
/// <see cref="Common.AppJsonContext"/> keeps its case-sensitive snake_case options, so a graph with
/// PascalCase keys is ignored exactly as before.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Invocation))]
internal partial class TelemetryReadContext : JsonSerializerContext;
