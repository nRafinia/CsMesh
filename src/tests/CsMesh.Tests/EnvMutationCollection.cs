using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Tests that mutate process-wide environment variables.
///
/// doctor resolves its user-global skill block targets, and AgentIntegration its user-global MCP
/// targets, from CLAUDE_CONFIG_DIR, COPILOT_HOME, CODEX_HOME and XDG_CONFIG_HOME. A test that points
/// those at a temp home while another test reads them can make the reader miss a target or, worse,
/// write into the temp directory under the reader's feet. DisableParallelization keeps this
/// collection from running alongside any other, so the reassignment is never observed outside it.
/// </summary>
[CollectionDefinition("env-mutation", DisableParallelization = true)]
public sealed class EnvMutationCollection;
