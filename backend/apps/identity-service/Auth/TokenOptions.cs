using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Tokens;

namespace IdentityService.Auth;

/// <summary>Who a session is for: mobile (tied to a device) or dashboard (admins only, no device).</summary>
public static class Clients
{
    public const string Mobile = "mobile";
    public const string AdminDashboard = "admin-dashboard";

    public static bool IsKnown(string client) => client is Mobile or AdminDashboard;
}

public sealed class JwtOptions
{
    /// <summary>OpenID Connect needs the issuer to be a URL. Every service checks tokens against this exact value.</summary>
    public string Issuer { get; set; } = "https://identity.melanin-wound-cdss/";
    public string Audience { get; set; } = "melanin-wound-cdss-devices";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>Audience for dashboard tokens; the Sync Gateway rejects it, so they can't touch patient data.</summary>
    public string DashboardAudience { get; set; } = "melanin-wound-cdss-admin";

    /// <summary>A browser session is shorter-lived than a device's.</summary>
    public int DashboardRefreshTokenHours { get; set; } = 12;

    /// <summary>How long a client's session lasts without a refresh (the refresh token's sliding lifetime).</summary>
    public TimeSpan RefreshTokenLifetime(string client) => client == Clients.AdminDashboard
        ? TimeSpan.FromHours(DashboardRefreshTokenHours)
        : TimeSpan.FromDays(RefreshTokenDays);

    /// <summary>PEM key for signing tokens; generated at start-up if empty (local dev only).</summary>
    public string SigningKeyPem { get; set; } = "";
}

/// <summary>RS256 signing key. Only this service has the private half; other services fetch the public half from the JWKS.</summary>
public sealed class SigningKey
{
    public RsaSecurityKey PrivateKey { get; }
    public RsaSecurityKey PublicKey { get; }
    public bool IsEphemeral { get; }

    public SigningKey(string pem)
    {
        var rsa = RSA.Create(2048);
        IsEphemeral = string.IsNullOrWhiteSpace(pem);
        if (!IsEphemeral)
        {
            try
            {
                rsa.ImportFromPem(NormalizePem(pem));
            }
            catch (Exception e) when (e is ArgumentException or CryptographicException)
            {
                // Never echo the value: it is (or is meant to be) a private key.
                throw new InvalidOperationException(
                    "Jwt:SigningKeyPem is not a usable RSA private key. Expected an unencrypted PKCS#8 or PKCS#1 PEM, " +
                    "given as the PEM text (newlines may be written as \\n), as base64 of the PEM file, or as a path to the file.",
                    e);
            }
        }

        var publicKey = new RsaSecurityKey(rsa.ExportParameters(false));
        var kid = Base64UrlEncoder.Encode(publicKey.ComputeJwkThumbprint());
        PublicKey = new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = kid };
        PrivateKey = new RsaSecurityKey(rsa) { KeyId = kid };
    }

    /// <summary>Cleans up a PEM mangled by env files (escaped newlines, quotes, base64, or a file path).</summary>
    internal static string NormalizePem(string value)
    {
        var v = value.Trim().Trim('"', '\'').Trim();
        if (!v.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            if (File.Exists(v)) return NormalizePem(File.ReadAllText(v));
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Replace(v, @"\s+", "")));
                if (decoded.Contains("-----BEGIN", StringComparison.Ordinal)) return NormalizePem(decoded);
            }
            catch (FormatException) { }
            return v;
        }

        v = v.Replace("\\r", "").Replace("\\n", "\n").Replace("\r", "");
        var match = Regex.Match(v, @"-----BEGIN ([A-Z0-9 ]+)-----(.*?)-----END \1-----", RegexOptions.Singleline);
        if (!match.Success) return v;

        // Rebuild with canonical 64-column lines so a PEM flattened onto one line still parses.
        var label = match.Groups[1].Value;
        var body = Regex.Replace(match.Groups[2].Value, @"\s+", "");
        var lines = Enumerable.Range(0, (body.Length + 63) / 64)
            .Select(i => body.Substring(i * 64, Math.Min(64, body.Length - i * 64)));
        return $"-----BEGIN {label}-----\n{string.Join('\n', lines)}\n-----END {label}-----\n";
    }
}
