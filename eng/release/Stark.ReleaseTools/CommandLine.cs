namespace Stark.ReleaseTools;

internal sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    private CommandLine(string command)
    {
        Command = command;
    }

    public string Command { get; }

    public static CommandLine Parse(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ReleaseToolException("A release-tool command is required.");
        }

        var result = new CommandLine(args[0]);
        for (var index = 1; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
            {
                throw new ReleaseToolException($"Unexpected argument '{token}'. Options must use --name value.");
            }

            var equals = token.IndexOf('=');
            if (equals >= 0)
            {
                result.Add(token[..equals], token[(equals + 1)..]);
                continue;
            }

            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result.Add(token, args[++index]);
            }
            else
            {
                if (result._options.ContainsKey(token) || !result._flags.Add(token))
                {
                    throw new ReleaseToolException($"Option '{token}' was supplied more than once.");
                }
            }
        }

        return result;
    }

    public bool HasFlag(string name)
    {
        if (_options.ContainsKey(name))
        {
            throw new ReleaseToolException($"Flag '{name}' does not accept a value.");
        }

        return _flags.Contains(name);
    }

    public string Required(string name)
    {
        var value = Optional(name);
        if (string.IsNullOrEmpty(value))
        {
            throw new ReleaseToolException($"Required option '{name}' is missing.");
        }

        return value;
    }

    public string Optional(string name, string defaultValue = "")
    {
        if (_flags.Contains(name))
        {
            throw new ReleaseToolException($"Option '{name}' requires a value.");
        }

        if (!_options.TryGetValue(name, out var values))
        {
            return defaultValue;
        }

        if (values.Count != 1)
        {
            throw new ReleaseToolException($"Option '{name}' must be supplied exactly once.");
        }

        return values[0];
    }

    public string? OptionalNullable(string name)
    {
        if (_flags.Contains(name))
        {
            throw new ReleaseToolException($"Option '{name}' requires a value.");
        }

        if (!_options.TryGetValue(name, out var values))
        {
            return null;
        }

        if (values.Count != 1)
        {
            throw new ReleaseToolException($"Option '{name}' must be supplied exactly once.");
        }

        return values[0];
    }

    public string[] Values(string name)
        => _options.TryGetValue(name, out var values) ? [.. values] : [];

    public void RejectUnknown(params string[] allowed)
    {
        var permitted = allowed.ToHashSet(StringComparer.Ordinal);
        var unknown = _options.Keys.Concat(_flags).Where(key => !permitted.Contains(key)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length != 0)
        {
            throw new ReleaseToolException($"Unknown option(s): {string.Join(", ", unknown)}");
        }
    }

    private void Add(string name, string value)
    {
        if (_flags.Contains(name) || _options.ContainsKey(name))
        {
            throw new ReleaseToolException($"Option '{name}' was supplied more than once.");
        }

        _options.Add(name, [value]);
    }
}
