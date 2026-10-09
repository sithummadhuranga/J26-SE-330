using IdentityService.Admin;
using IdentityService.Auth;
using IdentityService.Endpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Sync.Common.Auth;
using Sync.Common.Persistence;
using Sync.Common.Telemetry;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Sync.Common.Web;

// Identity service: login, MFA and admin; the only service that signs tokens.
var builder = WebApplication.CreateBuilder(args);
builder.AddSyncTelemetry("identity-service")
    .WithTracing(t => t.AddAspNetCoreInstrumentation())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation());

var dataSource = NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Username=cdss;Password=cdss;Database=cdss");

// `dotnet run -- create-clinician ...` registers a clinician and exits (bootstrap of a facility's first admin).
if (args.FirstOrDefault() == "create-clinician")
    return await CreateClinicianCommand.RunAsync(args, dataSource);

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
var signingKey = new SigningKey(jwt.SigningKeyPem);

builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton(signingKey);
builder.Services.AddSingleton(new SecretProtector(builder.Configuration["Secrets:EncryptionKey"] ?? ""));
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<MfaService>();
builder.Services.AddSingleton<ClinicianAdminService>();
builder.Services.AddSingleton<DeviceAdminService>();
builder.Services.AddHostedService<AuthHealthSampler>();

// Only reachable inside the Docker network, behind the API gateway, so forwarded headers are trusted as-is.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// MFA and admin endpoints need a signed-in clinician; this service checks its own bearer tokens.
builder.Services.AddInMemoryDataProtection();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudiences = [jwt.Audience, jwt.DashboardAudience],
            IssuerSigningKey = signingKey.PublicKey,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ClaimNames.Subject,
            RoleClaimType = ClaimNames.Role,
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AdminEndpoints.AdminPolicy, p => p.RequireRole("admin"));

var app = builder.Build();

if (signingKey.IsEphemeral)
    app.Logger.LogWarning("Jwt:SigningKeyPem is not set; using a key generated at start-up (local development only).");

if (!app.Configuration.GetValue<bool>("SkipSchemaCheck"))
    await SchemaVersionGuard.EnsureAsync(dataSource, ExpectedSchemaVersions.All);

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapDiscoveryEndpoints();

var v1 = app.MapGroup("/v1");
v1.MapAuthEndpoints();
v1.MapAdminEndpoints();

await app.RunAsync();
return 0;
