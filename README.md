# Odyle Windows Companion

A .NET 10 WPF tray app for your **Discord-linked Odyle account**, persistent characters and private PocketBase rooms. The dashboard and backend are in [odyle-dashboard](https://github.com/oldbear24/odyle-dashboard).

## Use it

1. Start/configure the dashboard's backend and sign in on the web with Discord.
2. Download the newest release: **My rooms & characters** links it as **Windows app &lt;version&gt;**, and the **Pair Windows app** dialog offers both packages (see [Download and updates](#download-and-updates)). Extract the ZIP into a folder you can write to and run `Odyle.exe`. From then on the app updates itself.
3. On **My rooms & characters**, choose **Pair Windows app**, then **Copy connection link**.
4. Open **Settings** (⚙) and click **Paste link & connect**. Settings opens by itself until the app is paired. The link includes the server address and a one-use code that expires after five minutes. You can also paste it into the input and press Enter. An older code-only connection works through the collapsed **Server address** option.
5. Install Npcap from its official site. The reader starts by itself once the app is connected to your account (**Stop**/**Start** beside the reader status toggles it). Keep Odyle running before logging your character into the game when you can: the login snapshot names the character at once. Started later, the app reads the total at the next Odyle change and asks for one relog or channel change so the game names the character. The character always comes from the game: its own-character message provides the name, server and Global region, and the app saves and shows that character automatically, following any in-game switch. There is no character picker or manual character entry; a character on a server the app cannot map yet is shown but not saved.
6. Rooms and sharing are set on the dashboard: under **My rooms & characters → Room sharing**, switch on each room that should see each character (**Manage rooms & sharing ↗** in Settings opens it). Sharing is per character: a new character starts private. Create and join rooms there too. The app picks up changes by itself. Values remain saved when you leave a room or sign out.

The app remembers your connection using Windows DPAPI, never Discord passwords or OAuth tokens. It reconnects when reopened and preserves credentials during temporary server outages; use **Reconnect saved account** to retry. Hosted connections require HTTPS; HTTP is allowed only on localhost.

The main window is a compact 280 px card with a dark title bar and its own icon: the character the game reports, its current Odyle, reward cubes at the cost of the first room it is shared in, a Live / Stale / Saved state, the reader status and **Start/Stop reader**. Below it, **Online in your rooms** lists the other members' characters each of your rooms can see while their readers report them: Odyle, cubes at that room's cost, and an amber dot once a reading has gone stale. Offline characters and your own are left out. The list refreshes every 15 seconds while the window is open. The separate **Settings** window (⚙ or the tray menu) holds only what belongs to this PC: pairing and server address, account and disconnect, a read-only summary of where each character is shared, Npcap setup, and the installed version with its updates. Everything else is configured on the dashboard. Closing either window keeps the app in the tray.

![Compact app with an illustrative character](docs/compact-preview.png)

![Settings with the room sharing summary](docs/settings-preview.png)

## Download and updates

Every push to `main` publishes a [GitHub release](https://github.com/oldbear24/odyle-windows/releases/latest) with two packages; the dashboard links the newest one on **My rooms & characters** and in its **Pair Windows app** dialog (its backend reads the release list from GitHub for it).

| Package | Approximate ZIP size | Requirements |
| --- | --- | --- |
| `odyle-<version>-win-x64.zip` | 200 KB | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) for Windows x64 |
| `odyle-<version>-win-x64-self-contained.zip` | 68 MB | Bundles its runtime |

Extract the ZIP into a folder you can write to (your own folder, not Program Files) and run `Odyle.exe`. The small download uses the installed runtime; Microsoft shows the required runtime download dialog if it is missing. Choose **Desktop Runtime**, which includes WPF support. Use the standalone package if you prefer no runtime installation. Sizes refer to this build and may change slightly.

**The app updates itself.** A released build (version `<Version>.<run number>`, for example 0.5.0.12) checks this repository's GitHub releases about 20 seconds after it starts and every six hours; **Settings → Updates** shows the installed version with **Check now** and a link to the releases. When a newer release has a package of the same kind (small or self-contained) for this CPU, the app downloads it to `%LOCALAPPDATA%\Odyle\updates\<version>`, verifies the ZIP against the release's `.sha256` file, extracts it and checks that the extracted `Odyle.exe` carries that version. The footer and Settings then offer **Restart to update**; left alone, the downloaded version installs by itself the next time Odyle starts. Installing moves the running `Odyle.exe` aside as `Odyle.exe.old` (Windows lets a running program be renamed but not overwritten), copies the new files into the same folder and starts the new version, which deletes the old file. If the folder is not writable, Settings says so and the release page remains the way to update. Builds are unsigned: the checksum guards against a damaged download, not against a compromised release. Development builds (`dotnet run`, version without a run number) never replace themselves. For updates the app contacts `api.github.com` and `github.com` only, sending a standard request that names the app version; nothing about your account or characters is included.

**Readings come from the game only; manual entry is disabled in both clients and the backend.** While the reader runs, Odyle is sent to your account under the adapter name `global-reference-v1` when a character's value is first read and afterwards only when it changes. A 20-second heartbeat keeps the value live while game packets keep arriving; the dashboard shows it as stale 45 seconds after the heartbeat stops. Each reading is saved to the character the game names: after an in-game character switch the new snapshot waits for the new character's identity, so it never overwrites the previous character. Stopping the reader, switching characters, disconnecting or quitting marks the current reading stale while keeping the last value. If the server refuses a reading (for example a backend that does not list the adapter), the card shows **Not saved** with the server's message and the app retries a minute later. Close the window to keep it in the tray; **Quit** exits.

**How the reader works.** It passively decodes incoming IPv4/TCP game traffic owned by the local AION2 process using an existing Npcap installation: TCP reassembly, heartbeat synchronization, bounded framing, LZ4 blocks, the own-character message, the Odyle snapshot and live stat updates. A live EU Global / Lumiel check on Steam build 25800038 read the startup value **0 + 390 = 390**, matching the game display; the live-update path follows the same documented format and should be compared against the in-game number after a spend or recharge. Starting the reader **before** logging in is best: the login snapshot names the character and both Odyle components at once. Joining an established connection reads the total from the next live Odyle change and shows a hint to relog or change channel once so the game names the character. No game memory access, packet injection or gameplay automation is implemented. Npcap is installed separately and is not bundled. See [packet reader details](docs/packet-reader.md).

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

Packaging defaults to the small single-file executable and SHA-256 checksum. `-SelfContained` produces the separate standalone ZIP and stamps `SelfContained` into the assembly metadata, which the updater reads to fetch the same package kind. Use `-Runtime win-arm64` for ARM64. Builds are unsigned. `-Version` stamps a version into the executable and the ZIP names; a build with a four-part version is a release build and updates itself.

Every push to `main` runs the checks and publishes the GitHub release `v<Version>.<run number>` (for example `v0.5.0.12`) with both ZIPs and their checksums; installed apps pick it up from there, so keep the package names and the `.sha256` files as they are. `<Version>` comes from `Odyle.Reader.csproj`; bump it to start a new line. Other branches and pull requests run the checks and upload both ZIPs as workflow artifacts.

`dotnet run --project tests/Odyle.Reader.Checks -c Release -- --update-probe` downloads the newest release's package for this machine into a temporary folder and verifies it the way the app does (needs the network).

The optional end-to-end check accepts a test server URL and **disposable desktop pairing code** as two arguments. Against a disposable backend that approves the synthetic `test-fixture-v1` adapter, it pairs, refreshes the account, saves packet zero, sends presence, invalidates freshness and confirms the value is retained. It does not delete characters or memberships.

Credentials are in `%LOCALAPPDATA%\Odyle\session.bin`. An older guest credential will fail the new account API and must be replaced by pairing from the Discord dashboard.

See [API contract](docs/api.md) and the dashboard's [Discord setup](https://github.com/oldbear24/odyle-dashboard/blob/main/docs/discord-setup.md). Live Discord sign-in requires the operator's provider configuration; it is never replaced by password registration.
