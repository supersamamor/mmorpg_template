# Staff panel (`Game.Admin`, T-805)

The web panel staff use: server status, accounts and characters, a character's saved state, the audit log and the commands their role allows. It is a separate ASP.NET Core
Blazor Server app (`src/Game.Admin`) that does everything through the server's admin API ([ADMIN_API.md](ADMIN_API.md)). It references `Game.Shared` and `Game.Content`
only, never `Game.Server`; it has no database connection and no game rule.

```
Staff browser ──HTTPS, sign-in at your provider──► Game.Admin ──service credential, private network──► Game.Server admin API
```

## 1. Try it on one PC

You need a local game server with its admin API on, which needs a database ([MOBILE_BUILD.md](MOBILE_BUILD.md) §8.1: `docker compose up -d postgres`,
`GAME_DB_CONNECTION`, then `--migrate` once). No identity provider is needed: use the **development sign-in**.

Make one credential and give it to both. In the first terminal (the server):

```powershell
$env:GAME_ADMIN_CREDENTIAL = [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))   # for this PC only; anywhere else use a real random secret
```

```powershell
dotnet run --project src/Game.Server -- --admin-port 7781
```

In the second (the panel), with the **same** value in `GAME_ADMIN_CREDENTIAL`:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"; $env:AdminPanel__DevelopmentSignIn = "true"
```

```powershell
dotnet run --project src/Game.Admin
```

Open <http://127.0.0.1:7790>, choose a name and a role, and sign in. The defaults are the local ones: the panel on `http://127.0.0.1:7790`, the server at
`http://127.0.0.1:7781`, names read from the `content/` folder above the working directory. Without `DOTNET_ENVIRONMENT=Development` the panel is in Production and
refuses these settings. To run it for real, publish it (`dotnet publish`, or the image in §7); `dotnet run` in Production serves no style sheet or script.

### The development sign-in, and how it is refused

It has no password: whoever can open the page picks any role. It is for local use only, and four things keep it there:

1. It is **off** unless `AdminPanel:DevelopmentSignIn` is `true`.
2. With it on in any environment other than `Development`, **the panel does not start** (`PanelSettings.Problems`).
3. Its address (`POST /signin/development`) is **mapped only** in Development with the setting on; otherwise it is a 404 and the sign-in page does not show it.
4. In Development the panel listens on **loopback only** (another address stops the start), and the address answers only a caller from loopback.

It also needs the page's antiforgery token, and requests naming any host but `localhost`, `127.0.0.1` or `[::1]` are refused, so a web page elsewhere cannot use it through your
browser. Its staff ids are `development|<name>`, so audit rows made with it are easy to tell apart. **Never set `DOTNET_ENVIRONMENT=Development` on a deployed panel.** A reverse proxy
or port forward in front of a development panel would make every caller look local: do not put one there.

### With the T-807 local play stack

The play stack's server runs without the admin API, and `docker-compose.yml` publishes only the game's port. You can add both with a compose override file of your own, without
changing the stack's files. **This recipe was not run (see the note at its end).** Save as `.play/admin.override.yml` (`.play/` is gitignored):

```yaml
services:
  play-server:
    environment:
      GAME_ADMIN_PORT: "7781"
      GAME_ADMIN_BIND_ADDRESS: 0.0.0.0           # inside the container only; the port below is published on this PC's loopback
      GAME_ADMIN_PRIVATE_NETWORK: "true"
      GAME_ADMIN_CERTIFICATE_PATH: /certs/server.pfx
      GAME_ADMIN_CERTIFICATE_PASSWORD: ${MMO_PLAY_CERT_PASSWORD:-}
      GAME_ADMIN_CREDENTIAL: ${GAME_ADMIN_CREDENTIAL:?set the service credential}
    ports:
      - "127.0.0.1:7781:7781"
```

Run `.\play-local.ps1 -NoGame` once (it makes the certificate and the image). Then, in a terminal with `GAME_ADMIN_CREDENTIAL` set, from the repository root:

