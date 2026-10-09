using Confluent.Kafka;
using Housekeeping;
using Npgsql;
using Sync.Common.Kafka;
using Sync.Common.Persistence;
using Sync.Common.Telemetry;

// Housekeeping service; run with "-- run-once" to do a single cycle and exit.
var builder = Host.CreateApplicationBuilder(args);
builder.AddSyncTelemetry("housekeeping");

var dataSource = NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Username=cdss;Password=cdss;Database=cdss");

var options = builder.Configuration.GetSection(HousekeepingOptions.Section).Get<HousekeepingOptions>() ?? new();
options.Validate();

builder.Services.AddSingleton(dataSource);
builder.Services.Configure<HousekeepingOptions>(builder.Configuration.GetSection(HousekeepingOptions.Section));
builder.Services.AddSingleton<IAdminClient>(_ => new AdminClientBuilder(new AdminClientConfig
{
    BootstrapServers = builder.Configuration["Kafka:BootstrapServers"] ?? KafkaDefaults.DefaultBootstrapServers,
}).Build());
builder.Services.AddSingleton<KafkaRetentionProbe>();
builder.Services.AddSingleton<HousekeepingWorker>();

var runOnce = args.Contains("run-once");
if (!runOnce) builder.Services.AddHostedService(sp => sp.GetRequiredService<HousekeepingWorker>());

var host = builder.Build();

if (!builder.Configuration.GetValue<bool>("SkipSchemaCheck"))
    await SchemaVersionGuard.EnsureAsync(dataSource, ExpectedSchemaVersions.All);

if (runOnce)
{
    var result = await host.Services.GetRequiredService<HousekeepingWorker>().RunCycleAsync(CancellationToken.None);
    Console.WriteLine($"ran={result.Ran} outbox_deleted={result.OutboxDeleted} change_log_archived={result.ChangeLogArchived} " +
                      $"inbox_deleted={result.InboxDeleted} inbox_retention={result.InboxRetention?.ToString() ?? "keep-all"}");
    foreach (var h in result.HeldBack)
        Console.WriteLine($"held_back facility={h.FacilityId} device={h.DeviceId} cursor={h.DeviceCursor}");
    return 0;
}

await host.RunAsync();
return 0;
