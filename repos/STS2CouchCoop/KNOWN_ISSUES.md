# Known Issues

LocalCoop is an experimental alpha. This list tracks known user-facing issues and limitations for the current Nexus release.

## Controller And Mouse Input Cannot Be Split Between Players

STS2 does not split gameplay input between mouse and controller users. When any active player uses a controller, STS2 enters controller mode and deactivates or resets mouse control.

This is a game input-system limitation, not just a missing LocalCoop quality-of-life feature. Supporting mixed mouse/controller local clients would require LocalCoop to rewrite how STS2 handles controller and mouse input.

Supported input setups:

- one controller per client;
- one mouse/keyboard controlling all clients.

Unsupported input setups:

- one player on mouse/keyboard while another player uses a controller;
- mixed mouse/controller control across different clients;
- multiple simultaneous mice through MouseMux or similar tools.

MouseMux or similar tools may work in theory for multiple mice, but they are untested and unsupported by this project.

## Future STS2 Updates May Break LocalCoop

LocalCoop is tested against Slay the Spire 2 v0.103.3 on Windows x64. STS2 is still changing, and future game updates may break LocalCoop's hooks, launcher assumptions, broker integration, or input behavior.

Compatibility fixes are best effort while the maintainer is still actively playing Slay the Spire 2.

## Mod Conflicts Are Not Fully Mapped

LocalCoop does not guarantee compatibility with arbitrary STS2 mod combinations. If another mod changes startup, controller handling, multiplayer services, lobby flow, or run state, it may conflict with LocalCoop.

Useful bug reports should include the full mod list and logs from:

```text
%APPDATA%\SlayTheSpire2\LocalCoop
```
