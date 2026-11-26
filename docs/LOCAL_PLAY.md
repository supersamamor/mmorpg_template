# Local play on one PC

How to play the game on one Windows PC: the server and its database in Docker, the game as a Windows program, two windows to see each other (T-807).
This is for trying the template and a fork of it. It is not a production deployment, and it is reachable from this PC only.

For working on the code, run the server with `dotnet run` and the client from Godot instead ([MOBILE_BUILD.md](MOBILE_BUILD.md) §8.1): it is quicker to change.

## 1. What you need

| Tool | Notes |
|------|-------|
| Docker Desktop | **you start it**; the scripts only check that it runs, they never start it |
| .NET 10 SDK | builds the game's C# and its content bundle |
| Godot 4.7.2 **.NET** build with its **export templates** (.NET flavour) | [MOBILE_BUILD.md](MOBILE_BUILD.md) §1. `setup-prereqs.ps1` installs the editor, **not the templates** |
| Git LFS files | `git lfs pull` once in a fresh clone; without it the models are small pointer files and the build stops and says so |

The Android and iOS tools are not needed.

## 2. Play

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\play-local.ps1 -Players 2
```

The first run takes several minutes: it downloads the .NET and PostgreSQL images (about 2 GB), builds the server image and builds the game. Later runs take well under a minute.

In each window: type a username and a password, press **Register**, create a character, press **Play**. Register a different account in the second window; the two characters
start in the same place and see each other. The passwords belong to this local game only: do not reuse one that matters.

Stop everything (accounts and characters are kept):

```powershell
powershell -ExecutionPolicy Bypass -File .\play-local.ps1 -Stop
```

| Command | What it does |
|---------|--------------|
| `.\play-local.ps1` | checks Docker, builds and starts the stack, builds the game if anything it is made from changed, opens one window |
| `-Players 2` | two windows side by side. Running the script again opens more windows; the ones already open stay |
| `-NoGame` | the server only |
| `-Rebuild` | builds the game again even if nothing changed |
| `-Stop` | closes the windows **this script** opened, stops the server (it saves every character first) and removes the two play containers and their network. The data stays |
| `-Reset` | `-Stop`, then deletes the play database (every account and character of the local game) after you type `wipe`. `-Force` does not ask |
| `-Port 7790` | another port on this PC (default 7780). Pass it on every start; the game windows are given it |
| `-Resolution 960x540`, `-At 100,100` | the window size, and where the first window opens. It must be a place on a screen: Godot moves a window that would be off every screen back onto one |
| `-Project name` | another compose project: its own containers, network and database. For a second copy, also use another `-Port` |
| `-Godot path` | the Godot .NET console binary (default: `$env:GODOT`, then the winget install) |

After you change `content/`, `themes/`, the server or the client, run the script again: it rebuilds the server image and, when needed, the game. The two must match, or the game says it must be updated.

The scripts work in Windows PowerShell 5.1 and PowerShell 7.

## 3. What runs

`docker-compose.yml` holds two services behind the profile **`play`**; `play-local.ps1` starts them under the compose project **`mmo-play`**. A plain `docker compose up -d` does what it did
before: the development database (and pgAdmin), and no game server.

| Part | What | Port on this PC |
|------|------|-----------------|
| `play-server` | the image of the root `Dockerfile`: .NET 10, the published server, `content/`, the theme manifests. Runs as the image's unprivileged user. Applies the migrations (`--migrate`), then starts | **127.0.0.1:7780** (`wss://127.0.0.1:7780/ws`, and the account endpoints as `https://` on the same port) |
| `play-db` | PostgreSQL 17 with its own volume `mmo-play_mmo_play_pgdata` | none: only the server reaches it, inside Docker |
| the game | `client/exports/play/game-client.exe` (gitignored), a release build, preset **Windows Desktop (local play)**, built by `client/export-windows.ps1` | |

The development database (`postgres`, container `mmo-postgres`, port 5432) is a different service with a different volume. Playing, `-Stop` and `-Reset` never touch it.
`-Stop` names the two play services when it removes containers, and `-Reset` removes the one play volume, so this holds even with an unusual `-Project`.

Both servers use port 7780 by default, so `dotnet run --project src/Game.Server` and local play cannot both run on it; the script says so, and `-Port` moves local play.

## 4. Transport: why `wss://` and a release build

The rules of ARCHITECTURE §10 stay exactly as they were, and no server or client code changed for local play.

**The server in the container runs as Production.** Docker forwards a published port to the container's network interface, not to its loopback. A server in development listens on loopback
only, because it allows plain `ws://` and the dev login; inside a container nothing could reach it, and letting it listen elsewhere would break that rule. Outside development the server
already listens on every interface of its container and **only with TLS**, and refuses to start without a certificate. So that is what it does. Only compose's `127.0.0.1:` in front of the port decides who can reach it:
this PC. Production also means the dev login is off: players register accounts.

**The certificate is self-signed and made on your PC** by `tools/new-play-cert.ps1`, for `127.0.0.1` and `localhost`, for one year (made again when under a week is left). It cannot sign other certificates.
It lives in `.play/mmo-play/certs/` (gitignored): `server.pfx` (certificate and key, mounted read-only into the container) and `server.pem` (the certificate alone). No key is in the repository.

**The game is a release build, and it is told to trust that one certificate.** `export-windows.ps1 -Certificate server.pem` writes `override.cfg` beside the exe with one Godot setting,
`network/tls/certificate_bundle_override`, which **replaces** the list of certificate authorities with this file. So this copy of the game:
- refuses `ws://`, like every release build (MOBILE_BUILD §8.4);
- verifies the server's certificate as always, and accepts only the local one, so it cannot connect to any other server;
- needs nothing added to the Windows certificate store: nothing about the PC's security changes, and no other program trusts the certificate.

