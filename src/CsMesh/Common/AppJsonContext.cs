using System.Text.Json.Serialization;
using CsMesh.Models;
using CsMesh.Telemetry;

namespace CsMesh.Common;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(Graph))]
[JsonSerializable(typeof(Invocation))]
[JsonSerializable(typeof(List<Invocation>))]
[JsonSerializable(typeof(QueryResult))]
internal partial class AppJsonContext : JsonSerializerContext;
