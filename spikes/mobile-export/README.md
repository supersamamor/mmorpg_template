# T-005 mobile export spike (throwaway)

Proves, or disproves, that Godot 4.7.2 .NET can carry this game on Android and iOS
(ADR-001). Not the real client; delete once T-206 starts `/client`.

| Folder | What |
|--------|------|
| `godot/` | Godot project. The whole scene is built in code in `scripts/Main.cs`; `MobileSpike.csproj` references `src/Game.Shared` (netstandard2.1). |
| `echo-server/` | Plain ASP.NET Core WebSocket echo server, `ws://:8080/echo` and `wss://:8443/echo`. |
| `builds/` | Export output (gitignored). |

`Directory.Build.props` / `Directory.Packages.props` here are deliberately empty so the repo's
root build settings (net10.0, warnings-as-errors, central package versions) do not apply.

Build, export, install and measurement steps: [docs/MOBILE_BUILD.md](../../docs/MOBILE_BUILD.md).
Results: ADR-001 "Spike results".
