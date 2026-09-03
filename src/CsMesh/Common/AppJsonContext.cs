using System.Text.Json.Serialization;
using CsMesh.Models;
using CsMesh.Telemetry;

namespace CsMesh.Common;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(Graph))]
[JsonSerializable(typeof(Invocation))]
[JsonSerializable(typeof(List<Invocation>))]
internal partial class AppJsonContext : JsonSerializerContext;
