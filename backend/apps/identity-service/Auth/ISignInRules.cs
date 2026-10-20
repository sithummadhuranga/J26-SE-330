namespace IdentityService.Auth;

/// <summary>
/// The sign-in rules the sign-in page relies on. <see cref="CredentialValidator"/> is the real one; tests pass a fake,
/// so the page can be tested without a database.
/// </summary>
public interface ISignInRules
{
    /// <summary>Checks one sign-in attempt. See <see cref="CredentialValidator.ValidateAsync"/> for the reason codes.</summary>
    Task<CredentialResult> ValidateAsync(SignInAttempt attempt, CancellationToken ct);

    /// <summary>Replaces a password an admin set. See <see cref="CredentialValidator.ChangePasswordAsync"/>.</summary>
    Task<(string? NewHash, string? Error)> ChangePasswordAsync(string username, string expectedHash, string? newPassword,
        CancellationToken ct);
}
