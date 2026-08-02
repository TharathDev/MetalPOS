using System;
using System.Linq;
using System.Text;

namespace PosApp.Services;

/// <summary>
/// Local startup authentication backed by the application's SQLite Users table.
/// Passwords are verified against salted PBKDF2 hashes and are never logged.
/// </summary>
public sealed class AuthService
{
    public const string PrimaryAdministratorPhone = "+855962201111";
    private const int MaximumAttempts = 5;
    private static readonly TimeSpan LockoutPeriod = TimeSpan.FromSeconds(30);

    private readonly DatabaseService _database;
    private int _failedAttempts;
    private DateTime _lockedUntilUtc;

    public AuthService(DatabaseService database)
    {
        _database = database;
    }

    public LoginResult Authenticate(string? phoneNumber, string? password)
    {
        if (DateTime.UtcNow < _lockedUntilUtc)
            return LoginResult.Failure("Too many attempts. Please wait 30 seconds and try again.");

        var normalizedPhone = NormalizeCambodianPhone(phoneNumber);
        if (normalizedPhone is null)
            return LoginResult.Failure("Enter a valid Cambodian phone number (for example: 012 345 678 or +855 12 345 678).");

        var credential = _database.GetLoginCredential(normalizedPhone);
        if (credential is null || !PasswordHasher.Verify(password ?? string.Empty, credential.Value.PasswordSalt, credential.Value.PasswordHash))
        {
            _failedAttempts++;
            if (_failedAttempts >= MaximumAttempts)
            {
                _failedAttempts = 0;
                _lockedUntilUtc = DateTime.UtcNow.Add(LockoutPeriod);
                return LoginResult.Failure("Too many attempts. Please wait 30 seconds and try again.");
            }

            return LoginResult.Failure("Incorrect phone number or password.");
        }

        _failedAttempts = 0;
        _lockedUntilUtc = default;
        return LoginResult.Success(normalizedPhone);
    }

    public static bool IsPrimaryAdministrator(string? phone) =>
        string.Equals(phone, PrimaryAdministratorPhone, StringComparison.Ordinal);

    /// <summary>
    /// Converts Cambodian national or international numbers to +855XXXXXXXX.
    /// Accepts 8 or 9 digits after the national zero/country code, which covers
    /// valid Cambodian mobile and landline number lengths without accepting a
    /// different country code.
    /// </summary>
    public static string? NormalizeCambodianPhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var compact = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (char.IsDigit(character))
                compact.Append(character);
            else if (character == '+' && compact.Length == 0)
                compact.Append(character);
            else if (character is not (' ' or '-' or '(' or ')' or '.'))
                return null;
        }

        var number = compact.ToString();
        string national;
        if (number.StartsWith("+855", StringComparison.Ordinal))
            national = number[4..];
        else if (number.StartsWith("855", StringComparison.Ordinal))
            national = number[3..];
        else if (number.StartsWith('0'))
            national = number[1..];
        else
            return null;

        return national.Length is 8 or 9 && national.All(char.IsDigit)
            ? "+855" + national
            : null;
    }
}

public readonly record struct LoginResult(bool Succeeded, string Message, string? Phone)
{
    public static LoginResult Success(string phone) => new(true, string.Empty, phone);
    public static LoginResult Failure(string message) => new(false, message, null);
}
