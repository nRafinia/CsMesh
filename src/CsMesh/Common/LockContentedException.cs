namespace CsMesh.Common;

/// <summary>
/// Thrown by the storage layer when the graph file stays held by another process until the
/// rename retries exhaust. Distinct from an arbitrary fault so the runner can answer
/// <see cref="Exit.Contended"/> (retry) instead of <see cref="Exit.Internal"/> (report).
/// </summary>
public sealed class LockContentedException(string message, Exception inner)
    : Exception(message, inner);
