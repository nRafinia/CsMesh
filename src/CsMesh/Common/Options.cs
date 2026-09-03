namespace CsMesh.Common;

/// <summary>
/// Lightweight parser for command line arguments, flags, and positional values.
/// </summary>
public sealed class Options
{
    private readonly Dictionary<string, string?> _flags = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positional { get; } = [];

    public Options(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                var name = a[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                {
                    _flags[name] = args[++i];
                }
                else
                {
                    _flags[name] = null;
                }
            }
            else if (a.StartsWith("-") && a.Length > 1)
            {
                var name = a[1..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                {
                    _flags[name] = args[++i];
                }
                else
                {
                    _flags[name] = null;
                }
            }
            else
            {
                Positional.Add(a);
            }
        }
    }

    public bool Flag(string name) => _flags.ContainsKey(name);
    public string? Value(string name) => _flags.TryGetValue(name, out var v) ? v : null;
    public string Get(string name, string defaultValue) => Value(name) ?? defaultValue;
    public int Int(string name, int defaultValue) => int.TryParse(Value(name), out var v) ? v : defaultValue;
}
