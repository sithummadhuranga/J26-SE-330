using Confluent.Kafka;
using IngestPersister.Consumers;
using IngestPersister.Persistence;
using Npgsql;
using Sync.Common.Evaluation;
using Sync.Common.Kafka;
using Sync.Common.Persistence;
using Sync.Common.Telemetry;

// Ingest persister: reads wound-events and writes them to PostgreSQL.
var builder = Host.CreateApplicationBuilder(args);
builder.AddSyncTelemetry("ingest-persister");

var dataSource = NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Username=cdss;Password=cdss;Database=cdss");

builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(new AblationOptions(builder.Configuration.GetValue<bool>(AblationOptions.ConfigKey)));
builder.Services.AddSingleton<PersisterTransaction>();
builder.Services.AddSingleton<IProducer<string, byte[]>>(_ => new ProducerBuilder<string, byte[]>(
    KafkaDefaults.Producer(builder.Configuration["Kafka:BootstrapServers"] ?? KafkaDefaults.DefaultBootstrapServers)).Build());
builder.Services.AddHostedService<WoundEventsConsumer>();

var host = builder.Build();

if (host.Services.GetRequiredService<AblationOptions>().Enabled)
    host.Services.GetRequiredService<ILogger<Program>>().LogWarning(AblationOptions.Warning);

if (!builder.Configuration.GetValue<bool>("SkipSchemaCheck"))
    await SchemaVersionGuard.EnsureAsync(dataSource, ExpectedSchemaVersions.All);

await host.RunAsync();
