using IdentityService.Account;
using IdentityService.Auth;
using IdentityService.Oidc;

namespace IdentityService.Tests;

/// <summary>
/// The models behind the MVC sign-in page: which return addresses the page accepts (LoginTarget), which step the
/// view shows (LoginViewModel) and what the clinician reads for each reason code (LoginMessages).
/// </summary>
public class LoginModelsTests
{
    private static readonly OidcClients Clients = new(
        new OidcOptions { DashboardRedirectUris = ["http://localhost:3001/callback"] }, new JwtOptions());

    [Fact]
    public void A_mobile_authorize_request_gives_the_mobile_client_and_its_device()
    {
        const string url = "/connect/authorize?client_id=mobile&response_type=code&device_id=dev-a41c";

        var target = LoginTarget.Parse(url, Clients);

        Assert.NotNull(target);
        Assert.Equal(url, target.ReturnUrl);
        Assert.Equal("mobile", target.Client.Id);
        Assert.Equal("dev-a41c", target.DeviceId);
        Assert.Equal("Mobile app", target.ClientName);
    }

    [Fact]
    public void A_dashboard_authorize_request_has_no_device_and_names_the_dashboard()
    {
        var target = LoginTarget.Parse("/connect/authorize?client_id=admin-dashboard&response_type=code", Clients);

        Assert.NotNull(target);
        Assert.Equal("admin-dashboard", target.Client.Id);
        Assert.Null(target.DeviceId);
        Assert.Equal("Admin dashboard", target.ClientName);
    }

    [Theory]
    [InlineData("/connect/authorize?client_id=mobile&device_id=")]
    [InlineData("/connect/authorize?client_id=mobile&device_id=%20%20")]
    [InlineData("/connect/authorize?client_id=mobile")]
    public void A_blank_or_missing_device_id_counts_as_no_device(string url)
    {
        Assert.Null(LoginTarget.Parse(url, Clients)!.DeviceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://evil.example/connect/authorize?client_id=mobile")]
    [InlineData("//evil.example/connect/authorize?client_id=mobile")]
    [InlineData("/\\evil.example/connect/authorize?client_id=mobile")]
    [InlineData("/connect/authorize")]
    [InlineData("/connect/authorizex?client_id=mobile")]
    [InlineData("/CONNECT/AUTHORIZE?client_id=mobile")]
    [InlineData("/connect/token?client_id=mobile")]
    [InlineData("/account/login?client_id=mobile")]
    [InlineData("/connect/authorize?client_id=someone-else")]
    [InlineData("/connect/authorize?response_type=code")]
    public void Anything_but_our_own_authorize_request_for_a_known_client_is_refused(string? url)
    {
        Assert.Null(LoginTarget.Parse(url, Clients));
    }

    [Fact]
    public void The_view_shows_the_password_step_when_there_is_no_ticket()
    {
        var page = new LoginViewModel { ClientName = "Mobile app", MinPasswordLength = 8 };

        Assert.False(page.AskForTotp);
        Assert.False(page.AskForNewPassword);
    }

    [Fact]
    public void The_view_shows_the_code_step_when_it_has_an_MFA_ticket()
    {
        var page = new LoginViewModel { ClientName = "Mobile app", MinPasswordLength = 8, MfaTicket = "t" };

        Assert.True(page.AskForTotp);
        Assert.False(page.AskForNewPassword);
    }

    [Fact]
    public void The_view_shows_the_new_password_step_when_it_has_a_change_ticket()
    {
        var page = new LoginViewModel { ClientName = "Mobile app", MinPasswordLength = 8, ChangeTicket = "t" };

        Assert.True(page.AskForNewPassword);
        Assert.False(page.AskForTotp);
    }

    [Fact]
    public void The_form_has_no_return_address_field_so_it_can_only_come_from_the_query_string()
    {
        Assert.DoesNotContain(typeof(LoginForm).GetProperties(),
            p => p.Name.Contains("Return", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_view_model_never_carries_a_password()
    {
        Assert.DoesNotContain(typeof(LoginViewModel).GetProperties(),
            p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) && p.Name != "MinPasswordLength"
                 && p.Name != "AskForNewPassword");
    }

    [Fact]
    public void A_wrong_code_on_the_password_step_reads_as_a_wrong_password_so_it_does_not_reveal_MFA()
    {
        Assert.Equal("Wrong username or password.", LoginMessages.For("INVALID_TOTP", onCodeStep: false));
        Assert.Equal("Wrong username or password.", LoginMessages.For("INVALID_CREDENTIALS", onCodeStep: false));
    }

    [Fact]
    public void A_wrong_code_on_the_code_step_asks_for_the_current_code()
    {
        Assert.Equal("That code is not valid. Enter the current code from your authenticator app.",
            LoginMessages.For("INVALID_TOTP", onCodeStep: true));
    }

    [Fact]
    public void The_too_short_message_uses_the_real_minimum_length()
    {
        Assert.Contains($"at least {Admin.ClinicianAdminService.MinPasswordLength} characters",
            LoginMessages.For("PASSWORD_TOO_SHORT", onCodeStep: false));
        Assert.Equal(8, Admin.ClinicianAdminService.MinPasswordLength);
    }

    [Theory]
    [InlineData("CREDENTIAL_LOCKED")]
    [InlineData("DEVICE_NOT_ALLOWED")]
    [InlineData("CLIENT_NOT_ALLOWED")]
    [InlineData("SESSION_EXPIRED")]
    [InlineData("SIGN_IN_CHANGED")]
    [InlineData("MISSING_FIELDS")]
    [InlineData("PASSWORD_MISMATCH")]
    [InlineData("PASSWORD_UNCHANGED")]
    public void Every_known_code_has_its_own_message(string code)
    {
        Assert.NotEqual("Sign-in failed. Please try again.", LoginMessages.For(code, onCodeStep: false));
    }

    [Fact]
    public void An_unknown_code_gets_a_plain_message_that_gives_nothing_away()
    {
        Assert.Equal("Sign-in failed. Please try again.", LoginMessages.For("SOMETHING_INTERNAL", onCodeStep: false));
    }
}
