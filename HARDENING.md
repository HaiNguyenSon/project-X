# Remote access & hardening

The app keeps **all document data on the machine** (text, chunks, embeddings, the
model). The only thing that ever leaves is encrypted chat traffic between your
phone and the app, carried over your private Tailscale network.

## 1. Set a password

The app **refuses to start without a password** unless you explicitly opt into
running with no authentication (`Auth:AllowAnonymous=true`). Local development is
pre-configured to allow anonymous (`appsettings.Development.json`), and logs a
warning whenever it runs without auth. This is a fail-safe so the app is never
accidentally exposed open.

Set a password before exposing the app. Don't commit it to `appsettings.json` —
use an environment variable or user-secrets:

```powershell
# Environment variable (note the double underscore)
$env:Auth__Password = "a-long-random-passphrase"

# …or user-secrets (per-machine, never committed)
dotnet user-secrets init
dotnet user-secrets set "Auth:Password" "a-long-random-passphrase"
```

With a password set, every page redirects to `/login` until you sign in. The
session cookie lasts 30 days (sliding) and "Sign out" is in the nav.

## 2. Bind to localhost only

In a published/production run the app binds to `http://localhost:5088` (see the
`Kestrel` section in `appsettings.json`). It is **not** reachable from the LAN.
Tailscale is the only external entry path. (In `dotnet run` with a launch
profile, `launchSettings.json` controls the URL instead.)

## 3. Expose it over Tailscale with `tailscale serve`

`tailscale serve` proxies tailnet traffic to the localhost app and terminates TLS
with a real certificate for your machine's MagicDNS name — so the app itself
stays bound to localhost.

On the laptop:

```powershell
# Install Tailscale (https://tailscale.com/download) and sign in
tailscale up

# Proxy HTTPS on the tailnet to the local app
tailscale serve --bg https / http://localhost:5088
tailscale serve status     # shows the https://<machine>.<tailnet>.ts.net URL
```

On the phone:

1. Install the Tailscale app and sign in to the **same** tailnet.
2. Open `https://<machine-name>.<your-tailnet>.ts.net` in the browser.
3. Sign in with the app password.

MagicDNS gives you the friendly `<machine>.<tailnet>.ts.net` hostname
automatically (enable it in the Tailscale admin console if it isn't already).

## 4. Keep the app bound to localhost

Forwarded headers (`X-Forwarded-Proto`/`For`) are only trusted from any source
while the app is bound to loopback — the case behind `tailscale serve`. If you
bind it to a public address, those headers are no longer trusted (they can be
spoofed) and the app logs a warning. In short: leave the `Kestrel` binding on
`localhost` and let Tailscale handle external access.

The app also sends a Content-Security-Policy and anti-clickjacking headers on
every response.

### Notes
- `tailscale serve` (default) keeps the app private to your tailnet. Do **not**
  use `tailscale funnel` unless you intend to publish to the public internet.
- Because traffic arrives over Tailscale's encrypted mesh, you don't need to open
  any firewall ports or set up port forwarding.
- The HTTPS-redirect warning in dev logs ("Failed to determine the https port")
  is harmless when running http-only locally; behind `tailscale serve`, TLS is
  handled by Tailscale.
