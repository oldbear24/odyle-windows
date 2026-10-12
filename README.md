# Odyle Windows Companion

A .NET 10 WPF tray app that shares your own confirmed Odyle Energy with a private PocketBase party room. The web UI, schema and server hooks live in [odyle-dashboard](https://github.com/oldbear24/odyle-dashboard).

## Current features

- Join with a **player invite** from the dashboard; viewer codes are rejected.
- Explicit consent, manual energy confirmation, character/server and Global region selection.
- Genuine zero is supported. No reading appears as a dash.
- Presence heartbeat every 20 seconds. Readings become stale after 45 seconds; heartbeats do not refresh them.
- Reset confidence after reconnect/resume; never upload an old number automatically.
- Switch character clears the previous reading before changing identity.
- Close the window to keep it in the tray; quit stops sharing. Leave revokes your credential.
- Tokens persisted with Windows DPAPI, scoped to the current OS user. Hosted connections require HTTPS.

**Automatic capture is unavailable.** This MVP does not install Npcap, inspect game traffic/memory, modify AION 2, or automate gameplay. A validated Global reading adapter can be added later behind explicit opt-in.

## Build and run

Windows 10/11, .NET SDK 10:

```powershell
dotnet build src/Odyle.Reader/Odyle.Reader.csproj -c Release
dotnet run --project src/Odyle.Reader
```

Start the dashboard's PocketBase server, create a room in its web UI, then enter `http://127.0.0.1:8090` and the player invite in the app. For a hosted server use its HTTPS URL. Choose your character/server, enable sharing, read **Odyle Energy** in the game, and confirm the number.

Local credentials live in `%LOCALAPPDATA%\Odyle\session.bin` encrypted by DPAPI. They cannot be moved to another OS user as plaintext configuration. Starting the app restores a valid room session with sharing off and requires a new reading.

## Verify and package

```powershell
dotnet run --project tests/Odyle.Core.Checks -c Release
./scripts/publish.ps1
```

The script creates `artifacts/odyle-win-x64.zip` with a self-contained executable and SHA-256 checksum. Use `-Runtime win-arm64` for ARM64. Initial releases are unsigned; the build does not claim publisher signing. Tagged GitHub builds create a draft release for review.

The contract check can optionally exercise a running PocketBase backend with two arguments: server URL and a disposable test player invite. It joins, resets, consents, reports zero, sends a heartbeat, invalidates and leaves. Never supply a leader or superuser token.

## Structure

```text
src/Odyle.Core/           PocketBase REST client and wire contracts
src/Odyle.Reader/         WPF interface, tray lifecycle and DPAPI storage
tests/Odyle.Core.Checks/  contract/security checks and optional API smoke check
scripts/publish.ps1       self-contained Windows release packaging
```

See [API contract](docs/api.md) for backend compatibility. The source was built on Windows with .NET 10.0.400. Actual game packet integration is outside this release.
