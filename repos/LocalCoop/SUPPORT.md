# Support

LocalCoop is an experimental alpha.

Pull requests and bug reports are welcome. GitHub Issues and Pull Requests are strongly preferred. Nexus comments may be read, but GitHub is the support source of truth.

There is no guarantee of continued feature work. Best-effort compatibility fixes may happen while the maintainer is still actively playing Slay the Spire 2.

## Useful Bug Reports

Please include:

- Slay the Spire 2 version;
- LocalCoop version;
- Windows version;
- player count;
- controller and mouse/keyboard setup;
- reproduction steps;
- logs from `%APPDATA%\SlayTheSpire2\LocalCoop`.

## Known Unsupported Areas

- Controller/mouse cross-play. STS2 switches into controller mode when any active player uses a controller, which deactivates or resets mouse control. Supporting mixed mouse/controller local clients would require rewriting how STS2 handles controller and mouse input.
- Multiple simultaneous mice through MouseMux or similar tools.
- Arbitrary mod combinations.
- Future STS2 versions that have not been tested with LocalCoop.