```powershell
$env:MMO_PLAY_CERT_DIR = "./.play/mmo-play/certs"; $env:MMO_PLAY_CERT_PASSWORD = (Get-Content .play/mmo-play/certificate-password.txt -Raw).Trim()
```

```powershell
docker compose -p mmo-play -f docker-compose.yml -f .play/admin.override.yml --profile play up -d --wait play-server
```

Start the panel as above with two more settings: the play certificate is self-signed, so tell the panel to accept exactly it.

```powershell
$env:Server__Url = "https://127.0.0.1:7781"; $env:Server__CertificatePath = ".play/mmo-play/certs/server.pem"
```

Running `play-local.ps1` again recreates the server **without** the override (the admin API goes off); run the `docker compose ... up` line again. `play-local.ps1 -Stop` removes the
container as always. Check the certificate paths if your `-Project` is not `mmo-play`.

> **Not run:** Docker was not available for a separate play stack when this was written. The settings are those of `AdminOptions` and the panel's, which are tested; the compose
> override itself is not.

## 2. Settings

From `appsettings.json` beside the app, environment variables (`:` is written `__`, e.g. `AdminPanel__Listen`) or the command line (`--AdminPanel:Listen=...`). The panel checks
them all at start and lists every problem. **Secrets are never settings**: each is an environment variable, and a setting can only change the variable's name. A list is one
text separated by `;`, or numbered entries (`Oidc__Roles__gm__0`).

| Setting | Default | Meaning |
|---------|---------|---------|
| `AdminPanel:Listen` | `http://127.0.0.1:7790` | `http(s)://address:port` (an IP address or `localhost`). HTTPS everywhere but Development on loopback. `Kestrel:Endpoints` is refused |
| `AdminPanel:CertificatePath` | none | The `.pfx` for HTTPS; its password is the variable `AdminPanel:CertificatePasswordEnv` names (`GAME_PANEL_CERTIFICATE_PASSWORD`) |
| `AdminPanel:PrivateNetwork` | `false` | **Required** to listen on anything but loopback: your word that only staff can reach it (§5) |
| `AdminPanel:AllowedHosts` | the loopback names | The host names requests may use; required off loopback; no wildcards |
| `AdminPanel:DevelopmentSignIn` | `false` | §1; Development only |
| `AdminPanel:SessionMinutes` | 480 | How long a sign-in lasts (5-1440). Never extended |
| `AdminPanel:StatusRefreshSeconds` | 10 | The status page's timer (at least 5) |
| `AdminPanel:ContentDirectory` | found above the working directory | `content/`, read once at start for names. Without it pages show ids |
| `AdminPanel:KeyDirectory` | ASP.NET Core's | Where cookie and antiforgery keys are kept (a volume in a container, or every restart signs everyone out) |
| `Server:Url` | `http://127.0.0.1:7781` | The admin API. `https://` (http only to loopback in Development) |
| `Server:CredentialEnv` | `GAME_ADMIN_CREDENTIAL` | The variable with the service credential: the server's own, 32+ printable characters |
| `Server:TimeoutMs` | 12000 | Above the server's request bound (10 s), so the server's own `timeout` arrives first |
| `Server:CertificatePath` | none | A PEM certificate accepted besides the system's authorities: exactly that certificate, for the right host, within its dates. For a self-signed or private one |
| `Oidc:Authority`, `Oidc:ClientId` | none | Your provider and the panel's client there |
| `Oidc:ClientSecretEnv` | `GAME_PANEL_OIDC_CLIENT_SECRET` | The variable with the client secret |
| `Oidc:Scopes` | `openid;profile` | `openid` is always asked for |
| `Oidc:RoleClaim` | `groups` | The claim that names the staff member's groups or app roles |
| `Oidc:NameClaim` | `name` | The display name |
| `Oidc:UseUserInfo` | `false` | Also ask the userinfo endpoint, for providers that put groups only there |
| `Oidc:Roles:support`, `:gm`, `:admin` | none | The claim values that give each role |

## 3. Set up your identity provider

Any OpenID Connect provider with the authorization-code flow works (Microsoft Entra ID, Okta, Auth0, Keycloak, Google Workspace and others). The template was **tested against
an in-process test provider only**, not against a real one.

