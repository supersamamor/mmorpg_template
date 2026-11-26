# Admin API (T-804)

The server's private HTTP API for staff tooling (ARCHITECTURE §12). The panel (`Game.Admin`, T-805) is its only intended caller; T-806 adds the moderation and support
commands. Code: `src/Game.Server/Admin/`; contracts and terms: `Game.Shared` (`AdminPaths`, `AdminHeaders`, `StaffRoles`, `AdminActions`, `AdminErrors`, `AdminOutcomes`).

## Running it

Off unless a port is set. Options are `--admin-<name> <value>` or `GAME_ADMIN_<NAME>` (the command line wins); `Game.Server --help`-style usage is printed on a bad option.

| Option | Default | Meaning |
|--------|---------|---------|
| `port` | off | TCP port; never the game's |
| `bind-address` | loopback | Anything else needs `private-network true`, and development allows only loopback |
| `private-network` | false | Says the address is reachable only from staff tooling (a VPN, or an allowlist) |
| `certificate-path`, `certificate-password-env` | none, `GAME_ADMIN_CERTIFICATE_PASSWORD` | A `.pfx`; **required outside development** (HTTPS only) |
| `credential-env` | `GAME_ADMIN_CREDENTIAL` | The environment variable holding the service credential (at least 32 characters; never an option, never logged) |
| `max-body-bytes`, `request-timeout-ms`, `step-timeout-ms` | 8192, 10000, 3000 | Bounds of a request and of a command's step on the tick |
| `rate-limit-per-second`, `rate-limit-burst` | 10, 30 | Per credential |
| `failure-burst`, `failures-per-minute` | 5, 10 | Requests with a missing or wrong credential, per address |
| `max-page-size` | 100 | Largest page of a lookup (the default page is 25) |

It needs the database (its audit log is there); without one the server refuses to start. It refuses to start, too, on the game's port, outside development without a
certificate, on a non-loopback address without `private-network true`, or without a credential.

**Locally** (development, loopback, plain HTTP):

```powershell
$env:GAME_ADMIN_CREDENTIAL = [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))   # any 32+ characters; use a real random secret anywhere else
dotnet run --project src/Game.Server -- --admin-port 7781
```

