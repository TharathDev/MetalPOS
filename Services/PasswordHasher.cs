using System;
using System.Security.Cryptography;

namespace PosApp.Services;

internal static class PasswordHasher
{
    private const int Iterations = 210_000;
    private const int HashSize = 32;

    public static bool Verify(string password, byte[] salt, byte[] expectedHash)
    {
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    public static (byte[] Salt, byte[] Hash) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);
        return (salt, hash);
    }
}

internal readonly record struct LoginCredential(byte[] PasswordSalt, byte[] PasswordHash, string Role);
internal readonly record struct UserAccount(string Phone, string Role);
