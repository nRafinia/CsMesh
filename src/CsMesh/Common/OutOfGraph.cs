using CsMesh.Models;

namespace CsMesh.Common;

/// <summary>
/// Works out what a caller was actually asking about when it was not a symbol, and points at the
/// tool that can answer it.
///
/// The graph holds declarations. Config keys, route strings, log messages, package names and
/// anything outside a .cs file are all legitimate things to be looking for and none of them are in
/// it. The existing message says so once, in the same words, for every one of those cases -- so a
/// caller that asked for ConnectionStrings and a caller that asked for POST /api/v1/credentials
/// get identical advice, and only one of them is being sent somewhere useful. The second is
/// already answerable: routes are indexed, as tags.
///
/// The point of this is a next command, not a category. An agent that reads "not in the graph"
/// falls back to grep over the whole repository; an agent that reads a specific command runs it.
/// </summary>
public static class OutOfGraph
{
    /// <summary>
    /// <paramref name="Decisive"/> means the input cannot be a C# identifier at all -- it has a
    /// space, a slash, a colon, a file extension, or it is screaming snake case. Those must be
    /// answered before the near-miss list gets a turn.
    ///
    /// Running the suggester first looked right and was not. Token matching will always find
    /// something: ASPNETCORE_ENVIRONMENT shares words with a method called
    /// LooksLikeEnvironmentVariable, and "could not save the order" shares words with Save. Both
    /// are true and both are useless, and printing them buries the one line that would have sent
    /// the caller to the right file.
    ///
    /// Where the input is identifier-shaped -- a bare package name like Hangfire, a config root
    /// like Serilog -- the ambiguity is real and the suggester should speak first.
    /// </summary>
    public sealed record Verdict(string Reason, IReadOnlyList<string> Next, bool Decisive = true);

    private static readonly string[] ConfigRoots =
    [
        "connectionstrings", "logging", "loglevel", "serilog", "kestrel", "allowedhosts",
        "applicationinsights", "authentication", "jwt", "cors", "healthchecks", "featureflags",
        "opentelemetry", "hangfire", "redis", "rabbitmq", "masstransit"
    ];

    private static readonly string[] NonSourceExtensions =
    [
        ".json", ".yml", ".yaml", ".xml", ".config", ".razor", ".cshtml", ".html", ".css",
        ".js", ".ts", ".sql", ".md", ".txt", ".props", ".targets", ".sh", ".ps1", ".env"
    ];

    /// <summary>
    /// Classifies a query that resolved to nothing, or null when it looks like an ordinary symbol
    /// name that simply is not here -- in which case a near-miss list is the better answer and
    /// this must stay quiet rather than talk over it.
    /// </summary>
    public static Verdict? Classify(string query, Graph g)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var q = query.Trim();

        if (q.StartsWith("TODO", StringComparison.OrdinalIgnoreCase) ||
            q.StartsWith("FIXME", StringComparison.OrdinalIgnoreCase) ||
            q.StartsWith("HACK", StringComparison.OrdinalIgnoreCase))
        {
            return new Verdict(
                "Code comments are not symbols; the indexer reads declarations, not trivia.",
                [$"grep -rn \"{q}\" --include=*.cs ."]);
        }

        // A route is the one case here the graph can actually answer, because entrypoints carry
        // their template as a tag. Sending this to grep would be the wrong answer twice over.
        if (LooksLikeRoute(q))
        {
            // The resource sits at the end, not the front. Taking the first usable segment picked
            // "api" out of /api/v1/credentials -- the one word in the route that narrows nothing.
            var segment = q.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => !s.StartsWith('{')
                            && !IsVersionSegment(s)
                            && !string.Equals(s, "api", StringComparison.OrdinalIgnoreCase))
                .LastOrDefault() ?? q;

            return new Verdict(
                "That is a route, not a symbol -- but routes are indexed as tags on their handlers.",
                [$"csmesh entrypoints {segment}", $"csmesh where {segment}"]);
        }

        if (LooksLikeEnvironmentVariable(q))
        {
            return new Verdict(
                "Environment variables are resolved at run time and never appear as declarations.",
                [
                    $"grep -rn \"{q}\" --include=*.json --include=*.yml --include=Dockerfile* .",
                    "check launchSettings.json and any compose file"
                ]);
        }

        if (LooksLikeFile(q))
        {
            var razor = q.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                        || q.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

            return new Verdict(
                razor
                    ? "Razor markup is generated at build time and is not in the source graph."
                    : "Only .cs files are indexed.",
                [$"grep -rn \"{Path.GetFileNameWithoutExtension(q)}\" ."]);
        }

        if (LooksLikeConfigKey(q))
        {
            var root = q.Split(':', '.').First();
            return new Verdict(
                "That reads as a configuration key. Config values live in files the indexer does not parse.",
                [
                    $"grep -rn \"{root}\" --include=appsettings*.json --include=*.yml .",
                    "also check user-secrets and environment variables"
                ],
                Decisive: q.Contains(':'));
        }

        // Assembly names come from the graph itself, so this stays accurate as the solution's
        // dependencies change rather than drifting against a hardcoded list of popular packages.
        var assembly = g.ExternalTypes
            .Select(x => x.Assembly)
            .FirstOrDefault(a => a.Length > 0 &&
                                 (string.Equals(a, q, StringComparison.OrdinalIgnoreCase) ||
                                  a.StartsWith(q + ".", StringComparison.OrdinalIgnoreCase)));

        if (assembly != null)
        {
            return new Verdict(
                $"{q} is a package ({assembly}), not something declared here. csmesh indexes source, not assemblies.",
                ["csmesh doctor", $"csmesh where {q} --budget 400"],
                Decisive: false);
        }

        if (q.Contains(' '))
        {
            return new Verdict(
                "That is prose, not an identifier -- a log message or UI string, most likely.",
                [$"grep -rn \"{q}\" ."]);
        }

        // Anything left looks like a name. Let the near-miss list speak instead.
        return null;
    }

    private static bool IsVersionSegment(string s) =>
        s.Length > 1 && (s[0] == 'v' || s[0] == 'V') && s[1..].All(char.IsDigit);

    private static bool LooksLikeRoute(string q) =>
        q.StartsWith('/') ||
        q.StartsWith("api/", StringComparison.OrdinalIgnoreCase) ||
        (q.Contains('/') && !q.Contains('\\') && !LooksLikeFile(q));

    private static bool LooksLikeEnvironmentVariable(string q) =>
        q.Length > 2 &&
        q.Contains('_') &&
        q.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_');

    private static bool LooksLikeFile(string q) =>
        NonSourceExtensions.Any(e => q.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ||
        q.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
        q.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
        q.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        q.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeConfigKey(string q)
    {
        if (q.Contains(':')) return true;

        var head = q.Split('.').First().ToLowerInvariant();
        return ConfigRoots.Contains(head);
    }
}
