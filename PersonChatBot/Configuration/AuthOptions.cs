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

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Explicit opt-in to run with NO authentication (everyone with network access
    /// can use the app). Must be set deliberately; otherwise a missing password is
    /// treated as a misconfiguration and the app refuses to start.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    public bool Enabled => !string.IsNullOrEmpty(Password);
}
