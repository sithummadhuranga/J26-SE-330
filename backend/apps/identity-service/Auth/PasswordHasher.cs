using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace IdentityService.Auth;

/// <summary>Argon2id with a per-credential salt (architecture §12). Never reversible encryption.</summary>
public sealed class PasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 3;
    private const int MemoryKb = 64 * 1024;
    private const int Parallelism = 2;

    public (byte[] Hash, byte[] Salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return (Compute(password, salt), salt);
    }

    public bool Verify(string password, byte[] expectedHash, byte[] salt) =>
        CryptographicOperations.FixedTimeEquals(Compute(password, salt), expectedHash);

    private static byte[] Compute(string password, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            Iterations = Iterations,
            MemorySize = MemoryKb,
            DegreeOfParallelism = Parallelism,
        };
        return argon.GetBytes(HashBytes);
    }
}
