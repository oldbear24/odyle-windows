# Odyle API · what the Windows app uses

The backend contract lives in the dashboard repository: [odyle-dashboard docs/api.md](https://github.com/oldbear24/odyle-dashboard/blob/main/docs/api.md). This page covers every route and field the Windows companion relies on, how it reacts to each answer, and summarizes the rest.

PocketBase 0.40.5. JSON requests; authenticated requests use a PocketBase **users** token (`Authorization: Bearer <token>`). Hosted connections require HTTPS; HTTP is accepted only on localhost. No superuser credentials belong in either client. The app's requests time out after 12 seconds.

## Answers and errors

Every answer that is not 2xx becomes an error carrying its status code. The message is the body's `message` when the body is JSON with a text `message` (PocketBase and the Odyle routes answer `{ message, data }`), otherwise *Server returned &lt;code&gt;.*; a success answer whose body is empty, `null` or not JSON is an error with its status code as well. The server's English messages are shown in Czech too when the app runs in Czech (the same translations as the dashboard).

| Answer | What the app does |
| --- | --- |
| 401 | The token expired or was replaced: a paired app signs in with its device key (`desktop-sign-in`) and repeats the request once. A 401 from that sign-in means the server no longer knows this PC: the pairing has ended, every further request fails at once without contacting the server, the saved session is removed and the app asks for a new connection link |
| 403 | Never a lost pairing. *Publisher session expired. Reconnect.* starts a new reader session at the next heartbeat; a refused reading is shown as **Not saved** and retried after a minute; at start-up a 403 is retried like an unavailable server |
| 429 | Every request pauses until `Retry-After` (seconds or an HTTP date; a minute when missing or unusable, at most an hour) without reaching the server; the reader session is kept, so the reading stays live. The footer shows **Server busy · retrying in …** |
| 5xx, timeout, offline | While running: retried with the next 20-second heartbeat, which opens a new reader session. At start-up the saved pairing is kept and tried again after 15 s, 30 s, 1 min, 2 min and then every 5 minutes (**Reconnect saved account** tries at once) |

## Pairing and sign-in

The dashboard's **Pair Windows app** dialog shows a connection link such as `https://your-backend.example/#odyle-pair=ABCDEFGH23456789` (from `POST /api/odyle/desktop-code`). The app takes the endpoint and the 16-character code from it; the fragment is never sent in an HTTP request. Base-path deployments are preserved; insecure remote HTTP, embedded credentials, query strings and room invite codes are refused. A code alone works with the endpoint from **Server address**.

Every pairing request names the PC: `name` is its Windows computer name, trimmed and cut to 40 characters. The dashboard lists paired PCs by that name (`devices` in `GET /api/odyle/account`) and can remove one. Backends that do not know `name` ignore it.

| Method / route | Body | Answer and use |
| --- | --- | --- |
| POST `/api/odyle/pair` | `{ code, name }` | Public, rate limited: `{ token, record, deviceKey }`; `record` is `{ id, displayName }`. The code is consumed. The app keeps endpoint, token and device key in `%LOCALAPPDATA%\Odyle\session.bin`, protected with DPAPI |
| POST `/api/odyle/desktop-sign-in` | `{ deviceKey, name }` | Public: `{ token, record }`; 401 *This PC is no longer paired with an account.* for an unknown key. Called at start-up, every 24 hours while running and after a 401 |
| POST `/api/odyle/desktop-device` | `{ name }` | Signed in: `{ deviceKey }`. Called once by an app paired before device keys existed; 404 on a backend without device keys, which then keeps refreshing the token |
| POST `/api/odyle/unpair` | `{ deviceKey }` | Public: forgets the key and ends its reader sessions (**Disconnect**, and before pairing again) |
| POST `/api/collections/users/auth-refresh` | `{}` | Refreshes a still-valid token; used only by apps without a device key |

Every sign-in answer refreshes the account's display name shown in the app. Account tokens last seven days; the device key keeps the pairing until the app disconnects, the PC is removed on the dashboard (`POST /api/odyle/forget-device`), the account signs out everywhere or is deleted. An account keeps its 10 most recently used pairings.

**A PC removed on the dashboard.** The backend deletes that PC's reader sessions, so the app's next heartbeat or reading answers 403 *Publisher session expired. Reconnect.*. The app then opens a new session with its device key, which answers 401 *This PC is no longer paired with an account.*; the one sign-in attempt with the key answers 401 too, and the app ends the pairing as above, within about a minute and without retrying.

## Reader sessions and readings

| Method / route | Body | Answer and use |
| --- | --- | --- |
| POST `/api/odyle/session` | `{ previousSessionId?, deviceKey? }` | `{ sessionId, sequence: 0 }`. `previousSessionId` rotates the app's own session and invalidates its last readings. A paired app sends its `deviceKey`, which ties the session to the PC; a key the server no longer knows answers 401. Absent fields are left out of the body |
| POST `/api/odyle/character` | `{ characterName, server, region }` | `{ character }`: saves the character the game names (unknown Odyle until the first reading); repeating an identity returns the same record. Any other field is refused. Used for automatic game identification only: the app has no manual character entry |
| POST `/api/odyle/report` | see below | `{ character, serverNow }` |
| POST `/api/odyle/heartbeat` | `{ sessionId, characterId }` | Every 20 seconds: keeps the session's latest reading live; `characterId` is `null` without a live reading |
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
  "parserVersion": "global-reference-v1",
  "source": "packet",
  "confirmed": true
}
```

An existing `characterId` must belong to the account and keeps its saved identity. `value` is an integer 0–1,000,000, `sequence` grows per session and `parserVersion` must be listed in the backend's adapter registry (`global-reference-v1`). The app sends a value the first time it reads a character and afterwards only when it changes; the heartbeat keeps an unchanged value live. Regions: `GLOBAL_EU`, `GLOBAL_NA`, `GLOBAL_SA`, `GLOBAL_ASIA`. Presence follows the dashboard's rule: live while the heartbeat is younger than 45 seconds, stale after that or once invalidated, offline after 90 seconds (`Odyle.Core.Readiness`).

## Rooms, party and planned runs

| Method / route | Answer and use |
| --- | --- |
| GET `/api/odyle/rooms` | `{ rooms, odylePerCube, serverNow }`; each room has `id`, `name`, `cost`, `role`, `sharing` and `sharedCharacters` (the character ids this account shares there). Settings summarizes where each character is shared; the card's cube cost is the first sharing room's |
| GET `/api/odyle/snapshot/{id}` | `{ room, members, wishlist, runs, dungeons, serverNow }`; members have `id`, `user`, `role`, `displayName` and `characters`. The app lists other members' live and stale characters, corrects its clock with `serverNow` and reads `runs` for reminders; it ignores `wishlist` and `dungeons` |

The app reads the rooms and their snapshots every 15 seconds while its window is open and every minute while it waits in the tray. Sharing, rooms, invitations, the wishlist and planning are managed on the dashboard.

A snapshot's `runs` list the room's planned runs until three hours after they started:

```json
[{ "id": "run-id", "dungeon": "Fire Temple", "startsAt": "2026-10-14 19:00:00.000Z", "note": "Bring potions", "planner": "user-id-1", "attendees": ["user-id-1", "user-id-2"] }]
```

`planner` and `attendees` are account ids. For each run whose `attendees` contain the signed-in account, the app shows one notification 15 minutes before `startsAt` (dungeon, room name, local start time, note), once per run id and start time, so a re-timed run is announced again (`Odyle.Core.Reminders`). Newer backends also offer `GET /api/odyle/upcoming`; the app reads the snapshots instead, which every backend version provides.

## Daily and weekly tasks

While the checklist panel is open (every 15 seconds), the app reads `GET /api/odyle/tasks` → `{ tasks, resets, serverNow }` and ticks a task with `POST /api/odyle/task` `{ id, done }`, which answers with the same object. A task has `id`, `user`, `title`, `cadence` (`daily`/`weekly`) and `doneAt`; `resets` is `{ dailyResetHour, weeklyResetDay }`. A task counts as done while `doneAt` is on or after the start of its current period (`Odyle.Core.Checklist`, mirroring the dashboard's `src/tasks.js`). Adding, renaming, ordering and removing tasks stay on the dashboard.

## Routes only the dashboard uses

Room management (`create`, `join`, `sharing`, `wishlist`, `plan`, `invites`, `revoke`, `leave`, `close`, `rename`, `role`, `transfer`, `discord-webhook`), Odyle history (`GET /api/odyle/history/{characterId}`), run reminders across rooms (`GET /api/odyle/upcoming`), the account page (`GET /api/odyle/account`, `profile`, `export`, `delete-character`, `forget-device`, `sign-out-everywhere`, `delete-account`), the Windows release lookup (`GET /api/odyle/windows-release`), the superuser monitor and settings (`/api/odyle/admin/...`) and realtime room topics (`odyle.room.<roomId>`) are described in the [dashboard's API document](https://github.com/oldbear24/odyle-dashboard/blob/main/docs/api.md). The Windows app does not subscribe to realtime events; it polls the snapshots as above.

## Updates from GitHub

The app's updater does not use the backend: it reads `https://api.github.com/repos/oldbear24/odyle-windows/releases/latest` and downloads only from `https://github.com/` release storage. Packages are named `odyle-<version>-<win-x64|win-arm64>[-self-contained].zip`, each with a `<name>.sha256` file (`<hash>  <name>`); a release also carries `odyle-<version>-win-x64-setup.exe` with its `.sha256`, which the updater never selects. Drafts, pre-releases and tags that are not versions are ignored (`Odyle.Core.ReleaseFeed`).
