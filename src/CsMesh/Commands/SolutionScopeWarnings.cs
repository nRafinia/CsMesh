using CsMesh.Analysis;

namespace CsMesh.Commands;

/// <summary>
/// Renders one line per solution that was found but did not fully decide scope. Shared by doctor and
/// index so the command that made the decision and the one that explains it cannot drift, and so a
/// fall back to the ProjectReference closure is never silent.
///
/// Every line is a warning, not a fault: the graph still answers, it just covers a set the solution
/// did not name. Nothing here changes an exit code.
/// </summary>
internal static class SolutionScopeWarnings
{
    internal const string Label = "solution        ";

    public static IEnumerable<string> Lines(ProjectScope scope)
    {
        foreach (var finding in scope.SolutionFindings)
        {
            var line = $"{Label}{finding.Solution}: ";

            if (finding.ParseError is not null)
            {
                line += $"could not be read ({finding.ParseError})";
            }
            else if (finding.Named == 0)
            {
                line += "lists no project path";
            }
            else
            {
                line += $"{finding.Matched} of {finding.Named} project path(s) matched on disk " +
                        $"(first unmatched: {finding.FirstUnmatched})";
            }

            // One suffix for the scope, not one per solution: the fall back is a property of the
            // decision, and a partial match beside a solution that did decide is not a fall back.
            if (scope.FellBackToClosure)
            {
                line += "; scope fell back to the ProjectReference closure";
            }

            yield return line;
        }
    }
}