1. Make a **confidential web client** (it has a secret). Flow: authorization code; the panel uses PKCE too. Scopes `openid profile`.
2. Redirect URI: `https://<your panel host>/signin-oidc`.
3. **Require MFA for this client at the provider** (a policy on the application or on the staff groups). **The panel does not check for a second factor**: MFA is the provider's
   job, and without that policy a password alone signs staff in.
4. Make one group (or app role) per staff role and put them in the ID token under a claim; set `Oidc:RoleClaim` to it. Assign only staff to the client.
5. Configure the panel:

```
Oidc__Authority=https://login.example.com/realms/staff
Oidc__ClientId=game-staff-panel
GAME_PANEL_OIDC_CLIENT_SECRET=(from your secret store)
Oidc__RoleClaim=groups
Oidc__Roles__support=game-support
Oidc__Roles__gm=game-masters;game-leads
Oidc__Roles__admin=game-admins
```

Group values are compared exactly, letter case too (some providers send group **ids**, not names: use what is in the token). Somebody in several groups gets the highest role;
somebody in none is not signed in. The staff id the server records is the token's `sub`; it must be 1-128 of `A-Z a-z 0-9 . _ - @ : |`, or the sign-in is refused. The panel
keeps in its cookie only that id, the display name, the role and when the sign-in ends: no token, e-mail or group list.

**What takes effect when.** A role taken away or an account disabled at the provider counts at the next sign-in: at most `SessionMinutes` later. Restarting the panel does not
end sign-ins whose keys it keeps. To cut everyone off at once, change the service credential or delete the key directory. **Sign out** ends the panel's sign-in (a copy of its
cookie too, until the panel restarts), not the one at the provider: pressing "Sign in" may sign the same person in again without a password, so on a shared PC sign out
there too.

## 4. Roles and pages

| Role | May |
|------|-----|
| `support` | Every page; `kick` |
| `gm` | What `support` may, and the commands the server gives `gm` |
| `admin` | Everything |

Which command needs which role is the **server's** table (`GET /admin/v1/commands`). The panel greys out a command above the signed-in role (and one whose lowest role it does
not know), and the **server checks every command anyway**: its refusal is shown.

| Page | Shows |
|------|-------|
| Status | Players, connections, content hash, error counts, each map instance's tick times, saving. Asks again every `StatusRefreshSeconds` over the page's live connection. Warns when the panel's content differs from the server's |
| Accounts, Characters | Search by name prefix or id, a page at a time; one account with its characters; one character's saved state (items, skills, quests, outfits, what was set aside), with names from content |
| Audit log | Search by target, staff, action and time (UTC); older pages |
| Commands | What the server offers. **Kick** from an account or character page: a required reason, then a confirmation that shows exactly what will be sent |

**A command is sent once.** The panel retries nothing that writes. The confirmation works once, for five minutes: a reload or a double press sends nothing. If no answer comes back
(timeout, broken connection), the page says the command **may** have been applied and gives the request id to look for in the audit log.

**Commands without a page.** A command the panel has no page for (added to the server later) gets a general form: up to eight named fields, each text, whole number or true/false,
and the reason. It is safe in that it decides nothing: the confirmation shows the exact JSON, and the server checks the role, each field and the reason, and audits it. It
cannot send lists or nested objects, and it explains nothing about the fields, so give a command staff use often a page of its own: its id fields in `CommandForms.Known`, and a
test.

**Reasons hold no personal data.** The warning beside every reason box says so: reasons are kept in the audit log, which nothing can change, also after an account is erased
([STORE_COMPLIANCE.md](STORE_COMPLIANCE.md) §6). Tell staff in your own rules too.

## 5. Security notes for a fork

**Keep it off the public internet (ARCHITECTURE §12.2).** By default the panel listens on loopback only. To let staff reach it, choose one:
- **VPN (preferred):** listen on the VPN interface's address, so only people on the VPN can connect.
- **Allowlist:** a firewall or security group in front that lets only your office or VPN addresses reach the port.

