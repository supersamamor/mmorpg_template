# Load test (T-214)

Scripted bots against a real `Game.Server` process on loopback. Each bot logs in, then either walks around where it joined or fights the
nearest monster (and walks toward it when refused), using the T-307 messages (`attack`, `respawn`, ...). It reports per-client KB/s,
the server's tick p50/p99/max over the last minute (after warm-up), its CPU per tick, and its memory.

```bash
dotnet build -c Release src/Game.Server
dotnet run -c Release --project tools/loadtest -- --bots 50
```

Options: `--bots N` (50), `--grace G` (0: first log in G characters and drop them, so they wait in their grace period), `--compression`
(the server and the bots negotiate permessage-deflate), `--warmup S` (30), `--measure S` (60, at least 60 so the server's one-minute tick
window is all after warm-up), `--fighters 0.5` (share of bots that fight), `--server path/Game.Server.dll`.
Fighters also pick up the ground items they may take within 6 cells, once per item (T-401: `pick_up`, counted as sent, picked up and refused);
`--no-pickups` turns that off, which a server from before T-401 needs (it does not know the message).
`--spare-slots N` (0) adds N sessions to `max-sessions` (bots + grace + N), so a real client can join the bot-filled server, and `--port P` (0 = a free port) fixes the port so you can point the
client at it: `dotnet run -c Release --project tools/loadtest -- --bots 50 --spare-slots 2 --port 7791`, then `godot --path client -- --measure 40 --server ws://127.0.0.1:7791/ws`
(the server runs for warm-up + measure + 60 s; the client logs in as the dev name `dev` unless `--name` says otherwise). With spare slots the end-of-run "full server" check is skipped.

How it measures:
- **Per-client KB/s** is JSON payload received by each bot during the measured window. **Wire KB/s** comes from the server, which counts
  every byte it writes to client sockets (`GatewayStats.WireBytesOut`): frame headers, and compressed frames when deflate is on.
- **Tick p50/p99/max** come from the server's own `[net] ticks` log line (each instance's last 600 ticks), read after the window.
- **CPU** is the server process's total processor time over the window, divided by the ticks in it (all threads: ticks, sockets, GC).
- **Memory** is the server process's working set (and the GC heap from its log).
- With `--grace`, `max-sessions` is set to bots + grace, so at the end the server is full: a new login must get `server_full` and a
  resume of a parked character must get `welcome`.

The bots run on the same machine as the server, so the numbers include their CPU contention. The output is a short report; the server's
log stays in memory.
