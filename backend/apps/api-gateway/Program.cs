using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Sync.Common.Auth;
using Sync.Common.Telemetry;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Sync.Common.Web;

// API gateway: the one public entry point; routes requests and handles JWT, rate limits, CORS and body size.
var builder = WebApplication.CreateBuilder(args);
builder.AddSyncTelemetry("api-gateway")
    .WithTracing(t => t.AddAspNetCoreInstrumentation())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation());

var jwt = builder.Configuration.GetSection("Jwt");
var limits = builder.Configuration.GetSection("RateLimits");

builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Reject bad tokens at the edge too (services still check them); keys come from the identity service's cached JWKS.
builder.Services.AddInMemoryDataProtection();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MetadataAddress = jwt["MetadataAddress"] ?? "http://localhost:8085/.well-known/openid-configuration";
        o.RequireHttpsMetadata = jwt.GetValue("RequireHttpsMetadata", false);
        o.RefreshInterval = TimeSpan.FromSeconds(30);
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt["Issuer"] ?? "melanin-wound-cdss",
            // Device and admin-dashboard tokens; the Sync Gateway itself accepts only the device audience.
            ValidAudiences = jwt.GetSection("Audiences").Get<string[]>()
                ?? ["melanin-wound-cdss-devices", "melanin-wound-cdss-admin"],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ClaimNames.Subject,
            RoleClaimType = ClaimNames.Role,
        };
    });
builder.Services.AddAuthorization();

// Only the admin dashboard calls from a browser.
builder.Services.AddCors(o => o.AddPolicy("dashboard", p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()));

// Rate limits: "auth" per client IP, "api" per signed-in device; rejected requests get 429 with Retry-After.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    };

    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => PerMinute(limits.GetValue("AuthPerMinutePerIp", 120))));

    o.AddPolicy("api", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.Identity?.IsAuthenticated == true
            ? $"{http.User.FindFirst(ClaimNames.Subject)?.Value}/{http.User.FindFirst(ClaimNames.DeviceId)?.Value}"
            : http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => PerMinute(limits.GetValue("ApiPerMinutePerClient", 600))));
});

var app = builder.Build();

// Answer 413 early for oversized bodies (YARP would say 400); the device splits batches on 413.
var maxBody = app.Configuration.GetValue<long?>("Kestrel:Limits:MaxRequestBodySize");
app.Use((context, next) =>
{
    if (context.Request.ContentLength > maxBody)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return Task.CompletedTask;
    }
    return next(context);
});

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapReverseProxy();

app.Run();

static FixedWindowRateLimiterOptions PerMinute(int permits) => new()
{
    PermitLimit = permits,
    Window = TimeSpan.FromMinutes(1),
    QueueLimit = 0,
};