Either way set `AdminPanel:PrivateNetwork=true` and `AdminPanel:AllowedHosts`. **The panel cannot check your network**: that setting is your statement, and it has no IP
allowlist of its own. The sign-in is a second wall, not a reason to expose the panel. The server's admin port is a separate matter and is reachable only from the panel
([ADMIN_API.md](ADMIN_API.md)).

**What the panel does**
- HTTPS only outside Development, with HSTS. Cookies are `__Host-`, `Secure`, `HttpOnly` (session `SameSite=Lax` for the provider's way back, antiforgery `Strict`).
  Antiforgery tokens on every post.
- A content security policy of `default-src 'none'` with this origin only: no inline script or style, no third-party script, style or font, no framing. Pages are `no-store`,
  `Referrer-Policy: no-referrer`.
- Every address needs a sign-in except the sign-in, its callback, the error page, the not-found answer and the static files. Requests naming a host not in the list get a 400.
- The staff id and role come from the sign-in cookie alone. Nothing the browser sends (field, header, query) can set them.
- The service credential and the client secret come from the environment, go to the server and the provider only, and are never on a page or in a log. The HTTP client's own
  logging is removed (it can print headers), and it follows no redirect and uses no proxy or cookies.
- Panel log lines name staff by id and role, with the command, its outcome and the request id: no reason, no player name.

**What is yours to do**
- Whoever has the **service credential** can name any staff member and role to the server ([ADMIN_API.md](ADMIN_API.md) "Trust"), and whoever controls the panel's host has it.
  Keep it in a secret store, rotate it, and look after that host as you would the database's.
- MFA and who is in the staff groups (§3).
- **Player data:** pages show usernames, character names and saved state to every role. Search words travel in the page's address (`?name=`); the panel does not log
  addresses at its default log levels, but a proxy in front may, and the browser keeps them in its history. Raising `Logging:LogLevel:Microsoft.AspNetCore` to `Information`
  writes them to the panel's log.
- **One instance.** Pending confirmations and signed-out sign-ins are kept in the process, and the status page holds a live connection. Behind a load balancer use one instance,
  or build shared storage for both and sticky connections.
- A proxy that terminates TLS in front is not set up here (no forwarded headers): give the panel its own certificate, or add and test that yourself.
- Keep the packages up to date.

## 6. When something fails

| You see | Do |
|---------|----|
| The panel exits listing its settings' problems | Each line names the setting |
| "The game server could not be reached" | Is the server up with `--admin-port`? Is `Server:Url` the admin port, not the game's? |
| "refused the panel's service credential" | The two `GAME_ADMIN_CREDENTIAL` values differ. After five wrong ones the server makes that address wait |
| "certificate is not trusted" | A private or self-signed certificate: `Server:CertificatePath`; the URL's host must be in the certificate |
| "Too many requests" | All staff share the credential's rate limit (10 a second, bursts of 30: `--admin-rate-limit-*` on the server) |
| Ids without names, or the content warning on Status | Give the panel the server's `content/` (`AdminPanel:ContentDirectory`) and restart it |
| A blank Status page that does not refresh | The WebSocket to the panel is blocked, or the request's host is not allowed |

## 7. Container

```powershell
docker build -f src/Game.Admin/Dockerfile -t game-admin-panel .
```

The image holds the published panel and `content/`, runs as an unprivileged user in Production and listens where `AdminPanel__Listen` says: for example
`https://0.0.0.0:8443` with `AdminPanel__PrivateNetwork=true`, `AdminPanel__AllowedHosts`, a mounted certificate, `Server__Url`, the `Oidc__*` settings, a volume for
`AdminPanel__KeyDirectory` (writable by the container's user) and the secrets from your orchestrator. Publish the port on the VPN or private interface only. Rebuild it when
`content/` changes, so names stay right.

**It is not in `docker-compose.yml`, on purpose.** The compose file's services are for development and local play. The play stack's server has its admin API off, and a container
is Production, where the panel needs an identity provider that a local stack does not have (the development sign-in is refused there). A service that cannot start without a
fork's own provider would only mislead; try the panel with `dotnet run` (§1).
