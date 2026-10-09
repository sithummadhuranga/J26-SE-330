using System.Security.Cryptography;
using System.Text;

namespace IdentityService.Auth;

/// <summary>Encrypts small secrets like the TOTP seed with AES-256-GCM, bound to the clinician id.</summary>
public sealed class SecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public SecretProtector(string passphrase)
    {
        if (passphrase.Length < 32)
            throw new InvalidOperationException("Secrets:EncryptionKey must be at least 32 characters (set Secrets__EncryptionKey).");
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(passphrase));
    }

    public byte[] Protect(byte[] plaintext, Guid boundTo)
    {
        var output = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), boundTo.ToByteArray());
        return output;
    }

    public byte[] Unprotect(byte[] protectedData, Guid boundTo)
    {
        var plaintext = new byte[protectedData.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(protectedData.AsSpan(0, NonceSize), protectedData.AsSpan(NonceSize + TagSize),
            protectedData.AsSpan(NonceSize, TagSize), plaintext, boundTo.ToByteArray());
        return plaintext;
    }
}
