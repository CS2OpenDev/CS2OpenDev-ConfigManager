namespace CS2OpenDev.ConfigManager.Cli;

/// <summary>A command line acb does not accept. Exits 64 with the message and the command's usage.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>
///     One command's arguments: positionals, flags and options taking a value. Each command names exactly what it
///     takes, and anything else is a usage error, so a misspelt option never runs as if it were absent.
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    private CommandLine(string command) => Command = command;

    public string Command { get; }

    public List<string> Positionals { get; } = [];

    /// <param name="args">Everything after the command name.</param>
    /// <param name="command">The command, for messages.</param>
    /// <param name="flags">Options that take no value, without their leading dashes.</param>
    /// <param name="valued">Options that take one value; they may repeat.</param>
    public static CommandLine Parse(IReadOnlyList<string> args, string command, IEnumerable<string> flags, IEnumerable<string> valued)
    {
        HashSet<string> flagSet = new(flags, StringComparer.Ordinal);
        HashSet<string> valuedSet = new(valued, StringComparer.Ordinal);
        CommandLine line = new(command);
        bool onlyPositionals = false;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (onlyPositionals || !arg.StartsWith('-') || arg == "-")
            {
                line.Positionals.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                onlyPositionals = true;
                continue;
            }

            string name = arg == "-o" ? "output" : arg.StartsWith("--", StringComparison.Ordinal) ? arg[2..] : "";
            string? inline = null;
            int equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                inline = name[(equals + 1)..];
                name = name[..equals];
            }

            if (flagSet.Contains(name) && inline is null)
            {
                line._flags.Add(name);
            }
            else if (valuedSet.Contains(name))
            {
                string value = inline ?? (i + 1 < args.Count ? args[++i] : throw new UsageException($"{arg} needs a value."));
                if (!line._values.TryGetValue(name, out List<string>? list))
                {
                    list = [];
                    line._values[name] = list;
                }

                list.Add(value);
            }
            else
            {
                throw new UsageException($"acb {command} has no option {arg}.");
            }
        }

        return line;
    }

    public bool Flag(string name) => _flags.Contains(name);

    /// <summary>The value of a single-valued option, or null. Given twice, it is a usage error rather than a guess.</summary>
    public string? Value(string name)
    {
        if (!_values.TryGetValue(name, out List<string>? list))
        {
            return null;
        }

        return list.Count == 1 ? list[0] : throw new UsageException($"--{name} is given {list.Count} times.");
    }

    /// <summary>Every value of a repeatable option, commas splitting one value into several; null when absent.</summary>
    public IReadOnlyList<string>? List(string name) =>
        _values.TryGetValue(name, out List<string>? list)
            ? [.. list.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))]
            : null;

    /// <summary>The one positional a command takes; a usage error when missing or when there are more.</summary>
    public string Single(string what) => Positionals.Count switch
    {
        1 => Positionals[0],
        0 => throw new UsageException($"acb {Command} needs {what}."),
        _ => throw new UsageException($"acb {Command} takes one {what}; it was given {Positionals.Count}.")
    };

    public void NoPositionals()
    {
        if (Positionals.Count > 0)
        {
            throw new UsageException($"acb {Command} takes no '{Positionals[0]}'.");
        }
    }
}
