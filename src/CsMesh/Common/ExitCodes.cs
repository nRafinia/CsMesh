namespace CsMesh.Common;

/// <summary>
/// Standard CLI process exit codes. Agents branch on these; keep them stable.
/// </summary>
public static class Exit
{
    public const int Ok = 0;
    public const int NotFound = 1;
    public const int OverBudget = 2;
    public const int Ambiguous = 3;
    public const int NoIndex = 4;
    public const int Usage = 64;

    /// <summary>
    /// Unhandled failure inside csmesh itself. Distinct from <see cref="Usage"/> so a crash is
    /// never mistaken for a bad command line.
    /// </summary>
    public const int Internal = 70;
}
