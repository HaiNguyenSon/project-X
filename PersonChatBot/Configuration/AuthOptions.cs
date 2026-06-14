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

    public bool Enabled => !string.IsNullOrEmpty(Password);
}