**Behind a VPN or an allowlist** (production): bind it to the VPN interface's address (or a private one the firewall only lets staff tooling reach), give it a certificate, and say so:
`--admin-bind-address 10.8.0.1 --admin-private-network true --admin-certificate-path /etc/game/admin.pfx`. Never publish the port on a public address (in a container, publish it on the private
interface only, as T-807 publishes the game's on `127.0.0.1`). The panel then calls `https://10.8.0.1:7781`.

## Every request

```
Authorization: Bearer <service credential>
X-Staff-Id:   <the identity provider's subject>     1-128 of A-Z a-z 0-9 . _ - @ : |
X-Staff-Name: <display name, percent-encoded>       1-64 characters
X-Staff-Role: support | gm | admin
X-Request-Id: <optional>                            1-64 of A-Z a-z 0-9 . _ -; made by the server when missing, sent back on every answer
```

Checked in this order: the address is not locked out by failed credentials (429); the credential is right (401, counted against the address); the credential is within its rate (429 with
`Retry-After`); the staff member is well formed (400 `invalid_staff`) and the role is a role (403 `unknown_role`). Every answer is JSON, `Cache-Control: no-store`, with the request id; a refusal is
`{"error":"<Admin errors term>","request_id":"..."}`. Nothing outside `/admin/v1` is served (404), and the game's paths are not on this listener at all.

```powershell
$h = @{ Authorization = "Bearer $env:GAME_ADMIN_CREDENTIAL"; "X-Staff-Id" = "dev"; "X-Staff-Name" = "Dev"; "X-Staff-Role" = "support" }
```

## Reads (any role)

| Endpoint | Answer |
|----------|--------|
| `GET /admin/v1/status` | `content_hash` (`ClientBundleExporter.Hash`), `started_at`, `players_online`, `sessions`, `instances[]` (`map_id`, `tick`, `tick_p50_micros`, `tick_p99_micros`, `tick_max_micros`, `tick_lifetime_max_micros`, `system_errors`, `handler_errors`, `intents_dropped`), `saves` (or null), `errors` (`logged_errors`, `logged_warnings`, `server_error_byes`) |
| `GET /admin/v1/accounts?name=&id=&after=&limit=` | `items[]` (`id`, `username`, `created_at`, `password_changed_at`, `erase_after`, `characters`, `sessions`, `online`) and `next` (pass it as `after`). `name` is a username prefix |
| `GET /admin/v1/accounts/{id}` | `account` and its `characters[]` (deleted ones too) |
| `GET /admin/v1/characters?name=&id=&account=&after=&limit=` | `items[]` (`id`, `account_id`, `slot`, `name`, `job_id`, `created_at`, `deleted_at`, `online`) and `next`. `name` is a name prefix, any letter case |
| `GET /admin/v1/characters/{id}` | `character` and its saved `state` (null when never saved): place, vitals, save point, levels, EXP, job, points, currency, stats, skills, quests, cooldowns, set-aside, outfits, items |
| `GET /admin/v1/audit?target=&staff=&action=&from=&to=&before=&limit=` | `items[]` newest first (`id`, `at`, `request_id`, `staff_id`, `staff_name`, `staff_role`, `action`, `target_kind`, `target_id`, `before`, `after`, `reason`) and `next` (pass it as `before`). `from` and `to` are ISO 8601 |
| `GET /admin/v1/commands` | Every command with its `lowest_role`, the `read_role` and the `roles` |

No answer ever holds a password hash, a session token or its hash. A query parameter that is malformed, given twice, or over 128 characters is `invalid_request`; `limit` is 1 to `max-page-size`.

```powershell
Invoke-RestMethod -Headers $h http://127.0.0.1:7781/admin/v1/status
Invoke-RestMethod -Headers $h "http://127.0.0.1:7781/admin/v1/accounts?name=ada"
```

## Commands

`POST /admin/v1/commands/{action}` with a JSON object; every command has `reason` (1 to 500 characters, required). The answer is
`{"outcome":"done|accepted|refused","request_id":"...","audit_id":7,"error":null,"result":{...}}`: 200 when done or accepted, otherwise 400 (`invalid_request`, `reason_required`), 403 (`forbidden`: the role is below
the command's), 404 (`not_found`: no such command), 409 (`unknown_target`), 503 (`unavailable`), 504 (`timeout`: the world did not answer; nothing was changed).

| Action | Lowest role | Body | What it does |
|--------|-------------|------|--------------|
| `kick` | `support` | `account_id`, `reason` | Ends every session of the account and its live game, as a password change does (T-710): `bye kicked`, the character (also one in its grace period) leaves on its next tick, saved, its resume token dead. The player signs in again. `accepted` when it was in the world, `done` when not; `result`: `was_online`, `sessions_ended` |

```powershell
$body = @{ account_id = "<id>"; reason = "spamming the start square" } | ConvertTo-Json
Invoke-RestMethod -Method Post -Headers $h -ContentType application/json -Body $body http://127.0.0.1:7781/admin/v1/commands/kick
```

`client/run-e2e.ps1 -Run kick` does exactly this against a real client.

## Trust: what the server checks, and what it takes from the panel

The panel signs staff in with OIDC and MFA (§12.2); the server has no identity provider of its own. So:

- **The server checks** that the caller holds the service credential (constant time: both sides are SHA-256 digests compared with `FixedTimeEquals`), the shape of the staff member, that the role is one of
  `support`, `gm`, `admin`, the command's lowest role in `AdminCommandTable` (one table, on the server, for every command, whatever the panel shows), the reason, the rate limits, and every field of every command.
- **It takes from the panel** that the person is who the id says and holds that role. Only something with the credential can say it, and the credential lives with the panel on the private network.
- **Why:** verifying the OIDC token itself would need the identity provider's configuration in the game server and a second trust path to keep working; the template keeps one boundary (the private network
  and the credential). Its cost: whoever has the credential can name any staff member and role. Keep it in a secret store, rotate it (restart with a new one), and never give it to a person.
  A fork that wants the server to check staff itself has the panel forward the provider's signed ID token (or its own signed assertion) in a header and verifies it here against the provider's keys: a
  follow-up, not built.

## Audit log

Table `admin_audit` (one row per change: `id`, `at`, `request_id`, `staff_id`, `staff_role`, `action`, `target_kind`, `target_id`, `before`, `after` (JSON), `reason`) and `admin_staff` (the staff the
rows name: id, display name, last role, first and last seen). A change in the database and its row are **one transaction** (kick ends the sessions and writes its row together); a change that is not in the
database gets its row written before the answer (`IAdminStore.AppendAuditAsync`). A refused command changed nothing and writes no row.

**Append-only:** triggers refuse every `UPDATE`, `DELETE` and `TRUNCATE` of `admin_audit`, whoever asks, the table's owner included (the template's server runs as the owner of its tables, so
grants alone could not hold it); a staff row named by the log cannot be deleted either. What still gets past them is DDL (dropping a trigger), which the server only runs in migrations. A deployment that
wants even that closed runs migrations as the owner role and the game server as another role with `SELECT, INSERT` on `admin_audit`, `SELECT, INSERT, UPDATE` on `admin_staff` and its usual rights
elsewhere. Erasing an account leaves its rows (docs/STORE_COMPLIANCE.md §6 "Audit rows").

## Events and the road into the tick

After a change is made and audited, `AdminActionApplied` (audit id, request id, staff id and role, action, target) is raised on the **admin event bus** (`AdminEvents`, an `EventBus` like an instance's,
dispatched at once on the request's thread). A subscriber that must touch the world queues an intent, as the gateway does.

A command that reads or changes the world does it in a **step on the tick**: `AdminRoad.OnTickAsync(entity, action, step)` queues an `AdminIntent` on the instance that holds the entity; `AdminSystem`
(last before the AOI) runs it there with the tick's context, like any intent, and the command waits up to `step-timeout-ms`. Kick reads the character's place this way for its `before`.

## How T-805 and T-806 build on it

- **T-805 (the panel)** references `Game.Shared` for the paths, headers and terms, does OIDC and MFA, maps its groups to the three roles, sends the five headers on every call, shows `GET /commands` to grey out
  what a role cannot do (the server checks anyway), and tells staff to keep personal data out of reasons. It keeps the credential in a secret store and calls only over the private network.
- **T-806 (moderation and support)** adds each command as an `IAdminCommand` with a row in `AdminCommandTable` and its action in `AdminActions` (and VOCABULARY). A command whose change is in the database
  makes it and its row in one transaction in `PostgresAdminStore` (as `EndSessionsAsync` does); one that changes a character in the world does it in a step on the tick (`OnTickAsync`) and writes its row
  before answering, or, for items and currency, through the character's next save. Mute and ban need checks at login and on the live session (a new `bye` reason for a ban); maintenance mode needs a
  timed kick of everyone (this command, in a loop). Items it creates get new ids and `source = admin` (§12.3).
