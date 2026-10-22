// One CLI command: its name, its usage line, and what it does with the arguments after its name.

namespace IngestionPipeline.Commands;

public interface ICommand
{
    string Name { get; }

    string Usage { get; }

    // 0 done, 1 a step or check failed, 2 not ready to run (missing settings, files or an earlier step).
    Task<int> RunAsync(string[] args);
}
