namespace SecureIntake.Host;

/// <summary>
/// <c>secureintake &lt;verb&gt; [sub-verb] [--option value] [--flag]</c>. Options of the form
/// <c>--Section:Key=value</c> are configuration overrides with the highest precedence.
/// </summary>
internal sealed class CliArgs
{
    public List<string> Verbs { get; } = [];
    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string?> ConfigOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static CliArgs Parse(string[] args)
    {
        var cli = new CliArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                cli.Verbs.Add(a);
                continue;
            }
            var key = a[2..];
            var eq = key.IndexOf('=', StringComparison.Ordinal);
            if (key.Contains(':', StringComparison.Ordinal) && eq > 0)
            {
                cli.ConfigOverrides[key[..eq]] = key[(eq + 1)..];
            }
            else if (eq > 0)
            {
                cli.Options[key[..eq]] = key[(eq + 1)..];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                cli.Options[key] = args[++i];
            }
            else
            {
                cli.Options[key] = "true";
            }
        }
        return cli;
    }

    public string Verb(int index) => index < Verbs.Count ? Verbs[index] : "";

    public string? Get(string name) => Options.GetValueOrDefault(name);

    public string Require(string name) =>
        Options.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new CliException($"Missing required option --{name}.");

    public bool Flag(string name) => Options.TryGetValue(name, out var v) && v == "true";
}

internal sealed class CliException : Exception
{
    public CliException(string message) : base(message) { }
    public CliException() { }
    public CliException(string message, Exception inner) : base(message, inner) { }
}
