using CsMesh.Models;

namespace CsMesh.Analysis;

/// <summary>
/// What a build produced: the graph and the project scope it decided.
///
/// The command carries the scope from here rather than deriving a second one, so the scope that
/// decided the index is the one reported. Nothing here is persisted; the graph's on-disk shape and
/// <see cref="Graph.CurrentFormatVersion"/> are untouched.
/// </summary>
public sealed record IndexBuild(Graph Graph, ProjectScope Scope);
