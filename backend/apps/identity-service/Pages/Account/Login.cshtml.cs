using System.Text.Json;
using IdentityService.Auth;
using IdentityService.Oidc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace IdentityService.Pages.Account;

/// <summary>
/// The only place a clinician types a password (ADR 0006). The rules and audit are the same as before: lockout, TOTP,
/// the admin-only dashboard and device binding. The reason code goes in data-code for tests; the message stays plain.
/// </summary>
public sealed class LoginModel(CredentialValidator credentials, OidcClients clients, IDataProtectionProvider protection)
    : PageModel
{
    private static readonly TimeSpan MfaStepLifetime = TimeSpan.FromMinutes(5);

    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? Totp { get; set; }

    /// <summary>The password step's result, sealed for the TOTP step so the password is never sent twice.</summary>
    [BindProperty] public string? MfaTicket { get; set; }

    public bool AskForTotp => MfaTicket is not null;
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string ClientName { get; private set; } = "";

    private sealed record Ticket(string Username, string PasswordHash);

    private ITimeLimitedDataProtector Protector =>
        protection.CreateProtector("IdentityService.Login.MfaTicket").ToTimeLimitedDataProtector();

    public IActionResult OnGet() => Target() is null ? BadRequest() : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Target() is not { } target) return BadRequest();

        SignInAttempt attempt;
        if (MfaTicket is not null)
        {
            if (Unseal(MfaTicket) is not { } ticket) return Restart("SESSION_EXPIRED");
            attempt = new SignInAttempt(ticket.Username, null, Totp?.Trim(), target.Client.Id, target.DeviceId, ticket.PasswordHash);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password)) return Restart("MISSING_FIELDS");
            attempt = new SignInAttempt(Username.Trim(), Password, null, target.Client.Id, target.DeviceId);
        }

        var result = await credentials.ValidateAsync(attempt, ct);
        Password = null;
        if (result.Clinician is { } clinician)
        {
            await LoginCookie.SignInAsync(HttpContext, clinician);
            return LocalRedirect(ReturnUrl!);
        }

        switch (result.ReasonCode)
        {
            case "MFA_REQUIRED":
                MfaTicket = Seal(new Ticket(attempt.Username, result.PasswordHash!));
                return Page();
            case "INVALID_TOTP" when MfaTicket is not null:
                return Show("INVALID_TOTP"); // keep the ticket: the clinician may retry the code
            default:
                return Restart(result.ReasonCode!);
        }
    }

    /// <summary>The authorize request this sign-in is for: only our own /connect/authorize, for a known client.</summary>
    private (OidcClient Client, string? DeviceId)? Target()
    {
        if (string.IsNullOrEmpty(ReturnUrl) || !Url.IsLocalUrl(ReturnUrl) ||
            !ReturnUrl.StartsWith("/connect/authorize?", StringComparison.Ordinal))
            return null;
        var query = QueryHelpers.ParseQuery(new Uri("http://local" + ReturnUrl).Query);
        if (clients.Find(query.TryGetValue("client_id", out var id) ? id.ToString() : null) is not { } client) return null;
        ClientName = client.Id == Clients.AdminDashboard ? "Admin dashboard" : "Mobile app";
        var device = query.TryGetValue(OidcValidationHandlers.DeviceIdParameter, out var d) ? d.ToString() : null;
        return (client, string.IsNullOrWhiteSpace(device) ? null : device);
    }

    private IActionResult Restart(string code)
    {
        MfaTicket = null;
        return Show(code);
    }

    private IActionResult Show(string code)
    {
        ErrorCode = code;
        ErrorMessage = code switch
        {
            "INVALID_CREDENTIALS" or "INVALID_TOTP" when MfaTicket is null => "Wrong username or password.",
            "INVALID_TOTP" => "That code is not valid. Enter the current code from your authenticator app.",
            "CREDENTIAL_LOCKED" => "Too many failed attempts. The account is locked for 15 minutes, or until an admin unlocks it.",
            "DEVICE_NOT_ALLOWED" => "This device is not allowed to sign in. Ask your facility admin.",
            "CLIENT_NOT_ALLOWED" => "Only facility admins can sign in to the admin dashboard.",
            "SESSION_EXPIRED" => "The sign-in took too long. Please start again.",
            "MISSING_FIELDS" => "Enter your username and password.",
            _ => "Sign-in failed. Please try again.",
        };
        return Page();
    }

    private string Seal(Ticket ticket) => Protector.Protect(JsonSerializer.Serialize(ticket), MfaStepLifetime);

    private Ticket? Unseal(string sealedTicket)
    {
        try
        {
            return JsonSerializer.Deserialize<Ticket>(Protector.Unprotect(sealedTicket));
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }
}
