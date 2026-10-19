# LocalCoop for Slay the Spire 2

LocalCoop is an experimental same-machine local co-op launcher and transport bridge for Slay the Spire 2.

It starts multiple local STS2 clients, assigns input slots, runs a loopback broker, and bridges the game's native multiplayer traffic between those clients.

## Status

LocalCoop is an Experimental Alpha tested against Slay the Spire 2 v0.103.3 on Windows x64.

Expect rough edges. This release does not promise polished split-screen presentation, full gameplay stability, compatibility with every future STS2 update, or compatibility with arbitrary mod combinations.

## Requirements

- Slay the Spire 2 installed on Windows.
- A copy of the LocalCoop release zip.
- One controller per local client, or one mouse/keyboard controlling all clients.

## Install

1. Download the LocalCoop release zip.
2. Extract it into your Slay the Spire 2 install folder so the mod lands here:

```text
Slay the Spire 2\mods\LocalCoop
```

3. Confirm this file exists:

```text
Slay the Spire 2\mods\LocalCoop\LocalCoop.json
```

## Launch

Open PowerShell in `Slay the Spire 2\mods\LocalCoop` and run:

```powershell
.\Start-LocalCoopClients.ps1 -ClientCount 4
```

For two or three clients, use:

```powershell
.\Start-LocalCoopClients.ps1 -ClientCount 2
.\Start-LocalCoopClients.ps1 -ClientCount 3
```

Convenience launchers are also included:

```text
Start-LocalCoop2Players.bat
Start-LocalCoop3Players.bat
Start-LocalCoop4Players.bat
```

## Input Modes

Controller/mouse cross-play is not supported in this alpha because STS2 does not split gameplay input between mouse and controller users. When any active player uses a controller, STS2 enters controller mode and deactivates or resets mouse control.

This is a game input-system limitation, not just a missing LocalCoop quality-of-life feature. Supporting mixed mouse/controller local clients would require LocalCoop to rewrite how STS2 handles controller and mouse input.

Supported:

- one controller per client;
- one mouse/keyboard controlling all clients.

Untested and unsupported:

- mixing controller and mouse control across different clients;
- multiple simultaneous mice through MouseMux or similar tools.

MouseMux or similar tools may work in theory, but they are not tested or supported by this project.

See [KNOWN_ISSUES.md](KNOWN_ISSUES.md) for the current known bugs and limitations list.

## Logs And Troubleshooting

Packaged launches write generated configs and broker logs under:

```text
%APPDATA%\SlayTheSpire2\LocalCoop
```

If PowerShell blocks the launcher, open PowerShell from the LocalCoop folder and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\Start-LocalCoopClients.ps1 -ClientCount 2
```

Controllers are detected after the developer logo and assigned automatically from Steam Input's stable session inventory. Device numbers, controller brands, and connection order in Godot are not used as player ownership. Every client warms Steam action data in standby, and collector handoff waits for stable focus and usable actions before transferring input.

If controller assignment looks wrong, start with two clients and two controllers:

```powershell
.\Start-LocalCoopClients.ps1 -ClientCount 2
```

To uninstall, delete:

```text
Slay the Spire 2\mods\LocalCoop
%APPDATA%\SlayTheSpire2\LocalCoop
```

## Support

Pull requests and bug reports are welcome. GitHub Issues and Pull Requests are strongly preferred.

Nexus comments may be read, but GitHub is the support source of truth. This is an alpha project with no guarantee of continued feature work. Best-effort compatibility fixes may happen while the maintainer is still actively playing Slay the Spire 2.

Useful bug reports include:

- Slay the Spire 2 version;
- LocalCoop version;
- Windows version;
- player count;
- controller and mouse/keyboard setup;
- reproduction steps;
- logs from `%APPDATA%\SlayTheSpire2\LocalCoop`.

## Credits

LocalCoop is maintained by Bahne. Codex assisted with development.

LocalCoop does not include or license Slay the Spire 2 assets or binaries. You must own and install Slay the Spire 2 separately.
