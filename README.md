# Odyle Windows Companion

A .NET 10 WPF tray app for your **Discord-linked Odyle account**, persistent characters and private PocketBase rooms. The dashboard and backend are in [odyle-dashboard](https://github.com/oldbear24/odyle-dashboard).

## Use it

1. Start/configure the dashboard's backend and sign in on the web with Discord.
2. On **My rooms & characters**, choose **Pair Windows app**.
3. Enter that server's URL and the one-time pairing code in this app. The code expires after five minutes and can be used once.
4. Select a saved character or add another one. Confirm its current in-game Odyle total; each character keeps its own saved value.
5. Select a room and explicitly enable sharing there. You can enter additional room invite codes in the app. Values remain saved when you leave a room or sign out.

The app stores a PocketBase account token using Windows DPAPI, never Discord passwords or OAuth tokens. Hosted connections require HTTPS; HTTP is allowed only on localhost.

Manual totals, including zero, are supported. A heartbeat every 20 seconds updates presence; readings become stale after 45 seconds. Reconnect/resume and switching characters require a new confirmation for current readiness, while retaining last saved values. Close the window to keep it in the tray; **Quit companion** marks the active reading stale and exits.

**Automatic game reading is unavailable pending Global validation.** There is no Npcap installation, game memory reading, packet modification or gameplay automation.

## Build and package

Windows 10/11 with .NET SDK 10:

```powershell
dotnet build src/Odyle.Reader/Odyle.Reader.csproj -c Release
dotnet run --project src/Odyle.Reader
dotnet run --project tests/Odyle.Core.Checks -c Release
./scripts/publish.ps1
```

Packaging creates `artifacts/odyle-win-x64.zip` with a self-contained executable and SHA-256 checksum. Use `-Runtime win-arm64` for ARM64. Builds are unsigned. Tag workflows create draft releases; checks build and upload the ZIP on each push.

The optional end-to-end check accepts a test server URL and **disposable desktop pairing code** as two arguments. It pairs, refreshes the account, saves confirmed zero, sends presence, invalidates freshness and confirms the value is retained. It does not delete characters or memberships.

Credentials are in `%LOCALAPPDATA%\Odyle\session.bin`. An older guest credential will fail the new account API and must be replaced by pairing from the Discord dashboard.

See [API contract](docs/api.md) and the dashboard's [Discord setup](https://github.com/oldbear24/odyle-dashboard/blob/main/docs/discord-setup.md). Live Discord sign-in requires the operator's provider configuration; it is never replaced by password registration.
