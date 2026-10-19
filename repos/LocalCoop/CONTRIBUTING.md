# Contributing

Pull requests are welcome.

LocalCoop is a transport bridge around Slay the Spire 2. STS2 should own lobby logic, player identity, character choice, ready state, run start, gameplay state, and message semantics wherever possible.

Prefer narrow transport, lifecycle, factory, launcher, or adapter changes that preserve native STS2 behavior.

Avoid synthetic lobby truth, manual state replay, UI reconstruction, timing heuristics, or gameplay reconciliation unless game-file analysis, runtime logs, and focused tests prove that a transport-level bridge cannot solve the issue.

## Before Opening A Pull Request

- Run `dotnet test LocalCoopTransport.sln --no-restore -p:OutputPath=bin\Debug\net9.0-test\` when dependencies are already restored.
- Run `.\Build-LocalCoopRelease.ps1 -GameRoot '..' -Version 0.1.0 -OutputRoot artifacts\release` when changing packaging or public docs.
- Do not include Slay the Spire 2 binaries, PCK files, logs, generated client folders, local config toggles, or generated release zips.
- Include focused tests for protocol, broker, launcher, release-script, and packaging changes when practical.
