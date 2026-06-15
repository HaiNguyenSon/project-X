using PersonChatBot.Auth;

namespace PersonChatBot.Configuration;

/// <summary>
/// Single-password gate for the app. Leave <see cref="Password"/> empty to disable
/// auth entirely (fine for local development). Set it before exposing over Tailscale.
/// Prefer providing it via an environment variable (Auth__Password) or user-secrets
/// rather than committing it to appsettings.json.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Plaintext password. Convenient, but prefer <see cref="PasswordHash"/>.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// PBKDF2 hash of the password (generated with `dotnet run -- hash-password ...`).
    /// Takes precedence over <see cref="Password"/> when set, so the secret never has
    /// to be stored in plaintext.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// Explicit opt-in to run with NO authentication (everyone with network access
    /// can use the app). Must be set deliberately; otherwise a missing password is
    /// treated as a misconfiguration and the app refuses to start.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    public bool Enabled => !string.IsNullOrEmpty(Password) || !string.IsNullOrEmpty(PasswordHash);

    /// <summary>Verify a supplied password against the configured hash or plaintext.</summary>
    public bool VerifyPassword(string supplied)
    {
        if (!string.IsNullOrEmpty(PasswordHash))
            return PasswordHasher.Verify(supplied, PasswordHash);

        return !string.IsNullOrEmpty(Password) &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.UTF8.GetBytes(supplied),
                   System.Text.Encoding.UTF8.GetBytes(Password));
    }
}
