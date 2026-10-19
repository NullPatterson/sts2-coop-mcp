# LocalCoop - Experimental Local Co-op Alpha

## Summary

Experimental same-machine local co-op launcher and transport bridge for Slay the Spire 2.

## Description

LocalCoop starts multiple local Slay the Spire 2 clients, assigns input slots, runs a loopback broker, and bridges the game's native multiplayer traffic between those clients.

This is an Experimental Alpha tested against Slay the Spire 2 v0.103.3 on Windows x64. Expect rough edges. This release does not promise polished split-screen presentation, full gameplay stability, compatibility with every future STS2 update, or compatibility with arbitrary mod combinations.

## Requirements

- Slay the Spire 2 for Windows.
- Manual mod installation.
- One controller per client, or one mouse/keyboard controlling all clients.

## Installation

Extract the zip into your Slay the Spire 2 install folder so it lands here:

```text
Slay the Spire 2\mods\LocalCoop
```

Confirm this file exists:

```text
Slay the Spire 2\mods\LocalCoop\LocalCoop.json
```

## Launch

Open PowerShell in `Slay the Spire 2\mods\LocalCoop` and run:

```powershell
.\Start-LocalCoopClients.ps1 -ClientCount 4
```

For fewer clients:

```powershell
.\Start-LocalCoopClients.ps1 -ClientCount 2
.\Start-LocalCoopClients.ps1 -ClientCount 3
```

Convenience batch files are included for 2, 3, and 4 players.

## Known Limitations

- Experimental alpha; full gameplay stability is not guaranteed.
- Controller/mouse cross-play is not supported because STS2 switches into controller mode when any active player uses a controller, which deactivates or resets mouse control.
- One controller per client is supported.
- One mouse/keyboard controlling all clients is supported.
- Multiple simultaneous mice through MouseMux or similar tools are untested and unsupported.
- Future STS2 updates may break compatibility.

Supporting mixed mouse/controller local clients would require LocalCoop to rewrite how STS2 handles controller and mouse input. This is outside the current alpha scope.

See `KNOWN_ISSUES.md` in the repository for the current known bugs and limitations list.

## Troubleshooting

Packaged launches write generated configs and broker logs under:

```text
%APPDATA%\SlayTheSpire2\LocalCoop
```

If PowerShell blocks the launcher, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\Start-LocalCoopClients.ps1 -ClientCount 2
```

## Support

GitHub Issues and Pull Requests are strongly preferred. Nexus comments may be read, but GitHub is the support source of truth.

Useful bug reports include STS2 version, LocalCoop version, Windows version, player count, input setup, reproduction steps, and logs from `%APPDATA%\SlayTheSpire2\LocalCoop`.

## Credits And Permissions

LocalCoop is maintained by Bahne. Codex assisted with development.

LocalCoop source code is MIT licensed. LocalCoop does not include or license Slay the Spire 2 assets or binaries. Users must own and install Slay the Spire 2 separately.

## Changelog

Initial Nexus-ready alpha release for Slay the Spire 2 v0.103.3 on Windows x64.
