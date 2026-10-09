using Confluent.Kafka;
using Npgsql;
using Orchestrator.Clients;
using Orchestrator.Consumers;
using Orchestrator.Graph;
using Orchestrator.Persistence;
using Sync.Common.Evaluation;
using Sync.Common.Kafka;
using Sync.Common.Persistence;
using Sync.Common.Recommendations;
using Sync.Common.Telemetry;

// Orchestrator: consumes persisted events, calls the Recommendation Service and stores the result.
var builder = Host.CreateApplicationBuilder(args);
builder.AddSyncTelemetry("orchestrator");

var dataSource = NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Username=cdss;Password=cdss;Database=cdss");
var rag = builder.Configuration.GetSection("RecommendationService");

builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(new AblationOptions(builder.Configuration.GetValue<bool>(AblationOptions.ConfigKey)));
builder.Services.AddSingleton<IOrchestratorStore, PostgresOrchestratorStore>();
builder.Services.AddSingleton(RecommendationResponseValidator.FromOutputDirectory());
builder.Services.AddSingleton<OrchestratorGraph>();
builder.Services.AddSingleton<WorkflowRunner>();
builder.Services.AddSingleton<OutcomeRouter>();
builder.Services.AddSingleton<IProducer<string, byte[]>>(_ => new ProducerBuilder<string, byte[]>(
    KafkaDefaults.Producer(builder.Configuration["Kafka:BootstrapServers"] ?? KafkaDefaults.DefaultBootstrapServers)).Build());

// Allow 60 s per call with bounded retries and a circuit breaker; anything still failing becomes ADVICE_DEFERRED.
var attemptTimeout = TimeSpan.FromSeconds(rag.GetValue("AttemptTimeoutSeconds", 60));
builder.Services.AddHttpClient(HttpRecommendationClient.ClientName, client =>
    {
        client.BaseAddress = new Uri(rag["BaseUrl"] ?? "http://localhost:5080");
        client.Timeout = Timeout.InfiniteTimeSpan; // the resilience pipeline owns all timeouts
    })
    .AddStandardResilienceHandler(o =>
    {
        o.AttemptTimeout.Timeout = attemptTimeout;
        o.Retry.MaxRetryAttempts = rag.GetValue("MaxRetryAttempts", 2);
        o.Retry.Delay = TimeSpan.FromSeconds(rag.GetValue("RetryDelaySeconds", 2.0));
        o.TotalRequestTimeout.Timeout = attemptTimeout * 3 + TimeSpan.FromSeconds(30);
        o.CircuitBreaker.SamplingDuration = attemptTimeout * 2;
    });
builder.Services.AddSingleton<IRecommendationClient, HttpRecommendationClient>();

builder.Services.AddHostedService<PersistedEventsConsumer>();
builder.Services.AddHostedService<RetryTopicsConsumer>();

var host = builder.Build();

if (host.Services.GetRequiredService<AblationOptions>().Enabled)
    host.Services.GetRequiredService<ILogger<Program>>().LogWarning(AblationOptions.Warning);

if (!builder.Configuration.GetValue<bool>("SkipSchemaCheck"))
    await SchemaVersionGuard.EnsureAsync(dataSource, ExpectedSchemaVersions.All);

await host.RunAsync();
