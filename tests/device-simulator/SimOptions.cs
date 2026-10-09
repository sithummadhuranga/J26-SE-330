namespace DeviceSimulator;

public enum SimMode { EventDriven, Baseline }

/// <summary>Command-line options; each run's id tags its devices so metrics can pick out one run.</summary>
public sealed record SimOptions
{
    public string Gateway { get; init; } = "http://localhost:8080/";
    /// <summary>Where the admin registers clinicians; setup only, so it can skip the faulty network.</summary>
    public string? SetupGateway { get; init; }
    /// <summary>How long a device keeps a TCP connection; 0 means a new one per request.</summary>
    public int ConnectionLifetimeSeconds { get; init; } = 120;
    /// <summary>Starting pull cursor; 0 downloads the whole history, experiments pass the current head.</summary>
    public long StartCursor { get; init; }
    public int Devices { get; init; } = 10;
    public int EventsPerDevice { get; init; } = 20;
    public SimMode Mode { get; init; } = SimMode.EventDriven;
    public string RunId { get; init; } = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    public string Facility { get; init; } = "fac-001";
    /// <summary>Admin who registers one clinician per device (POST /v1/admin/clinicians), as in a real ward.</summary>
    public string AdminUsername { get; init; } = "admin.demo";
    public string AdminPassword { get; init; } = "Demo-Admin-2026!";
    /// <summary>When set, every device logs in as this one clinician instead (stress case: one account, many phones).</summary>
    public string? SharedUsername { get; init; }
    public string SharedPassword { get; init; } = "Demo-Pass-2026!";
    public TimeSpan CaptureInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public double EditRate { get; init; } = 0.1;
    public int Seed { get; init; } = 42;
    public string Output { get; init; } = "tests/device-simulator/results";

    /// <summary>Device ids look like sim-&lt;run&gt;-007: the run id is how SQL finds this run's rows.</summary>
    public string DeviceId(int index) => $"sim-{RunId}-{index:D3}";

    /// <summary>The clinician on device <paramref name="index"/>; lower-case to satisfy the username rule.</summary>
    public string ClinicianUsername(int index) => $"sim.{RunId}.{index:D3}".ToLowerInvariant();
    public string ClinicianPassword => $"Sim-{RunId}-Pass!";

    public static SimOptions Parse(string[] args)
    {
        var o = new SimOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            o = args[i] switch
            {
                "--gateway" => o with { Gateway = Value().TrimEnd('/') + "/" },
                "--setup-gateway" => o with { SetupGateway = Value().TrimEnd('/') + "/" },
                "--connection-lifetime-s" => o with { ConnectionLifetimeSeconds = int.Parse(Value()) },
                "--start-cursor" => o with { StartCursor = long.Parse(Value()) },
                "--devices" => o with { Devices = int.Parse(Value()) },
                "--events" => o with { EventsPerDevice = int.Parse(Value()) },
                "--mode" => o with { Mode = Value() == "baseline" ? SimMode.Baseline : SimMode.EventDriven },
                "--run-id" => o with { RunId = Value() },
                "--facility" => o with { Facility = Value() },
                "--admin-user" => o with { AdminUsername = Value() },
                "--admin-password" => o with { AdminPassword = Value() },
                "--shared-user" => o with { SharedUsername = Value() },
                "--shared-password" => o with { SharedPassword = Value() },
                "--capture-interval-ms" => o with { CaptureInterval = TimeSpan.FromMilliseconds(int.Parse(Value())) },
                "--poll-interval-ms" => o with { PollInterval = TimeSpan.FromMilliseconds(int.Parse(Value())) },
                "--timeout-s" => o with { Timeout = TimeSpan.FromSeconds(int.Parse(Value())) },
                "--edit-rate" => o with { EditRate = double.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture) },
                "--seed" => o with { Seed = int.Parse(Value()) },
                "--out" => o with { Output = Value() },
                _ => throw new ArgumentException($"Unknown option {args[i]}. See tests/device-simulator/README.md."),
            };
        }
        if (o.Devices is < 1 or > 100) throw new ArgumentException("--devices must be 1–100 (§13.1)");
        if (!System.Text.RegularExpressions.Regex.IsMatch(o.RunId, "^[A-Za-z0-9-]{1,30}$"))
            throw new ArgumentException("--run-id: letters, digits and '-' only (it becomes part of device ids and usernames)");
        return o;
    }
}
