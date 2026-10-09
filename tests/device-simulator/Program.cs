using System.Globalization;
using System.Text;
using System.Text.Json;
using DeviceSimulator;

// Device simulator for 1-100 phones, e.g. `-- --devices 10 --events 20 [--mode baseline]`.
var options = SimOptions.Parse(args);
var clock = TimeProvider.System;

using var http = new HttpClient(new SocketsHttpHandler
{
    MaxConnectionsPerServer = 256,
    PooledConnectionLifetime = TimeSpan.FromSeconds(options.ConnectionLifetimeSeconds),
})
{
    BaseAddress = new Uri(options.Gateway),
    Timeout = TimeSpan.FromSeconds(70), // above the baseline's 60 s Recommendation Service budget
};
using var setupHttp = new HttpClient { BaseAddress = new Uri(options.SetupGateway ?? options.Gateway) };

Console.WriteLine($"Run {options.RunId}: {options.Devices} devices × {options.EventsPerDevice} events, " +
                  $"{options.Mode}, gateway {options.Gateway}");

// Register one clinician per phone through the admin API unless --shared-user is set.
if (options.SharedUsername is null)
{
    var admin = new GatewayClient(setupHttp, $"sim-{options.RunId}-admin", options.AdminUsername, options.AdminPassword);
    await admin.LoginAsync(CancellationToken.None);
    await Parallel.ForEachAsync(Enumerable.Range(1, options.Devices), new ParallelOptions { MaxDegreeOfParallelism = 8 },
        async (i, ct) => await admin.RegisterClinicianAsync(options.ClinicianUsername(i), options.ClinicianPassword, ct));
    Console.WriteLine($"Registered {options.Devices} clinicians (sim.{options.RunId.ToLowerInvariant()}.NNN)");
}

var devices = Enumerable.Range(1, options.Devices).Select(i =>
{
    var random = new Random(options.Seed + i);
    var id = options.DeviceId(i);
    var client = options.SharedUsername is { } shared
        ? new GatewayClient(http, id, shared, options.SharedPassword)
        : new GatewayClient(http, id, options.ClinicianUsername(i), options.ClinicianPassword);
    return new SimulatedDevice(id, client, new EventFactory(id, options.Facility, random, options.EditRate), options, clock, random);
}).ToList();

var started = clock.GetUtcNow();
using var timeout = new CancellationTokenSource(options.Timeout);
var runs = devices.Select(async d =>
{
    try { await d.RunAsync(timeout.Token); }
    catch (OperationCanceledException) { /* timed out: unfinished rows are reported as such */ }
    catch (Exception ex) { Console.Error.WriteLine($"{d.DeviceId}: {ex.Message}"); }
});
await Task.WhenAll(runs);
var finished = clock.GetUtcNow();

// ---- results -------------------------------------------------------------------------------------------------
var rows = devices.SelectMany(d => d.Queue.Snapshot().Select(r => (Device: d.DeviceId, Row: r))).ToList();
Directory.CreateDirectory(options.Output);
var csvPath = Path.Combine(options.Output, $"{options.RunId}-{options.Mode.ToString().ToLowerInvariant()}-events.csv");
var csv = new StringBuilder("run_id,mode,device_id,event_id,assessment_id,revision,status,attempts,enqueued_at,accepted_at,completed_at,accept_ms,complete_ms,last_error\n");
foreach (var (device, r) in rows)
{
    csv.AppendJoin(',', options.RunId, options.Mode, device, r.EventId, r.AssessmentId, r.Revision, r.Status, r.Attempts,
        Iso(r.EnqueuedAt), Iso(r.AcceptedAt), Iso(r.CompletedAt), Ms(r.AcceptedAt - r.EnqueuedAt), Ms(r.CompletedAt - r.EnqueuedAt),
        r.LastError?.Replace(',', ';') ?? "");
    csv.Append('\n');
}
await File.WriteAllTextAsync(csvPath, csv.ToString());

var accept = rows.Where(x => x.Row.AcceptedAt is not null).Select(x => (x.Row.AcceptedAt!.Value - x.Row.EnqueuedAt).TotalMilliseconds).Order().ToList();
var complete = rows.Where(x => x.Row.Status == RecordStatus.Complete).Select(x => (x.Row.CompletedAt!.Value - x.Row.EnqueuedAt).TotalMilliseconds).Order().ToList();
var summary = new
{
    runId = options.RunId,
    mode = options.Mode.ToString(),
    devices = options.Devices,
    eventsPerDevice = options.EventsPerDevice,
    startedAt = started,
    finishedAt = finished,
    seconds = Math.Round((finished - started).TotalSeconds, 1),
    events = rows.Count,
    byStatus = rows.GroupBy(x => x.Row.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
    pushes = devices.Sum(d => d.Pushes),
    pulls = devices.Sum(d => d.Pulls),
    resent = rows.Sum(x => Math.Max(0, x.Row.Attempts - 1)),
    acceptMs = Percentiles(accept),
    completeMs = Percentiles(complete),
};
var summaryPath = Path.ChangeExtension(csvPath.Replace("-events", "-summary"), ".json");
await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Wrote {csvPath}\n      {summaryPath}");
return rows.All(x => x.Row.IsFinal) ? 0 : 2; // 2: some events did not finish before --timeout-s

static string Iso(DateTimeOffset? t) => t?.ToString("O", CultureInfo.InvariantCulture) ?? "";
static string Ms(TimeSpan? d) => d is { } v ? Math.Round(v.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) : "";

static object Percentiles(List<double> sorted) => sorted.Count == 0
    ? new { count = 0 }
    : new
    {
        count = sorted.Count,
        p50 = Math.Round(At(sorted, 0.50)),
        p95 = Math.Round(At(sorted, 0.95)),
        p99 = Math.Round(At(sorted, 0.99)),
        max = Math.Round(sorted[^1]),
    };

static double At(List<double> sorted, double q) => sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(q * sorted.Count) - 1)];
