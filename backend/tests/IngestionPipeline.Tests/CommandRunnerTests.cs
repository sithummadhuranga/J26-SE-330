// Command dispatch with fake commands, and the real container: every command the CLI had still resolves by name.

using IngestionPipeline.Commands;
using IngestionPipeline.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IngestionPipeline.Tests;

public class CommandRunnerTests
{
    private sealed class FakeCommand(string name, int exitCode) : ICommand
    {
        public string Name => name;
        public string Usage => name + " [--flag]";
        public string[]? ReceivedArgs { get; private set; }

        public Task<int> RunAsync(string[] args)
        {
            ReceivedArgs = args;
            return Task.FromResult(exitCode);
        }
    }

    [Fact]
    public async Task The_named_command_runs_with_the_arguments_after_its_name()
    {
        var load = new FakeCommand("load", 7);
        var runner = new CommandRunner([new FakeCommand("chunk", 0), load], TextWriter.Null);
        Assert.Equal(7, await runner.RunAsync(["load", "--dry-run"]));
        Assert.Equal(["--dry-run"], load.ReceivedArgs!);
    }

    [Theory]
    [InlineData]
    [InlineData("no-such-command")]
    public async Task An_unknown_or_missing_command_prints_every_usage_and_exits_2(params string[] args)
    {
        var error = new StringWriter();
        var runner = new CommandRunner([new FakeCommand("chunk", 0), new FakeCommand("load", 0)], error);
        Assert.Equal(2, await runner.RunAsync(args));
        Assert.Equal("usage: ingestion-pipeline <chunk [--flag] | load [--flag]>" + Environment.NewLine, error.ToString());
    }

    [Fact]
    public void Two_commands_with_one_name_are_refused() =>
        Assert.Throws<ArgumentException>(() => new CommandRunner([new FakeCommand("load", 0), new FakeCommand("load", 1)], TextWriter.Null));

    [Fact]
    public void Every_command_of_the_cli_resolves_from_the_container()
    {
        var settings = new PipelineSettings(null, null, null, PipelineSettings.DefaultCostCeilingUsd);
        using var services = new ServiceCollection().AddIngestionPipeline(RepoPaths.Discover(), settings).BuildServiceProvider();
        var names = services.GetServices<ICommand>().Select(c => c.Name).Order(StringComparer.Ordinal);
        Assert.Equal(["annotate", "check-structure", "chunk", "divergence-report", "embed", "export", "hash-transcription", "load",
            "propose-aliases", "propose-figures", "propose-sections", "propose-tables", "rerank", "smoke", "verify"], names);
    }
}
