using IdentityService.Auth;
using IdentityService.Oidc;
using Microsoft.AspNetCore.WebUtilities;

namespace IdentityService.Account;

/// <summary>What the sign-in form posts. Which fields are filled in depends on the step the clinician is on.</summary>
public sealed class LoginForm
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Totp { get; set; }

    /// <summary>The sealed result of the password step, carried into the authenticator-code step.</summary>
    public string? MfaTicket { get; set; }

    /// <summary>The sealed result of a sign-in whose password an admin set, carried into the new-password step.</summary>
    public string? ChangeTicket { get; set; }

    public string? NewPassword { get; set; }
    public string? ConfirmPassword { get; set; }
}

/// <summary>What the sign-in view shows. Passwords are never put back into the page.</summary>
public sealed class LoginViewModel
{
    public required string ClientName { get; init; }
    public required int MinPasswordLength { get; init; }
    public string? Username { get; init; }
    public string? MfaTicket { get; init; }
    public string? ChangeTicket { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public bool AskForTotp => MfaTicket is not null;
    public bool AskForNewPassword => ChangeTicket is not null;
}

/// <summary>
/// The authorize request a sign-in belongs to. It comes only from the page's query string, never from the form,
/// and it has to be our own /connect/authorize for a known client, so the sign-in can never redirect anywhere else.
/// </summary>
public sealed record LoginTarget(string ReturnUrl, OidcClient Client, string? DeviceId)
{
    private const string AuthorizePrefix = "/connect/authorize?";

    public string ClientName => Client.Id == Clients.AdminDashboard ? "Admin dashboard" : "Mobile app";

    /// <summary>Null unless <paramref name="returnUrl"/> is our own authorize request for a client we know.</summary>
    public static LoginTarget? Parse(string? returnUrl, OidcClients clients)
    {
        // Starting with "/connect/authorize?" also rules out "//host" and "/\host", which browsers treat as other sites.
        if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith(AuthorizePrefix, StringComparison.Ordinal))
            return null;
        var query = QueryHelpers.ParseQuery(returnUrl[(AuthorizePrefix.Length - 1)..]);
        if (clients.Find(query.TryGetValue("client_id", out var id) ? id.ToString() : null) is not { } client) return null;
        var device = query.TryGetValue(OidcValidationHandlers.DeviceIdParameter, out var d) ? d.ToString() : null;
        return new LoginTarget(returnUrl, client, string.IsNullOrWhiteSpace(device) ? null : device);
    }
}

/// <summary>What the clinician reads for each reason code. The code itself goes in data-code for the tests.</summary>
public static class LoginMessages
{
    public static string For(string code, bool onCodeStep) => code switch
    {
        "INVALID_CREDENTIALS" or "INVALID_TOTP" when !onCodeStep => "Wrong username or password.",
        "INVALID_TOTP" => "That code is not valid. Enter the current code from your authenticator app.",
        "CREDENTIAL_LOCKED" => "Too many failed attempts. The account is locked for 15 minutes, or until an admin unlocks it.",
        "DEVICE_NOT_ALLOWED" => "This device is not allowed to sign in. Ask your facility admin.",
        "CLIENT_NOT_ALLOWED" => "Only facility admins can sign in to the admin dashboard.",
        "SESSION_EXPIRED" => "The sign-in took too long. Please start again.",
        "SIGN_IN_CHANGED" => "This sign-in changed partway through. Please start again.",
        "MISSING_FIELDS" => "Enter your username and password.",
        "PASSWORD_MISMATCH" => "The two new passwords do not match.",
        "PASSWORD_TOO_SHORT" =>
            $"The new password must be at least {Admin.ClinicianAdminService.MinPasswordLength} characters.",
        "PASSWORD_UNCHANGED" => "Choose a password different from the one your admin gave you.",
        _ => "Sign-in failed. Please try again.",
    };
}
