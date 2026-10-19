# Combining STS2_MCP + LocalCoop on Ubuntu — apply notes

## 1. STS2_MCP port-per-client patch
Apply `McpMod.LoadPort.patch.cs` into `McpMod.cs`: delete the body of the
existing `private static int LoadPort()` method and paste both methods from
the patch file in its place (keep everything else in `McpMod.cs` untouched).

Why it's needed: LocalCoop launches every local client from the same
`<game_root>/mods/` directory, so `STS2_MCP.dll` — and any conf file sitting
beside it — is literally the same file on disk for all N processes. The
original `LoadPort()` would have every client either race to create/read one
shared `STS2_MCP.conf` (all binding 15526, only the first winning) or step on
each other's writes. The patch instead reads LocalCoop's own
`LOCALCOOP_CONFIG_DIR` env var (already set per-process by the launcher
below) and derives the port as `15526 + clientIndex` — read-only, no shared
file writes, works whether or not LocalCoop is present at all.

Ports end up:
| clientIndex | role (client 0 is host) | MCP port |
|---|---|---|
| 0 | host | 15526 |
| 1 | client | 15527 |
| 2 | client | 15528 |
| 3 | client | 15529 |

Point your MCP server config / `--port` flag at whichever client you're
driving (`server.py --port 15527` for client 1, etc. — see `mcp/server.py`'s
`--port`/`--host` args, already supported).

## 2. Building STS2_MCP for Linux
Nothing to change here — `STS2_MCP.csproj` already resolves
`data_sts2_linuxbsd_x86_64` for non-macOS/non-Windows and the mod has no
Win32 dependencies at all. Build with:
```
dotnet build STS2_MCP.csproj -c Release -o out/STS2_MCP -p:STS2GameDir="/path/to/Slay the Spire 2"
```
then copy `out/STS2_MCP/STS2_MCP.dll` + `mod_manifest.json` (renamed to
`STS2_MCP.json`) into `<game_root>/mods/`.

## 3. LocalCoop on Linux — what's already safe, what to watch
Good news: most of LocalCoop's C# is already Linux-safe by construction:
- `DynamicControllerCoordinator.TryIsGameFocusedWindow()` already guards its
  `user32.dll` P/Invoke calls behind `OperatingSystem.IsWindows()` and falls
  back to a reflection call into the game's own focus check otherwise.
- The XInput diagnostics poll (`SteamControllerInputRuntimeDiagnosticsPatches`)
  already catches `DllNotFoundException`/`EntryPointNotFoundException` around
  `xinput1_4.dll` and logs "XInput poll unavailable" instead of crashing.
- The broker, `BrokerBackedNetService`, `BrokerNetGameService`, and the lobby
  substitution patches are pure TCP/Harmony/reflection — no OS dependency.

What is genuinely Windows-only and doesn't matter for your use case (agent
drives the game via each client's own MCP port, not by screen position):
- **All window placement / DPI-awareness / Steam-Input-focus-bootstrap code
  lives in the PowerShell launcher** (`Start-LocalCoopTwoClient.ps1`'s
  `user32.dll`/`dwmapi.dll` `Add-Type` block), not in the mod DLL. Since
  you're using `start-localcoop-clients.sh` instead of the PowerShell
  scripts, none of that code path is ever invoked — nothing to strip.
- Controller/gamepad assignment (`SteamDynamicControllerInput`,
  `ControllerSessionCoordinator`) is irrelevant if you're not using physical
  controllers for AI-driven clients; it will simply have nothing to assign
  and stay idle. Safe to leave as-is.

Net effect: you should be able to drop LocalCoop's mod DLL onto the Linux
build unmodified. If you hit an actual crash from a Win32 call that isn't
guarded (I didn't find one, but 200+ files were skimmed, not exhaustively
verified), the fix is the same pattern already used in
`TryIsGameFocusedWindow`: wrap it in `if (OperatingSystem.IsWindows()) { ... }
else { /* no-op or reflection fallback */ }`.

You'll need to build LocalCoop's own projects for Linux too — they're plain
`net9.0` class libraries/console apps with no `RuntimeIdentifier` pinned to
`win-x64` except in the release-packaging script
(`Build-LocalCoopRelease.ps1`, which you don't need — the bash launcher below
runs everything via `dotnet run`/`dotnet build`, not that packaging script).

## 4. Launch script
`start-localcoop-clients.sh` — run from inside your LocalCoop checkout:
```bash
./start-localcoop-clients.sh -g "/path/to/Slay the Spire 2" -c 4
```
It starts the broker, prepares N client configs via
`LocalCoop.MultiClientHarness` (unchanged, portable), launches N game
processes each with its own `LOCALCOOP_CONFIG_DIR`, and does **no** window
placement/focus/DPI work. Use `-n` to just prepare configs without launching
(useful for a dry run), `-r` to reuse an already-running broker.

## 5. Testing order I'd suggest
1. Singleplayer, STS2_MCP only, on Linux — sanity check the base build and
   your performance baseline.
2. `-c 2` two-client co-op with the port patch, confirm both
   `curl localhost:15526/` and `curl localhost:15527/` respond.
3. Scale to `-c 4` once 2-client co-op is confirmed stable.
