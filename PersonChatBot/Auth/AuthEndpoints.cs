using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using PersonChatBot.Configuration;

namespace PersonChatBot.Auth;

/// <summary>Minimal, no-JavaScript login/logout endpoints backed by a cookie.</summary>
public static class AuthEndpoints
{
    /// <summary>Name of the rate-limiting policy applied to the login POST.</summary>
    public const string LoginRateLimitPolicy = "login";

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/login", (HttpContext ctx) =>
        {
            var error = ctx.Request.Query.ContainsKey("error");
            return Results.Content(LoginHtml(error), "text/html");
        }).AllowAnonymous();

        app.MapPost("/login", async (HttpContext ctx, IOptions<AuthOptions> options) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var supplied = form["password"].ToString();

            if (!options.Value.VerifyPassword(supplied))
                return Results.Redirect("/login?error=1");

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "owner")],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });

            return Results.Redirect("/");
        }).AllowAnonymous().DisableAntiforgery().RequireRateLimiting(LoginRateLimitPolicy);

        app.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).DisableAntiforgery();
    }

    private static string LoginHtml(bool error) =>
        $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>Sign in — Document Chat</title>
            <style>
                body { font-family: system-ui, sans-serif; background:#f8f9fa; display:flex;
                       align-items:center; justify-content:center; height:100vh; margin:0; }
                form { background:#fff; padding:2rem; border-radius:.75rem; box-shadow:0 1px 8px rgba(0,0,0,.1);
                       width:min(20rem,90vw); display:flex; flex-direction:column; gap:.75rem; }
                h1 { font-size:1.25rem; margin:0 0 .5rem; }
                input { padding:.6rem .75rem; border:1px solid #ced4da; border-radius:.5rem; font:inherit; }
                button { padding:.6rem; border:0; border-radius:.5rem; background:#0d6efd; color:#fff;
                         font:inherit; cursor:pointer; }
                .error { color:#b02a37; font-size:.85rem; margin:0; }
            </style>
        </head>
        <body>
            <form method="post" action="/login">
                <h1>Document Chat</h1>
                {{(error ? "<p class=\"error\">Incorrect password.</p>" : "")}}
                <input type="password" name="password" placeholder="Password" autofocus required />
                <button type="submit">Sign in</button>
            </form>
        </body>
        </html>
        """;
}
