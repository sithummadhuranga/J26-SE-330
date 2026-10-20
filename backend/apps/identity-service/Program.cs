using IdentityService.Admin;
using IdentityService.Auth;
using IdentityService.Endpoints;
using IdentityService.Oidc;
using IdentityService.Profile;
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

// The identity service. It's the OpenID Connect provider (ADR 0006) and also handles MFA and admin.
// It's the only service that signs tokens.
var builder = WebApplication.CreateBuilder(args);
builder.AddSyncTelemetry("identity-service")
    .WithTracing(t => t.AddAspNetCoreInstrumentation())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation());

var dataSource = NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Username=cdss;Password=cdss;Database=cdss");

// `dotnet run -- create-clinician ...` registers a clinician and exits. That's how a facility gets its first admin.
if (args.FirstOrDefault() == "create-clinician")
    return await CreateClinicianCommand.RunAsync(args, dataSource);

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
var signingKey = new SigningKey(jwt.SigningKeyPem);
var secrets = new SecretProtector(builder.Configuration["Secrets:EncryptionKey"] ?? "");
var oidc = builder.Configuration.GetSection("Oidc").Get<OidcOptions>() ?? new OidcOptions();

builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton(signingKey);
builder.Services.AddSingleton(secrets);
builder.Services.AddSingleton(oidc);
builder.Services.AddSingleton<OidcClients>();
builder.Services.AddSingleton<CodeRedemptions>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<CredentialValidator>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<MfaService>();
builder.Services.AddSingleton<ClinicianAdminService>();
builder.Services.AddSingleton<DeviceAdminService>();
builder.Services.AddSingleton<ProfileService>();
builder.Services.AddHostedService<AuthHealthSampler>();

// This is only reachable inside the Docker network, behind the API gateway, so we trust forwarded headers as they are.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// The MFA and admin endpoints need a signed-in clinician, and this service checks its own bearer tokens.
// In-memory keys are fine here because they only protect short-lived things (the 5-minute login cookie, the MFA step
// and anti-forgery), so a restart just means signing in again. Codes and refresh tokens use a key from
// Secrets:EncryptionKey.
builder.Services.AddInMemoryDataProtection();
builder.Services.AddRazorPages();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddLoginCookie(oidc.RequireHttps)
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
builder.Services.AddOidcServer(jwt, oidc, signingKey, secrets);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AdminEndpoints.AdminPolicy, p => p.RequireRole("admin"));

var app = builder.Build();

if (oidc.AllowPasswordGrant)
    app.Logger.LogWarning("Oidc:AllowPasswordGrant is on: the mobile client accepts passwords directly (simulator and tests only).");
if (signingKey.IsEphemeral)
    app.Logger.LogWarning("Jwt:SigningKeyPem is not set; using a key generated at start-up (local development only).");

if (!app.Configuration.GetValue<bool>("SkipSchemaCheck"))
    await SchemaVersionGuard.EnsureAsync(dataSource, ExpectedSchemaVersions.All);

app.UseForwardedHeaders();
// Just the login page's font (wwwroot/account/fonts), so the page matches the app without needing a font service.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "public, max-age=604800",
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapConnectEndpoints();
app.MapRazorPages();

var v1 = app.MapGroup("/v1");
v1.MapAuthEndpoints();
v1.MapAdminEndpoints();
v1.MapMeEndpoints();

await app.RunAsync();
return 0;
