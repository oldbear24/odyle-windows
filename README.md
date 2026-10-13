# Odyle Windows Companion

A .NET 10 WPF tray app for your **Discord-linked Odyle account**, persistent characters and private PocketBase rooms. The dashboard and backend are in [odyle-dashboard](https://github.com/oldbear24/odyle-dashboard).

## Use it

1. Start/configure the dashboard's backend and sign in on the web with Discord.
2. On **My rooms & characters**, choose **Pair Windows app**, then **Copy connection link**.
3. Click **Paste link & connect** in this app. The link includes the server address and a one-use code that expires after five minutes. You can also paste it into the input and press Enter. An older code-only connection works through the collapsed **Server address** option.
4. Install Npcap from its official site and click **Detect character from game** (or **Start packet reader** for an existing character). Start before logging your character into the game. The own-character message provides its name, server and Global region; the app saves and selects that character automatically. A missing or unfamiliar identity remains unknown. For manual identity setup, click **+ Add**, enter the name/server/region and press **Save character**. This saves the character without entering any Odyle.
5. Select a room and explicitly enable sharing there. You can enter additional room invite codes in the app. Values remain saved when you leave a room or sign out.

The app remembers your connection using Windows DPAPI, never Discord passwords or OAuth tokens. It reconnects when reopened and preserves credentials during temporary server outages; use **Reconnect saved account** to retry. Hosted connections require HTTPS; HTTP is allowed only on localhost.

The window is 400 × 690 with a dark title bar and its own icon. The reading card at the top shows the selected character, its current Odyle, reward cubes at the selected room's cost, and a Live / Stale / Saved state. Saved characters load automatically; character details, room sharing and account options fold away. Adding another character keeps the current server and region to avoid retyping them.

Selectors, expanders and scrolling use the same dark palette. Empty accounts have a direct detection button; manual setup includes an explicit Save action and can be cancelled when another character exists. Invalid form input keeps the draft open and does not reconnect the reader.

![Compact app with an illustrative saved character](docs/compact-preview.png)

![Automatic setup for an illustrative empty account](docs/auto-detect-preview.png)

## Download size

| Package | Approximate ZIP size | Requirements |
| --- | --- | --- |
| `odyle-win-x64.zip` | 200 KB | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) for Windows x64 |
| `odyle-win-x64-self-contained.zip` | 68 MB | Bundles its runtime |

When updating, quit the old app using **Quit** in its window or tray menu; closing the window keeps it running. Extract the new ZIP and run `Odyle.exe`. The small download uses the installed runtime; Microsoft shows the required runtime download dialog if it is missing. Choose **Desktop Runtime**, which includes WPF support. Use the standalone package if you prefer no runtime installation. Sizes refer to this build and may change slightly.

**Readings come from the game only; manual entry is disabled in both clients and the backend.** While the reader runs, every decoded Odyle value is sent to your account under the adapter name `global-reference-v1`, re-confirmed every 20 seconds while game packets keep arriving, and the same pulse updates presence. The dashboard shows a value as live while those confirmations continue and as stale 45 seconds after they stop. Stopping the reader, switching characters, disconnecting or quitting marks the current reading stale while keeping the last value. If the server refuses a reading (for example a backend that does not list the adapter), the card shows **Not saved** with the server's message and the app retries a minute later. Close the window to keep it in the tray; **Quit** exits.

**How the reader works.** It passively decodes incoming IPv4/TCP game traffic owned by the local AION2 process using an existing Npcap installation: TCP reassembly, heartbeat synchronization, bounded framing, LZ4 blocks, the own-character message, the Odyle snapshot and live stat updates. A live EU Global / Lumiel check on Steam build 25800038 read the startup value **0 + 390 = 390**, matching the game display; the live-update path follows the same documented format and should be compared against the in-game number after a spend or recharge. Start the reader **before** logging in: the snapshot is sent once at login, so joining an established connection shows a hint to relog or change channel. No game memory access, packet injection or gameplay automation is implemented. Npcap is installed separately and is not bundled. See [packet reader details](docs/packet-reader.md).

## Build and package

Windows 10/11 with .NET SDK 10:

```powershell
dotnet build src/Odyle.Reader/Odyle.Reader.csproj -c Release
dotnet run --project src/Odyle.Reader
dotnet run --project tests/Odyle.Core.Checks -c Release
dotnet run --project tests/Odyle.Reader.Checks -c Release
./scripts/publish.ps1
./scripts/publish.ps1 -SelfContained
```

Packaging defaults to the small single-file executable and SHA-256 checksum. `-SelfContained` produces the separate standalone ZIP. Use `-Runtime win-arm64` for ARM64. Builds are unsigned. Tag workflows create draft releases; checks build and upload both ZIPs on each push.

The optional end-to-end check accepts a test server URL and **disposable desktop pairing code** as two arguments. Against a disposable backend that approves the synthetic `test-fixture-v1` adapter, it pairs, refreshes the account, saves packet zero, sends presence, invalidates freshness and confirms the value is retained. It does not delete characters or memberships.

Credentials are in `%LOCALAPPDATA%\Odyle\session.bin`. An older guest credential will fail the new account API and must be replaced by pairing from the Discord dashboard.

See [API contract](docs/api.md) and the dashboard's [Discord setup](https://github.com/oldbear24/odyle-dashboard/blob/main/docs/discord-setup.md). Live Discord sign-in requires the operator's provider configuration; it is never replaced by password registration.
