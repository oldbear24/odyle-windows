# Odyle API v2 · Discord accounts

PocketBase 0.40.5. JSON requests; authenticated requests use a PocketBase **users** token (`Authorization: Bearer <token>`). HTTPS for hosted connections. No superuser credentials belong in either client.

## Authentication

The browser calls `pb.collection('users').authWithOAuth2({ provider: 'discord', createData: { displayName: 'Adventurer' } })`. PocketBase creates or loads the account linked to that Discord identity. Public password registration and password sign-in are disabled.

`POST /api/odyle/desktop-code` with `{}` returns `{ code, expiresAt }` to an authenticated account. The 16-character code expires after five minutes, is stored as a digest, and replaces that account's previous unused code.

The dashboard presents a connection link such as `https://your-backend.example/#odyle-pair=ABCDEFGH23456789`. The Windows app extracts the endpoint and code when the user clicks **Paste link & connect** or presses Enter in the pairing input. The fragment is never sent as part of an HTTP request. This link contains the short-lived pairing code, not the account token; only the intended Windows companion should receive it. Base-path deployments are preserved, insecure remote HTTP and embedded URL credentials are rejected. Older apps can still use the raw code with a separate endpoint.

`POST /api/odyle/pair` is public and rate limited. Body `{ code }`; response `{ token, record }` for the same account. The code is consumed atomically. Windows stores the application token in DPAPI, never Discord credentials.

`POST /api/collections/users/auth-refresh` refreshes a valid token. Tokens last seven days; accounts/data persist independently of token expiry.

## Rooms

| Method / route | Body | Result and authorization |
| --- | --- | --- |
| POST `/api/odyle/create` | `{ name }` | Signed-in user creates owned room; returns `{ room, membership, invites }` |
| POST `/api/odyle/join` | `{ code }` | Join as reporter/viewer; returns `{ room, membership }`; repeat join is idempotent |
| GET `/api/odyle/rooms` | — | `{ rooms, odylePerCube, serverNow }`: all rooms of the current account |
| GET `/api/odyle/snapshot/{id}` | — | Member-only `{ room, members, wishlist, serverNow }`; each member has `displayName` and `characters` |
| POST `/api/odyle/sharing` | `{ roomId, characterId, enabled }` | Reporter/owner shares or hides one of their own characters in this room; returns `{ sharedCharacters }`. Without `characterId` (older clients) the choice applies to every character saved at that moment |
| POST `/api/odyle/wishlist` | `{ roomId, dungeon, wanted, everyone? }` | Adds (`wanted: true`, reporter/owner) or removes (`false`, any member) the caller's own entry for a dungeon; `everyone: true` with `wanted: false` lets the owner clear that dungeon for all members. Returns `{ wishlist }` |
| POST `/api/odyle/invites` | `{ roomId }` | Owner revokes old codes and receives reporter/viewer invites |
| POST `/api/odyle/revoke` | `{ roomId, memberId }` | Owner removes another membership |
| POST `/api/odyle/leave` | `{ roomId }` | Non-owner removes their membership; character data remains |
| POST `/api/odyle/close` | `{ roomId }` | Owner deletes room and memberships; character data remains |

Room codes are valid for 30 minutes; memberships and rooms have no automatic inactivity expiry. Sharing is per character: each membership lists the characters its owner linked to that room (`sharedCharacters` in `GET /api/odyle/rooms`). New characters start private everywhere. Room snapshots include only linked characters of other members; members always see all of their own characters, each with `shared: true|false`. Upgrading a backend keeps every character of a membership that shared everything shared. Viewer accounts can maintain private characters, but cannot share them in a viewer-only room. Display-name collisions are resolved by user/member IDs.

### Dungeon wishlist

Each room keeps a list of dungeons its members still need to run. Names are free text (1–60 characters); whitespace is collapsed and names are matched without regard to case, so `fire  temple` joins an existing `Fire Temple` entry, which keeps its first spelling. Repeating an add or removing an entry you are not on changes nothing. A member can be on up to 20 dungeons per room and a room lists up to 50. Entries belong to the membership: leaving, being removed or closing the room deletes them. Viewers see the list but cannot add to it.

The snapshot's `wishlist` is grouped by dungeon, most wanted first, then oldest:

```json
[{ "key": "fire temple", "dungeon": "Fire Temple", "since": "2026-10-13 18:00:00.000Z", "users": ["user-id-1", "user-id-2"] }]
```

`users` are account ids in the order they added the dungeon; clients take display names from `members`. Every change sends the room's realtime refresh. The `room_wishes` collection has no API rules; use the routes above.

