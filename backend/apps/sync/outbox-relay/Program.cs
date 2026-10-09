using Confluent.Kafka;
using Npgsql;
using OutboxRelay;
using Sync.Common.Kafka;
using Sync.Common.Persistence;
using Sync.Common.Telemetry;

// Outbox relay: publishes outbox rows to Kafka.
var builder = Host.CreateApplicationBuilder(args);
builder.AddSyncTelemetry("outbox-relay");

var dataSource = NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Username=cdss;Password=cdss;Database=cdss");

builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton<IProducer<string, byte[]>>(_ => new ProducerBuilder<string, byte[]>(
    KafkaDefaults.Producer(builder.Configuration["Kafka:BootstrapServers"] ?? KafkaDefaults.DefaultBootstrapServers)).Build());
builder.Services.AddHostedService<RelayWorker>();

var host = builder.Build();

if (!builder.Configuration.GetValue<bool>("SkipSchemaCheck"))
    await SchemaVersionGuard.EnsureAsync(dataSource, ExpectedSchemaVersions.All);

await host.RunAsync();
