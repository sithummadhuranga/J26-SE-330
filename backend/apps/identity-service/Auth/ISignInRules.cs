namespace IdentityService.Auth;

// Lets the sign-in page be tested with a fake instead of the database.
public interface ISignInRules
{
    Task<CredentialResult> ValidateAsync(SignInAttempt attempt, CancellationToken ct);

    Task<(string? NewHash, string? Error)> ChangePasswordAsync(string username, string expectedHash, string? newPassword,
        CancellationToken ct);
}
