namespace PersonChatBot.Security;

/// <summary>
/// Adds defensive HTTP response headers. The Content-Security-Policy is tuned for
/// a Blazor Server app: scripts come from the app itself (blazor.web.js), the
/// SignalR circuit connects same-origin, and inline styles are allowed (the login
/// page and scoped component styles use them). frame-ancestors blocks clickjacking.
/// </summary>
public static class SecurityHeadersMiddleware
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self' ws: wss:; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "object-src 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            await next();
        });
}