The other choice was a debug build over `ws://`. It would need a development server reachable through a published port, which the rules above forbid.

A debug build or the Godot editor uses the PC's own certificate authorities, so it does **not** connect to the local play server. Use the `dotnet run` server for it.
Without `-Certificate`, `export-windows.ps1` writes no `override.cfg`: that build trusts the PC's authorities, as for a real server with a real certificate (pass its address with `-- --server wss://host:port/ws`).
Double-clicking `game-client.exe` does not work for local play: its built-in address is the development one (`ws://`), which a release build refuses. `play-local.ps1` passes the `wss://` address.

## 5. Development secrets, and how to change them

Nothing here protects anything of value: the database holds a local game and is not reachable from the network. Do not reuse these values anywhere else, and do not use this stack for other players.

| Secret | Where it comes from | To change it |
|--------|--------------------|--------------|
| Database password `mmo_dev_password` (**dev only**) | the default in `docker-compose.yml` | put `MMO_PLAY_DB_PASSWORD=...` in a `.env` file at the repository root (gitignored) or in the environment. PostgreSQL sets the password only when the volume is made, so then run `-Reset` |
| Server's whole connection string | made by compose from the password above | `MMO_PLAY_DB_CONNECTION=...`, the same two ways |
| Certificate file password | random, made with each certificate, kept in `.play/mmo-play/certificate-password.txt` | delete the folder `.play/mmo-play/certs`: the next start makes a new certificate and password |
| Certificate and key | `.play/mmo-play/certs/` | the same |

The port is `-Port` (the script sets `MMO_PLAY_PORT`, `MMO_PLAY_CERT_DIR` and `MMO_PLAY_CERT_PASSWORD` for its own compose commands, so those three are not for `.env`).
The container's own port stays 7780. Other server settings are options and `GAME_NET_*` variables on the `play-server` service ([MOBILE_BUILD.md](MOBILE_BUILD.md) §8.1, `GatewayOptions`).

## 6. Checking it

```powershell
powershell -ExecutionPolicy Bypass -File .\client\check-local-play.ps1
```

Headless, a few minutes. It uses a stack of its own (project `mmo-play-check`, port 7871) and removes it, with its volume, image, certificate and build, at the end; your local game is not touched.
It checks that the server answers nothing to plain HTTP; that the game without the certificate beside it cannot connect; that a release build registers, makes a character and enters the world;
that with two clients at once, one sees the other's character; and that `-Stop` keeps the data. `tests/Game.Tests/Server/LocalPlayTests.cs` checks, without Docker, that the compose file and the
Dockerfile keep the transport rules. CI runs the tests but not this script.

`run-e2e.ps1`'s two-client watchers (`appearance`, `wardrobe`) sign in with the development login, which this server rightly refuses, so the check uses the account run as its second player.

## 7. When something fails

| Message or symptom | What to do |
|--------------------|------------|
| `Docker is not running` | start Docker Desktop, wait until it says it is running, run the script again |
| `Something else already listens on 127.0.0.1:7780` | stop the `dotnet run` server, or use `-Port` |
| The stack did not start | the script prints the server's last lines; `-Stop` removes what is left |
| `... is a Git LFS pointer` | `git lfs pull` |
| The export fails | the Godot export templates are probably missing (MOBILE_BUILD §1) |
| `Cannot replace ...game-client.exe` | close the game windows, or `-Stop` |
| In the game: the server cannot be reached | is the stack up (`docker ps`)? Was the certificate made again after the game was built? Run the script again: it rewrites `override.cfg` |

The game's log is in `%APPDATA%\Godot\app_userdata\MMORPG Template\logs\`. The server's: `docker logs mmo-play-play-server-1`.
Both windows share that folder and the saved settings (UI scale, sound).

## 8. Linux and macOS

**Not tested.** What differs:
- Docker Engine or Docker Desktop; `play-local.ps1` needs PowerShell 7 (`pwsh`). Its game build and windows are Windows only, so use `-NoGame` and build the game by hand.
- `tools/new-play-cert.ps1` should run under `pwsh` (it was run there in a Linux container). Without PowerShell, in `.play/mmo-play/certs/`:

  ```sh
  export MMO_PLAY_CERT_PASSWORD="$(openssl rand -hex 24)"
  openssl req -x509 -newkey rsa:2048 -sha256 -days 365 -nodes -keyout server.key -out server.pem -subj "/CN=MMORPG local play" \
    -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" -addext "basicConstraints=critical,CA:FALSE" -addext "extendedKeyUsage=serverAuth"
  openssl pkcs12 -export -inkey server.key -in server.pem -out server.pfx -passout env:MMO_PLAY_CERT_PASSWORD && rm server.key
  chmod 644 server.pfx   # the container's user is not yours, and on Linux a mounted file keeps its permissions
  ```

  Then, from the repository root in the same shell: `docker compose -p mmo-play --profile play up -d --build --wait play-server`.
  Anyone who can read that folder can read the key; it is only good for impersonating your own local server to the copy of the game that trusts it.
- The game: there is no local play preset for either. On Linux the `Linux (release check)` preset makes a release build; put an `override.cfg` beside it with
  `[network]` and `tls/certificate_bundle_override="/full/path/server.pem"`, and start it with `-- --server wss://127.0.0.1:7780/ws`. There is no macOS preset.
- Stop with `docker compose -p mmo-play --profile play rm --stop --force play-server play-db`; wipe with `docker volume rm mmo-play_mmo_play_pgdata`.
