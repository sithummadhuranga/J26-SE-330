using System.Reflection;
using System.Security.Claims;
using IdentityService.Account;
using IdentityService.Auth;
using IdentityService.Oidc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityService.Tests;

// The sign-in rules are faked here; the end-to-end suites test them for real.
public class AccountControllerTests
{
    private const string MobileReturnUrl =
        "/connect/authorize?client_id=mobile&response_type=code&code_challenge=abc&device_id=dev-a41c";

    private const string DashboardReturnUrl = "/connect/authorize?client_id=admin-dashboard&response_type=code";

    private static readonly OidcClients Clients = new(
        new OidcOptions { DashboardRedirectUris = ["http://localhost:3001/callback"] }, new JwtOptions());

    private static readonly VerifiedClinician Nurse =
        new(Guid.NewGuid(), "n.silva", "nurse", "fac-001", IdentityService.Auth.Clients.Mobile, "dev-a41c");

    private readonly FakeRules _rules = new();
    private readonly FakeAuthentication _auth = new();
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private readonly AccountController _controller;

    public AccountControllerTests()
    {
        var services = new ServiceCollection().AddSingleton<IAuthenticationService>(_auth).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        _controller = new AccountController(_rules, Clients, _protection)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NoTempData()),
        };
    }

    private Task<IActionResult> Post(LoginForm form, string returnUrl = MobileReturnUrl) =>
        _controller.Login(returnUrl, form, CancellationToken.None);

    private static LoginViewModel Page(IActionResult result)
    {
        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Login", view.ViewName);
        return Assert.IsType<LoginViewModel>(view.Model);
    }

    private async Task<string> MfaTicket()
    {
        _rules.Results.Enqueue(new CredentialResult(null, "MFA_REQUIRED", "hash-1"));
        return Page(await Post(new LoginForm { Username = "n.silva", Password = "secret-pw" })).MfaTicket!;
    }

    private async Task<string> ChangeTicket()
    {
        _rules.Results.Enqueue(new CredentialResult(null, "PASSWORD_CHANGE_REQUIRED", "hash-1"));
        return Page(await Post(new LoginForm { Username = "n.silva", Password = "admin-set" })).ChangeTicket!;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example/connect/authorize?client_id=mobile")]
    [InlineData("//evil.example/connect/authorize?client_id=mobile")]
    [InlineData("/v1/admin/clinicians")]
    [InlineData("/connect/authorize?client_id=someone-else")]
    public void The_page_only_opens_for_our_own_authorize_request_and_a_known_client(string? returnUrl)
    {
        Assert.IsType<BadRequestResult>(_controller.Login(returnUrl));
    }

    [Fact]
    public void The_page_names_the_app_the_clinician_is_signing_in_to()
    {
        Assert.Equal("Mobile app", Page(_controller.Login(MobileReturnUrl)).ClientName);
        Assert.Equal("Admin dashboard", Page(_controller.Login(DashboardReturnUrl)).ClientName);
    }

    [Fact]
    public async Task A_post_with_a_foreign_return_address_is_refused_before_any_password_is_checked()
    {
        var result = await Post(new LoginForm { Username = "n.silva", Password = "secret-pw" }, "https://evil.example/");
        Assert.IsType<BadRequestResult>(result);
        Assert.Empty(_rules.Attempts);
    }

    [Fact]
    public async Task A_missing_username_or_password_is_asked_for_again_without_checking_anything()
    {
        var page = Page(await Post(new LoginForm { Username = "n.silva" }));
        Assert.Equal("MISSING_FIELDS", page.ErrorCode);
        Assert.Equal("Enter your username and password.", page.ErrorMessage);
        Assert.Equal("n.silva", page.Username);
        Assert.Empty(_rules.Attempts);
    }

    [Fact]
    public async Task A_correct_password_signs_in_and_goes_back_to_the_authorize_request()
    {
        _rules.Results.Enqueue(new CredentialResult(Nurse, null));
        var result = await Post(new LoginForm { Username = "  n.silva ", Password = "secret-pw" });

        Assert.Equal(MobileReturnUrl, Assert.IsType<LocalRedirectResult>(result).Url);
        var attempt = Assert.Single(_rules.Attempts);
        Assert.Equal(("n.silva", "secret-pw"), (attempt.Username, attempt.Password));
        Assert.Equal(("mobile", "dev-a41c"), (attempt.Client, attempt.DeviceId));
        Assert.True(attempt.EnforcePasswordChange);
        Assert.Equal(LoginCookie.Scheme, _auth.Scheme);
        Assert.Equal(Nurse.ClinicianId.ToString(), _auth.Principal!.FindFirstValue("sub"));
    }

    [Theory]
    [InlineData("INVALID_CREDENTIALS", "Wrong username or password.")]
    [InlineData("CREDENTIAL_LOCKED", "Too many failed attempts. The account is locked for 15 minutes, or until an admin unlocks it.")]
    [InlineData("DEVICE_NOT_ALLOWED", "This device is not allowed to sign in. Ask your facility admin.")]
    [InlineData("CLIENT_NOT_ALLOWED", "Only facility admins can sign in to the admin dashboard.")]
    public async Task A_refused_sign_in_shows_its_reason_and_keeps_the_username(string code, string message)
    {
        _rules.Results.Enqueue(new CredentialResult(null, code));
        var page = Page(await Post(new LoginForm { Username = "n.silva", Password = "wrong" }));
        Assert.Equal((code, message), (page.ErrorCode, page.ErrorMessage));
        Assert.Equal("n.silva", page.Username);
        Assert.False(page.AskForTotp);
        Assert.Null(_auth.Principal);
    }

    [Fact]
    public async Task With_MFA_on_the_password_step_leads_to_the_code_step_and_the_code_finishes_the_sign_in()
    {
        var ticket = await MfaTicket();
        _rules.Results.Enqueue(new CredentialResult(Nurse, null));
        var result = await Post(new LoginForm { MfaTicket = ticket, Totp = " 123456 " });

        Assert.IsType<LocalRedirectResult>(result);
        var attempt = _rules.Attempts[1];
        Assert.Equal(("n.silva", null, "123456", "hash-1"),
            (attempt.Username, attempt.Password, attempt.Totp, attempt.VerifiedPasswordHash));
    }

    [Fact]
    public async Task A_wrong_code_keeps_the_code_step_so_the_clinician_can_try_again()
    {
        var ticket = await MfaTicket();
        _rules.Results.Enqueue(new CredentialResult(null, "INVALID_TOTP"));
        var page = Page(await Post(new LoginForm { MfaTicket = ticket, Totp = "000000" }));

        Assert.True(page.AskForTotp);
        Assert.Equal(ticket, page.MfaTicket);
        Assert.Equal("That code is not valid. Enter the current code from your authenticator app.", page.ErrorMessage);
    }

    [Fact]
    public async Task A_tampered_ticket_starts_the_sign_in_again()
    {
        var page = Page(await Post(new LoginForm { MfaTicket = "not-a-real-ticket", Totp = "123456" }));
        Assert.Equal("SESSION_EXPIRED", page.ErrorCode);
        Assert.False(page.AskForTotp);
        Assert.Empty(_rules.Attempts);
    }

    [Fact]
    public async Task A_ticket_cannot_be_moved_to_a_different_authorize_request_between_steps()
    {
        var ticket = await MfaTicket();
        var page = Page(await Post(new LoginForm { MfaTicket = ticket, Totp = "123456" }, DashboardReturnUrl));

        Assert.Equal("SIGN_IN_CHANGED", page.ErrorCode);
        Assert.Single(_rules.Attempts); // only the password step; the swapped request was never checked
    }

    [Fact]
    public async Task An_MFA_ticket_cannot_be_used_to_skip_to_the_new_password_step()
    {
        var ticket = await MfaTicket();
        var page = Page(await Post(new LoginForm { ChangeTicket = ticket, NewPassword = "new-pass-1", ConfirmPassword = "new-pass-1" }));
        Assert.Equal("SESSION_EXPIRED", page.ErrorCode);
        Assert.Empty(_rules.Changes);
    }

    [Fact]
    public async Task A_password_an_admin_set_must_be_replaced_and_then_signs_in_without_asking_again()
    {
        var ticket = await ChangeTicket();
        _rules.Results.Enqueue(new CredentialResult(Nurse, null));
        var result = await Post(new LoginForm { ChangeTicket = ticket, NewPassword = "my-own-pw", ConfirmPassword = "my-own-pw" });

        Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal(("n.silva", "hash-1", "my-own-pw"), Assert.Single(_rules.Changes));
        var attempt = _rules.Attempts[1];
        Assert.Equal("new-hash", attempt.VerifiedPasswordHash);
        Assert.True(attempt.MfaAlreadyVerified);
    }

    [Fact]
    public async Task New_passwords_that_do_not_match_are_refused_and_the_step_is_kept()
    {
        var ticket = await ChangeTicket();
        var page = Page(await Post(new LoginForm { ChangeTicket = ticket, NewPassword = "my-own-pw", ConfirmPassword = "my-own-px" }));

        Assert.Equal("PASSWORD_MISMATCH", page.ErrorCode);
        Assert.Equal(ticket, page.ChangeTicket);
        Assert.Empty(_rules.Changes);
    }

    [Theory]
    [InlineData("PASSWORD_TOO_SHORT", "The new password must be at least 8 characters.")]
    [InlineData("PASSWORD_UNCHANGED", "Choose a password different from the one your admin gave you.")]
    public async Task A_new_password_the_rules_refuse_keeps_the_step_with_the_reason(string code, string message)
    {
        var ticket = await ChangeTicket();
        _rules.ChangeError = code;
        var page = Page(await Post(new LoginForm { ChangeTicket = ticket, NewPassword = "short", ConfirmPassword = "short" }));

        Assert.Equal((code, message), (page.ErrorCode, page.ErrorMessage));
        Assert.True(page.AskForNewPassword);
    }

    [Theory]
    [InlineData(null, "secret-pw")]
    [InlineData("", "secret-pw")]
    [InlineData("   ", "secret-pw")]
    [InlineData("n.silva", null)]
    [InlineData("n.silva", "")]
    public async Task A_blank_username_or_empty_password_is_asked_for_again(string? username, string? password)
    {
        var page = Page(await Post(new LoginForm { Username = username, Password = password }));
        Assert.Equal("MISSING_FIELDS", page.ErrorCode);
        Assert.Empty(_rules.Attempts);
    }

    [Fact]
    public async Task The_password_is_checked_exactly_as_typed_and_only_the_username_is_trimmed()
    {
        _rules.Results.Enqueue(new CredentialResult(null, "INVALID_CREDENTIALS"));
        await Post(new LoginForm { Username = " n.silva ", Password = " pass word " });
        Assert.Equal(("n.silva", " pass word "), (_rules.Attempts[0].Username, _rules.Attempts[0].Password));
    }

    [Fact]
    public async Task A_dashboard_sign_in_sends_no_device()
    {
        _rules.Results.Enqueue(new CredentialResult(Nurse, null));
        await Post(new LoginForm { Username = "admin", Password = "secret-pw" }, DashboardReturnUrl);
        Assert.Equal(("admin-dashboard", null), (_rules.Attempts[0].Client, _rules.Attempts[0].DeviceId));
    }

    [Fact]
    public async Task An_unknown_reason_from_the_rules_shows_a_plain_message()
    {
        _rules.Results.Enqueue(new CredentialResult(null, "SOMETHING_INTERNAL"));
        var page = Page(await Post(new LoginForm { Username = "n.silva", Password = "secret-pw" }));
        Assert.Equal("Sign-in failed. Please try again.", page.ErrorMessage);
    }

    [Fact]
    public async Task A_wrong_code_reason_on_the_password_step_does_not_open_the_code_step()
    {
        _rules.Results.Enqueue(new CredentialResult(null, "INVALID_TOTP"));
        var page = Page(await Post(new LoginForm { Username = "n.silva", Password = "secret-pw" }));
        Assert.False(page.AskForTotp);
        Assert.Equal("Wrong username or password.", page.ErrorMessage);
    }

    [Fact]
    public async Task The_code_step_asks_only_for_the_code()
    {
        _rules.Results.Enqueue(new CredentialResult(null, "MFA_REQUIRED", "hash-1"));
        var page = Page(await Post(new LoginForm { Username = "n.silva", Password = "secret-pw" }));
        Assert.True(page.AskForTotp);
        Assert.Null(page.Username);
        Assert.Null(page.ErrorCode);
    }

    [Fact]
    public async Task Tickets_are_encrypted_so_the_page_never_shows_the_username_or_password_hash()
    {
        var ticket = await MfaTicket();
        Assert.DoesNotContain("n.silva", ticket);
        Assert.DoesNotContain("hash-1", ticket);
    }

    [Fact]
    public async Task An_expired_ticket_starts_the_sign_in_again()
    {
        var expired = _protection.CreateProtector("IdentityService.Login.MfaTicket").ToTimeLimitedDataProtector()
            .Protect($$"""{"Username":"n.silva","PasswordHash":"hash-1","ReturnUrl":"{{MobileReturnUrl}}"}""",
                DateTimeOffset.UtcNow.AddMinutes(-1));
        var page = Page(await Post(new LoginForm { MfaTicket = expired, Totp = "123456" }));
        Assert.Equal("SESSION_EXPIRED", page.ErrorCode);
        Assert.Empty(_rules.Attempts);
    }

    [Fact]
    public async Task Tickets_from_the_page_run_out_after_five_minutes()
    {
        var mfa = await MfaTicket();
        var change = await ChangeTicket();

        _protection.CreateProtector("IdentityService.Login.MfaTicket").ToTimeLimitedDataProtector()
            .Unprotect(mfa, out var mfaExpiry);
        _protection.CreateProtector("IdentityService.Login.ChangeTicket").ToTimeLimitedDataProtector()
            .Unprotect(change, out var changeExpiry);

        foreach (var expiry in new[] { mfaExpiry, changeExpiry })
            Assert.InRange(expiry - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task A_freshly_sealed_ticket_in_the_same_format_is_accepted()
    {
        // Guards the expired-ticket test: it only proves something if this same ticket works when it is in date.
        var fresh = _protection.CreateProtector("IdentityService.Login.MfaTicket").ToTimeLimitedDataProtector()
            .Protect($$"""{"Username":"n.silva","PasswordHash":"hash-1","ReturnUrl":"{{MobileReturnUrl}}"}""",
                DateTimeOffset.UtcNow.AddMinutes(1));
        _rules.Results.Enqueue(new CredentialResult(Nurse, null));
        Assert.IsType<LocalRedirectResult>(await Post(new LoginForm { MfaTicket = fresh, Totp = "123456" }));
    }

    [Fact]
    public async Task Any_other_refusal_on_the_code_step_starts_again_without_the_username()
    {
        var ticket = await MfaTicket();
        _rules.Results.Enqueue(new CredentialResult(null, "CREDENTIAL_LOCKED"));
        var page = Page(await Post(new LoginForm { MfaTicket = ticket, Totp = "123456" }));

        Assert.Equal("CREDENTIAL_LOCKED", page.ErrorCode);
        Assert.False(page.AskForTotp);
        Assert.Null(page.Username);
        Assert.Null(_auth.Principal);
    }

    [Fact]
    public async Task After_the_code_an_admin_set_password_still_has_to_be_replaced_without_asking_for_the_code_again()
    {
        var mfa = await MfaTicket();
        _rules.Results.Enqueue(new CredentialResult(null, "PASSWORD_CHANGE_REQUIRED", "hash-1"));
        var page = Page(await Post(new LoginForm { MfaTicket = mfa, Totp = "123456" }));
        Assert.True(page.AskForNewPassword);
        Assert.False(page.AskForTotp);

        _rules.Results.Enqueue(new CredentialResult(Nurse, null));
        var result = await Post(new LoginForm { ChangeTicket = page.ChangeTicket, NewPassword = "my-own-pw", ConfirmPassword = "my-own-pw" });
        Assert.IsType<LocalRedirectResult>(result);
        Assert.True(_rules.Attempts[2].MfaAlreadyVerified);
    }

    [Fact]
    public async Task A_change_ticket_cannot_be_moved_to_a_different_authorize_request()
    {
        var ticket = await ChangeTicket();
        var page = Page(await Post(new LoginForm { ChangeTicket = ticket, NewPassword = "my-own-pw", ConfirmPassword = "my-own-pw" },
            DashboardReturnUrl));
        Assert.Equal("SIGN_IN_CHANGED", page.ErrorCode);
        Assert.Empty(_rules.Changes);
    }

    [Fact]
    public async Task A_password_change_refused_for_another_reason_starts_again()
    {
        var ticket = await ChangeTicket();
        _rules.ChangeError = "SESSION_EXPIRED";
        var page = Page(await Post(new LoginForm { ChangeTicket = ticket, NewPassword = "my-own-pw", ConfirmPassword = "my-own-pw" }));

        Assert.Equal("SESSION_EXPIRED", page.ErrorCode);
        Assert.False(page.AskForNewPassword);
        Assert.Single(_rules.Attempts);
    }

    [Fact]
    public async Task A_refusal_after_the_new_password_starts_again_without_the_username()
    {
        var ticket = await ChangeTicket();
        _rules.Results.Enqueue(new CredentialResult(null, "DEVICE_NOT_ALLOWED"));
        var page = Page(await Post(new LoginForm { ChangeTicket = ticket, NewPassword = "my-own-pw", ConfirmPassword = "my-own-pw" }));

        Assert.Equal("DEVICE_NOT_ALLOWED", page.ErrorCode);
        Assert.False(page.AskForNewPassword);
        Assert.Null(page.Username);
        Assert.Null(_auth.Principal);
    }

    [Fact]
    public void The_signed_out_page_shows()
    {
        Assert.IsType<ViewResult>(_controller.SignedOut());
    }

    [Fact]
    public void The_form_post_checks_the_anti_forgery_token()
    {
        var post = typeof(AccountController).GetMethods()
            .Single(m => m.Name == nameof(AccountController.Login) && m.GetCustomAttribute<HttpPostAttribute>() is not null);
        Assert.NotNull(post.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    private sealed class FakeRules : ISignInRules
    {
        public Queue<CredentialResult> Results { get; } = new();
        public List<SignInAttempt> Attempts { get; } = [];
        public List<(string Username, string Hash, string? NewPassword)> Changes { get; } = [];
        public string? ChangeError { get; set; }

        public Task<CredentialResult> ValidateAsync(SignInAttempt attempt, CancellationToken ct)
        {
            Attempts.Add(attempt);
            return Task.FromResult(Results.Dequeue());
        }

        public Task<(string? NewHash, string? Error)> ChangePasswordAsync(string username, string expectedHash,
            string? newPassword, CancellationToken ct)
        {
            if (ChangeError is not null) return Task.FromResult<(string?, string?)>((null, ChangeError));
            Changes.Add((username, expectedHash, newPassword));
            return Task.FromResult<(string?, string?)>(("new-hash", null));
        }
    }

    // View() needs TempData even though the page doesn't use it.
    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class FakeAuthentication : IAuthenticationService
    {
        public string? Scheme { get; private set; }
        public ClaimsPrincipal? Principal { get; private set; }

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            (Scheme, Principal) = (scheme, principal);
            return Task.CompletedTask;
        }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
