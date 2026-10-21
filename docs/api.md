# Odyle API · what the Windows app uses

The backend contract lives in the dashboard repository: [odyle-dashboard docs/api.md](https://github.com/oldbear24/odyle-dashboard/blob/main/docs/api.md). This page covers every route and field the Windows companion relies on, how it reacts to each answer, and summarizes the rest.

PocketBase 0.40.5. JSON requests; authenticated requests use a PocketBase **users** token (`Authorization: Bearer <token>`). Hosted connections require HTTPS; HTTP is accepted only on localhost. No superuser credentials belong in either client. The app's requests time out after 12 seconds.

## Answers and errors

Every answer that is not 2xx becomes an error carrying its status code. The message is the body's `message` when the body is JSON with a text `message` (PocketBase and the Odyle routes answer `{ message, data }`), otherwise *Server returned &lt;code&gt;.*; a success answer whose body is empty, `null` or not JSON is an error with its status code as well. The server's English messages are shown in Czech too when the app runs in Czech (the same translations as the dashboard).

| Answer | What the app does |
| --- | --- |
| 401 | The token expired or was replaced: a paired app signs in with its device key (`desktop-sign-in`) and repeats the request once. A 401 from that sign-in means the server no longer knows this PC: the pairing has ended, every further request fails at once without contacting the server, the saved session is removed and the app asks for a new connection link |
| 400 | A refused reading is shown as **Not saved** with the message and retried after a minute. *This packet adapter has not been validated for Global. No reading was saved.* means the backend does not list this build's adapter: the app checks GitHub for its update at once (then at most hourly), the card says **Odyle needs an update to keep reading** and the reading is tried again every ten minutes |
| 403 | Never a lost pairing. *Publisher session expired. Reconnect.* starts a new reader session at the next heartbeat; a refused reading is shown as **Not saved** and retried after a minute; at start-up a 403 is retried like an unavailable server |
| 429 | Every request pauses until `Retry-After` (seconds or an HTTP date; a minute when missing or unusable, at most an hour) without reaching the server; the reader session is kept, so the reading stays live. The footer shows **Server busy · retrying in …** |
| 5xx, timeout, offline | While running: retried with the next 20-second heartbeat, which opens a new reader session. At start-up the saved pairing is kept and tried again after 15 s, 30 s, 1 min, 2 min and then every 5 minutes (**Reconnect saved account** tries at once) |
| 503 *Packet readers are paused by the server operators.* | Only `POST /api/odyle/report` answers this, while the operators pause packet readers (`readerPaused`, below). The app stops capturing and closes Npcap, the card says **Paused by the server**, and the reader starts again by itself once a rooms, session or heartbeat answer says `readerPaused: false`. Any other 503 is handled like a 5xx |

## Pairing and sign-in

The dashboard's **Pair Windows app** dialog shows a connection link such as `https://your-backend.example/#odyle-pair=ABCDEFGH23456789` (from `POST /api/odyle/desktop-code`). The app takes the endpoint and the 16-character code from it; the fragment is never sent in an HTTP request. Base-path deployments are preserved; insecure remote HTTP, embedded credentials, query strings and room invite codes are refused. A code alone works with the endpoint from **Server address**.

Every pairing request names the PC: `name` is its Windows computer name, trimmed and cut to 40 characters. The dashboard lists paired PCs by that name (`devices` in `GET /api/odyle/account`) and can remove one. Backends that do not know `name` ignore it.

| Method / route | Body | Answer and use |
| --- | --- | --- |
| POST `/api/odyle/pair` | `{ code, name }` | Public, rate limited: `{ token, record, deviceKey }`; `record` is `{ id, displayName }`. The code is consumed. The app keeps endpoint, token and device key in `%LOCALAPPDATA%\Odyle\session.bin`, protected with DPAPI |
| POST `/api/odyle/desktop-sign-in` | `{ deviceKey, name }` | Public: `{ token, record }`; 401 *This PC is no longer paired with an account.* for an unknown key. Called at start-up, every 24 hours while running and after a 401 |
| POST `/api/odyle/desktop-device` | `{ name }` | Signed in: `{ deviceKey }`. Called once by an app paired before device keys existed; 404 on a backend without device keys, which then keeps refreshing the token |
| POST `/api/odyle/unpair` | `{ deviceKey }` | Public: forgets the key and ends its reader sessions (**Disconnect**, before pairing again, and `Odyle.exe --unpair`, which the uninstaller runs when asked to disconnect the PC; installs updated in place from an earlier setup keep an uninstaller without that question) |
| POST `/api/collections/users/auth-refresh` | `{}` | Refreshes a still-valid token; used only by apps without a device key |

Every sign-in answer refreshes the account's display name shown in the app. Account tokens last seven days; the device key keeps the pairing until the app disconnects, the PC is removed on the dashboard (`POST /api/odyle/forget-device`), the account signs out everywhere or is deleted. An account keeps its 10 most recently used pairings.

**A PC removed on the dashboard.** The backend deletes that PC's reader sessions, so the app's next heartbeat or reading answers 403 *Publisher session expired. Reconnect.*. The app then opens a new session with its device key, which answers 401 *This PC is no longer paired with an account.*; the one sign-in attempt with the key answers 401 too, and the app ends the pairing as above, within about a minute and without retrying.

## Reader sessions and readings

| Method / route | Body | Answer and use |
| --- | --- | --- |
| POST `/api/odyle/session` | `{ previousSessionId?, deviceKey?, appVersion? }` | `{ sessionId, sequence: 0, readerPaused }`. `previousSessionId` rotates the app's own session and invalidates its last readings. A paired app sends its `deviceKey`, which ties the session to the PC; a key the server no longer knows answers 401. `appVersion` (for example `0.6.0.12`) shows on the server's superuser monitor. Absent fields are left out of the body |
| POST `/api/odyle/character` | `{ characterName, server, region }` | `{ character }`: saves the character the game names (unknown Odyle until the first reading); repeating an identity returns the same record. Any other field is refused. Used for automatic game identification only: the app has no manual character entry |
| POST `/api/odyle/report` | see below | `{ character, serverNow }`; 503 while the operators pause packet readers |
| POST `/api/odyle/heartbeat` | `{ sessionId, characterId }` | `{ serverNow, readerPaused }`. Every 20 seconds: keeps the session's latest reading live; `characterId` is `null` without a live reading. The heartbeat keeps running while readers are paused |
| POST `/api/odyle/invalidate` | `{ characterId, sessionId }` | Marks the reading stale (reader stopped, character switched, 20 seconds without decodable game packets, disconnect, quit); value and timestamps are kept |
| GET `/api/collections/characters/records?perPage=500&sort=created` | — | The account's own characters (`id`, `user`, `characterName`, `server`, `region`, `value`, `confirmed`, `valid`, `lastReadingAt`, `lastSeenAt`, `source`) |

```json
{
  "sessionId": "publisher-session-id",
  "sequence": 1,
  "characterId": "character-id",
  "characterName": "Your character",
  "server": "Lumiel",
  "region": "GLOBAL_EU",
  "value": 390,
  "parserVersion": "global-reference-v2",
  "source": "packet",
  "confirmed": true,
  "base": 0,
  "bonus": 390,
  "kind": "snapshot",
  "delta": null
}
```

`base` is the natural pool (it regenerates up to a cap) and `bonus` what items added; they add up to `value`. `kind` says what changed since the reading the app saved before it on this live streak: `snapshot` (the game sent the whole value: login, channel change), `update` (no reading before it), `regen` (the natural pool alone rose: the game's three-hourly tick), `spend` (the total fell) or `item` (anything else that raised it), and `delta` the change of the total (`null` for a snapshot or the first update). All four are optional: backends from before them ignore them, and the total in `value` is what every backend stores.

**Reader pause.** `readerPaused` in the rooms, session and heartbeat answers is the superusers' **Server controls → Pause packet readers** switch (`odyle_settings.readerPaused`). While it is `true` the app stops capturing and closes Npcap, says so on the card and keeps its heartbeat and invalidations; a reading sent meanwhile answers 503 with *Packet readers are paused by the server operators.*, which the app treats the same. The first answer with `false` starts the reader again by itself, unless it was stopped by hand. Backends from before the switch leave the field out, which changes nothing; a new connection without it counts as not paused.

An existing `characterId` must belong to the account and keeps its saved identity. `value` is an integer 0–1,000,000, `sequence` grows per session and `parserVersion` must be listed in the backend's adapter registry: this app sends `global-reference-v2`, and backends keep accepting `global-reference-v1` from apps not yet updated. The backend must list a new adapter name before the Windows release that sends it is pushed. The app sends a value the first time it reads a character and afterwards only when it changes; the heartbeat keeps an unchanged value live. Regions: `GLOBAL_EU`, `GLOBAL_NA`, `GLOBAL_SA`, `GLOBAL_ASIA`. Presence follows the dashboard's rule: live while the heartbeat is younger than 45 seconds, stale after that or once invalidated, offline after 90 seconds (`Odyle.Core.Readiness`).

## Rooms, party and planned runs

The next-run chip's **Going ✓** and **Join** send `POST /api/odyle/plan` with `{ roomId, id, going }` (the run's id; `going` false to leave). Viewers get no button; the server refuses them anyway.

| Method / route | Answer and use |
| --- | --- |
| GET `/api/odyle/rooms` | `{ rooms, odylePerCube, announcement, readerPaused, clock, serverNow }`; `clock` is the Odyle clock's rules `{ tickHours, tick, caps, paused }` (the defaults when an older backend leaves it out), and characters carry `parts`, `regenTick` and `naturalMax`, from which the card projects the natural pool; `announcement` is the server's message `{ text, level }` (`info` or `warning`) or `null`, shown above the card (inside it while readers are paused); `readerPaused` is the reader pause above; each room has `id`, `name`, `cost`, `role`, `sharing` and `sharedCharacters` (the character ids this account shares there). Settings summarizes where each character is shared; the card's cube cost is the first sharing room's |
| GET `/api/odyle/snapshot/{id}` | `{ room, members, wishlist, runs, dungeons, serverNow }`; members have `id`, `user`, `role`, `displayName` and `characters`. The app lists other members' live and stale characters, works out each room's party (`Odyle.Core.PartyReadiness`, the dashboard's rules: characters with `shared`, else the member's `sharing`, else, in snapshots without either, shared), corrects its clock with `serverNow` and reads `runs` for reminders and the next-run chip; it ignores `wishlist` and `dungeons` |

The app reads the rooms and their snapshots when it connects and listens to realtime news (below); it reads them again on news it cannot apply in place, when the realtime connection comes back and every five minutes. Without a realtime connection it reads them every 15 seconds while its window is open and, while run reminders are on (they are by default), every minute while it waits in the tray. Sharing, rooms, invitations, the wishlist and planning are managed on the dashboard.

A snapshot's `runs` list the room's planned runs until three hours after they started:

```json
[{ "id": "run-id", "dungeon": "Fire Temple", "startsAt": "2026-10-14 19:00:00.000Z", "note": "Bring potions", "planner": "user-id-1", "attendees": ["user-id-1", "user-id-2"] }]
```

`planner` and `attendees` are account ids. For each run whose `attendees` contain the signed-in account, the app shows a notification at the account's reminder times before `startsAt` (dungeon, room name, local start time, note), once per run id, start time and reminder time, so a re-timed run is announced again (`Odyle.Core.Reminders`). The reminder times, the tray switch and the quiet hours come from `notifications` in `GET /api/odyle/rooms` (`{ channels: { tray, browser, push }, leadMinutes, kinds: { runs, cap }, quiet: { from, to, timeZone }, saved }`) and from `{ notifications }` news on the account's topic; without them (older backends) the app reminds 15 minutes ahead unless its old local switch is off. `POST /api/odyle/notifications` saves them as a whole; the app calls it once, to move that switch. `{ capAlerts: [{ characterId, characterName, state, base, cap, settled, tick, at? }] }` on the account's topic are the server's cap alerts (`Odyle.Core.CapAlert`): shown in the tray when `channels.tray` and `kinds.cap` are on. Newer backends also offer `GET /api/odyle/upcoming`; the app reads the snapshots instead, which every backend version provides.

## Discord status

With **Settings → Window → Show my Odyle in my Discord status** on, the app reads PocketBase's public `GET /api/collections/users/auth-methods` once per connection (again after ten minutes when it failed) and takes the `client_id` of the `discord` provider's `authURL`: the Discord application the server signs in with, which Discord then names in the status. A server without Discord sign-in, or an id that is no Discord snowflake, gives no status. Everything else happens on this PC, over the Discord app's local pipe (`\\.\pipe\discord-ipc-N`): no other request reaches the server or Discord's servers (`Odyle.Core.DiscordPresence`, `Odyle.Reader.DiscordStatus`).

## Daily and weekly tasks

While the checklist panel is open (on news of the account, or every 15 seconds without realtime), the app reads `GET /api/odyle/tasks` → `{ tasks, resets, serverNow }` and ticks a task with `POST /api/odyle/task` `{ id, done }`, which answers with the same object. A task has `id`, `user`, `title`, `cadence` (`daily`/`weekly`) and `doneAt`; `resets` is `{ dailyResetHour, weeklyResetDay }`. A task counts as done while `doneAt` is on or after the start of its current period (`Odyle.Core.Checklist`, mirroring the dashboard's `src/tasks.js`). Adding, renaming, ordering and removing tasks stay on the dashboard.

## Routes only the dashboard uses

Room management (`create`, `join`, `sharing`, `wishlist`, `plan`, `invites`, `revoke`, `leave`, `close`, `rename`, `role`, `transfer`, `discord-webhook`), Odyle history (`GET /api/odyle/history/{characterId}`), run reminders across rooms (`GET /api/odyle/upcoming`), the account page (`GET /api/odyle/account`, `profile`, `export`, `delete-character`, `forget-device`, `sign-out-everywhere`, `delete-account`), the Windows release lookup (`GET /api/odyle/windows-release`), the superuser monitor and settings (`/api/odyle/admin/...`) are described in the [dashboard's API document](https://github.com/oldbear24/odyle-dashboard/blob/main/docs/api.md).

## Realtime

`Odyle.Core.RealtimeClient` keeps one connection to PocketBase's server-sent events at `GET /api/realtime`. On its `PB_CONNECT` event it subscribes with `POST /api/realtime` `{ clientId, subscriptions }` and the account token (a 401 signs in with the device key and repeats once, like every request) to the account's topic `odyle.user.<accountId>` and, for each room, `odyle.room.<roomId>` and `odyle.presence.<roomId>`; a room the account left is refused (403), and the app then listens to its own topic until it has read the rooms again. After a drop it connects again after 1, 2, 5, 10 and then every 30 seconds, and a connection silent for six minutes (the server closes idle ones after five) counts as dropped. `Odyle.Core.PartyNews` applies the news: a room's changed `characters` and its `seen` heartbeats change the party list in place; a room's other `refresh`, a `removed` member, a character the list does not show yet and the account's own `refresh` read the rooms (and the account's characters and checklist) again. The topics and messages are described in the [dashboard's realtime section](https://github.com/oldbear24/odyle-dashboard/blob/main/docs/api.md#realtime).

The account's topic also carries news for everyone: `{ release: { version, publishedAt, url } }` when the backend sees a newer Windows release, which makes the app run its update check at once (still against GitHub, verified as below; a release no newer than the running build is ignored), and `{ announcement: { text, level } | null }` when the operators changed the announcement, which the app shows at once and, for a new warning, also as a notification.

## Updates from GitHub

The app's updater does not use the backend for packages: it reads `https://api.github.com/repos/oldbear24/odyle-windows/releases/latest` and downloads only from `https://github.com/` release storage. Packages are named `odyle-<version>-<win-x64|win-arm64>[-self-contained].zip`, each with a `<name>.sha256` file (`<hash>  <name>`); a release also carries `odyle-<version>-win-x64-setup.exe` with its `.sha256`, which the updater never selects. Drafts, pre-releases and tags that are not versions are ignored (`Odyle.Core.ReleaseFeed`).
