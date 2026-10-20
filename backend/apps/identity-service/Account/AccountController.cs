using System.Text.Json;
using IdentityService.Auth;
using IdentityService.Oidc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace IdentityService.Account;

/// <summary>
/// The sign-in page, as an MVC controller (ADR 0006). It's the only place a clinician types a password. The steps are
/// password, then the authenticator code if MFA is on, then a new password if an admin set the current one. The rules
/// and audit live in <see cref="ISignInRules"/>; this controller only moves the clinician between the steps.
/// </summary>
[Route("account")]
public sealed class AccountController(ISignInRules rules, OidcClients clients, IDataProtectionProvider protection)
    : Controller
{
    private static readonly TimeSpan StepLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// What one step hands to the next. It's sealed (encrypted and time-limited), and it carries the authorize request
    /// it belongs to, so the request can't be swapped between steps.
    /// </summary>
    private sealed record Ticket(string Username, string PasswordHash, string ReturnUrl);

    private ITimeLimitedDataProtector MfaProtector =>
        protection.CreateProtector("IdentityService.Login.MfaTicket").ToTimeLimitedDataProtector();

    // A different purpose from the MFA ticket, so an MFA ticket can never be used to skip the code.
    private ITimeLimitedDataProtector ChangeProtector =>
        protection.CreateProtector("IdentityService.Login.ChangeTicket").ToTimeLimitedDataProtector();

    [HttpGet("login")]
    public IActionResult Login([FromQuery] string? returnUrl) =>
        LoginTarget.Parse(returnUrl, clients) is { } target ? Form(target) : BadRequest();

    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login([FromQuery] string? returnUrl, [FromForm] LoginForm form, CancellationToken ct)
    {
        if (LoginTarget.Parse(returnUrl, clients) is not { } target) return BadRequest();

        SignInAttempt attempt;
        string? mfaTicket = null;
        var passwordStep = form.ChangeTicket is null && form.MfaTicket is null;
        if (form.ChangeTicket is not null)
        {
            if (Unseal(ChangeProtector, form.ChangeTicket) is not { } ticket) return Form(target, error: "SESSION_EXPIRED");
            if (ticket.ReturnUrl != target.ReturnUrl) return Form(target, error: "SIGN_IN_CHANGED");
            if (form.NewPassword != form.ConfirmPassword)
                return Form(target, changeTicket: form.ChangeTicket, error: "PASSWORD_MISMATCH");
            var (newHash, error) = await rules.ChangePasswordAsync(ticket.Username, ticket.PasswordHash, form.NewPassword, ct);
            if (error is "PASSWORD_TOO_SHORT" or "PASSWORD_UNCHANGED")
                return Form(target, changeTicket: form.ChangeTicket, error: error);
            if (error is not null) return Form(target, error: error);
            // The code was already checked before this step, so sign in with the new password without asking again.
            attempt = new SignInAttempt(ticket.Username, null, null, target.Client.Id, target.DeviceId, newHash,
                EnforcePasswordChange: true, MfaAlreadyVerified: true);
        }
        else if (form.MfaTicket is not null)
        {
            if (Unseal(MfaProtector, form.MfaTicket) is not { } ticket) return Form(target, error: "SESSION_EXPIRED");
            if (ticket.ReturnUrl != target.ReturnUrl) return Form(target, error: "SIGN_IN_CHANGED");
            mfaTicket = form.MfaTicket;
            attempt = new SignInAttempt(ticket.Username, null, form.Totp?.Trim(), target.Client.Id, target.DeviceId,
                ticket.PasswordHash, EnforcePasswordChange: true);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(form.Username) || string.IsNullOrEmpty(form.Password))
                return Form(target, username: form.Username, error: "MISSING_FIELDS");
            attempt = new SignInAttempt(form.Username.Trim(), form.Password, null, target.Client.Id, target.DeviceId,
                EnforcePasswordChange: true);
        }

        var result = await rules.ValidateAsync(attempt, ct);
        if (result.Clinician is { } clinician)
        {
            await LoginCookie.SignInAsync(HttpContext, clinician);
            return LocalRedirect(target.ReturnUrl);
        }

        return result.ReasonCode switch
        {
            "MFA_REQUIRED" => Form(target,
                mfaTicket: Seal(MfaProtector, new Ticket(attempt.Username, result.PasswordHash!, target.ReturnUrl))),
            "PASSWORD_CHANGE_REQUIRED" => Form(target,
                changeTicket: Seal(ChangeProtector, new Ticket(attempt.Username, result.PasswordHash!, target.ReturnUrl))),
            // Keep the ticket after a wrong code, so the clinician can try the code again.
            "INVALID_TOTP" when mfaTicket is not null => Form(target, mfaTicket: mfaTicket, error: "INVALID_TOTP"),
            // Anything else starts again. Only the password step puts the username back in the box.
            _ => Form(target, username: passwordStep ? attempt.Username : null, error: result.ReasonCode!),
        };
    }

    [HttpGet("signed-out")]
    public IActionResult SignedOut() => View();

    private ViewResult Form(LoginTarget target, string? username = null, string? mfaTicket = null,
        string? changeTicket = null, string? error = null) =>
        View("Login", new LoginViewModel
        {
            ClientName = target.ClientName,
            MinPasswordLength = Admin.ClinicianAdminService.MinPasswordLength,
            Username = username,
            MfaTicket = mfaTicket,
            ChangeTicket = changeTicket,
            ErrorCode = error,
            ErrorMessage = error is null ? null : LoginMessages.For(error, onCodeStep: mfaTicket is not null),
        });

    private static string Seal(ITimeLimitedDataProtector protector, Ticket ticket) =>
        protector.Protect(JsonSerializer.Serialize(ticket), StepLifetime);

    private static Ticket? Unseal(ITimeLimitedDataProtector protector, string sealedTicket)
    {
        try
        {
            return JsonSerializer.Deserialize<Ticket>(protector.Unprotect(sealedTicket));
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }
}
