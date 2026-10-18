// Picks the command named by the first argument and runs it with the rest; anything else prints the usage.

namespace IngestionPipeline.Commands;

public sealed class CommandRunner
{
    private readonly IReadOnlyList<ICommand> _commands;
    private readonly Dictionary<string, ICommand> _byName;
    private readonly TextWriter _error;

    public CommandRunner(IEnumerable<ICommand> commands, TextWriter error)
    {
        _commands = [.. commands];
        _byName = _commands.ToDictionary(c => c.Name, StringComparer.Ordinal);
        _error = error;
    }

    public Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && _byName.TryGetValue(args[0], out var command))
            return command.RunAsync(args[1..]);
        _error.WriteLine($"usage: ingestion-pipeline <{string.Join(" | ", _commands.Select(c => c.Usage))}>");
        return Task.FromResult(2);
    }
}
