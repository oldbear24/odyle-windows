# Odyle Windows Companion

A .NET 10 WPF tray app for your **Discord-linked Odyle account**, persistent characters and private PocketBase rooms. The dashboard and backend are in [odyle-dashboard](https://github.com/oldbear24/odyle-dashboard).

## Use it

1. Start/configure the dashboard's backend and sign in on the web with Discord.
2. On **My rooms & characters**, choose **Pair Windows app**, then **Copy connection link**.
3. Click **Paste link & connect** in this app. The link includes the server address and a one-use code that expires after five minutes. You can also paste it into the input and press Enter. An older code-only connection works through the collapsed **Server address** option.
4. Select a saved character or add another one. Confirm its current in-game Odyle total; each character keeps its own saved value.
5. Select a room and explicitly enable sharing there. You can enter additional room invite codes in the app. Values remain saved when you leave a room or sign out.

The app remembers your connection using Windows DPAPI, never Discord passwords or OAuth tokens. It reconnects when reopened and preserves credentials during temporary server outages; use **Reconnect saved account** to retry. Hosted connections require HTTPS; HTTP is allowed only on localhost.

The window is now 380 × 610. Saved characters load automatically; character details, room sharing and account options fold away. Adding another character keeps the current server and region to avoid retyping them.

![Compact app with illustrative values](docs/compact-preview.png)

## Download size

| Package | Approximate ZIP size | Requirements |
| --- | --- | --- |
| `odyle-win-x64.zip` | 110 KB | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) for Windows x64 |
| `odyle-win-x64-self-contained.zip` | 68 MB | Bundles its runtime |

Extract the ZIP and run `Odyle.exe`. The small download uses the installed runtime; Microsoft shows the required runtime download dialog if it is missing. Choose **Desktop Runtime**, which includes WPF support. Use the standalone package if you prefer no runtime installation. Sizes refer to this build and may change slightly.

Manual totals, including zero, are supported. A heartbeat every 20 seconds updates presence; readings become stale after 45 seconds. Reconnect/resume and switching characters require a new confirmation for current readiness, while retaining last saved values. Close the window to keep it in the tray; **Quit companion** marks the active reading stale and exits.

**Automatic game reading is unavailable pending Global validation.** There is no Npcap installation, game memory reading, packet modification or gameplay automation.

## Build and package

Windows 10/11 with .NET SDK 10:

```powershell
dotnet build src/Odyle.Reader/Odyle.Reader.csproj -c Release
dotnet run --project src/Odyle.Reader
dotnet run --project tests/Odyle.Core.Checks -c Release
./scripts/publish.ps1
./scripts/publish.ps1 -SelfContained
```

Packaging defaults to the small single-file executable and SHA-256 checksum. `-SelfContained` produces the separate standalone ZIP. Use `-Runtime win-arm64` for ARM64. Builds are unsigned. Tag workflows create draft releases; checks build and upload both ZIPs on each push.

The optional end-to-end check accepts a test server URL and **disposable desktop pairing code** as two arguments. It pairs, refreshes the account, saves confirmed zero, sends presence, invalidates freshness and confirms the value is retained. It does not delete characters or memberships.

Credentials are in `%LOCALAPPDATA%\Odyle\session.bin`. An older guest credential will fail the new account API and must be replaced by pairing from the Discord dashboard.

See [API contract](docs/api.md) and the dashboard's [Discord setup](https://github.com/oldbear24/odyle-dashboard/blob/main/docs/discord-setup.md). Live Discord sign-in requires the operator's provider configuration; it is never replaced by password registration.