## Persistent characters and publisher sessions

Own characters can be read with the normal PocketBase `characters` collection API. Others' records and all direct character writes are locked. Shared readings are read via authorized room snapshots; the Windows app reads `GET /api/odyle/snapshot/{id}` for each room to list the party.

### Character pictures

Each character record carries `portrait`, a file name or empty. Clients load the image from `/api/files/characters/{id}/{portrait}` (the SDK's `pb.files.getURL(record, record.portrait)`); room snapshots include both fields for shared characters only, so other members never receive the link of a private character.

`portrait` is always the character's **official AION 2 portrait**; players cannot set, upload or replace it, and no route writes it. A backend cron job looks up new characters within a minute through PLAYNC's public Global character search (`api-search.plaync.com/aion2global/search/v2/character`, by name, server id and region) and downloads the `profileImageUrl` it lists from `profileimg.plaync.com`. It retries characters without a portrait hourly and refreshes the others daily, rewriting the file only when the image changed. The search server id is rebuilt from the saved server name and region (the game protocol's race × 1000 + region × 100 + server number); NA East and West are both tried. Characters the search does not list, such as ones on servers the reader cannot name, keep their initials; a failed lookup keeps the last downloaded portrait.

`POST /api/odyle/character` accepts only `{ characterName, server, region }` and returns `{ character }` for the signed-in account. It saves identity before any reading, with unknown Odyle (`confirmed: false`, `valid: false`, no reading timestamp or source). Repeating the same identity returns the existing record without changing its last value or confidence. Ownership always comes from authentication; extra fields such as `value`, `user`, `source`, `confirmed` and `characterId` are rejected. The Windows app uses this endpoint for automatic game identification and the explicit **Save character** fallback.

`POST /api/odyle/session` with `{}` returns `{ sessionId, sequence: 0 }`. Optional `{ previousSessionId }` rotates that device's session and invalidates its last readings without clearing stored values or other devices' sessions.

`POST /api/odyle/report`:

```json
{
  "sessionId": "publisher-session-id",
  "sequence": 1,
  "characterId": null,
  "characterName": "Your character",
  "server": "Your server",
  "region": "GLOBAL_EU",
  "value": 0,
  "source": "packet",
  "parserVersion": "global-reference-v1",
  "confirmed": true
}
```

Returns `{ character, serverNow }`. Existing `characterId` must belong to the authenticated account and keeps its saved name, server and region: the identity fields in the body are validated but never rewrite an existing character. Without an ID, the identity tuple `(user, region, server, characterName)` finds or creates a character. Value is an integer 0–1,000,000; sequence increases per device session. Supported regions: `GLOBAL_EU`, `GLOBAL_NA`, `GLOBAL_SA`, `GLOBAL_ASIA`. Only packet reports whose `parserVersion` is listed in the backend `lib/protocols.cjs` registry are accepted; it currently lists `global-reference-v1`, the Windows companion's Global stream decoder. Manual reports are always rejected. The Windows app sends a value the first time it reads a character and afterwards only when the value changes, so `lastReadingAt` is the time of the last change (a value re-sent unchanged, for example after the reader paused, keeps that timestamp); its 20-second heartbeat keeps an unchanged value live. Client metadata is not cryptographic proof of a game observation. The synthetic `test-fixture-v1` adapter is approved only in disposable integration-test hooks.

`POST /api/odyle/heartbeat` with `{ sessionId, characterId }` updates presence for the character only when that session owns its latest reading. It never changes the last reading timestamp. Empty `{}` only returns `serverNow`. Heartbeats never trigger realtime events: clients refresh presence on their own schedule (the dashboard refetches an open room every 15 seconds; `GET /api/odyle/rooms` and the snapshot carry `serverNow` for clock correction).

`POST /api/odyle/invalidate` with `{ characterId, sessionId }` marks the latest reading invalid for current readiness if it belongs to that session. It **retains** value, identity and observation timestamp. Confirmed historical values are distinct from current confidence (`valid`). A valid reading is live while presence is newer than 45 seconds, however old the value is; 45 seconds without presence or an invalidated reading is stale, and 90 seconds without presence means offline.

## Realtime

Subscribe through the PocketBase SDK to `odyle.room.<roomId>`. The server checks room membership at subscription and before each delivery. Events contain only `{ refresh: true }` and follow room, membership, sharing, wishlist, reading, session and portrait changes, never heartbeats; fetch the authorized snapshot afterward and on reconnect. Removed members cannot fetch the snapshot or receive further notifications. User and provider tokens are never broadcast.
