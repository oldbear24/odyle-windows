# Odyle API v2 · Discord accounts

PocketBase 0.40.5. JSON requests; authenticated requests use a PocketBase **users** token (`Authorization: Bearer <token>`). HTTPS for hosted connections. No superuser credentials belong in either client.

## Authentication

The browser calls `pb.collection('users').authWithOAuth2({ provider: 'discord', createData: { displayName: 'Adventurer' } })`. PocketBase creates or loads the account linked to that Discord identity. Public password registration and password sign-in are disabled.

`POST /api/odyle/desktop-code` with `{}` returns `{ code, expiresAt }` to an authenticated account. The 16-character code expires after five minutes, is stored as a digest, and replaces that account's previous unused code.

`POST /api/odyle/pair` is public and rate limited. Body `{ code }`; response `{ token, record }` for the same account. The code is consumed atomically. Windows stores the application token in DPAPI, never Discord credentials.

`POST /api/collections/users/auth-refresh` refreshes a valid token. Tokens last seven days; accounts/data persist independently of token expiry.

## Rooms

| Method / route | Body | Result and authorization |
| --- | --- | --- |
| POST `/api/odyle/create` | `{ name, cost: 40 }` | Signed-in user creates owned room; returns `{ room, membership, invites }` |
| POST `/api/odyle/join` | `{ code }` | Join as reporter/viewer; returns `{ room, membership }`; repeat join is idempotent |
| GET `/api/odyle/rooms` | — | All rooms of the current account |
| GET `/api/odyle/snapshot/{id}` | — | Member-only `{ room, members, serverNow }`; each member has `displayName` and `characters` |
| POST `/api/odyle/sharing` | `{ roomId, enabled }` | Reporter/owner opts to share all saved characters in this room |
| POST `/api/odyle/settings` | `{ roomId, cost }` | Owner changes cube cost, integer 1–1000 |
| POST `/api/odyle/invites` | `{ roomId }` | Owner revokes old codes and receives reporter/viewer invites |
| POST `/api/odyle/revoke` | `{ roomId, memberId }` | Owner removes another membership |
| POST `/api/odyle/leave` | `{ roomId }` | Non-owner removes their membership; character data remains |
| POST `/api/odyle/close` | `{ roomId }` | Owner deletes room and memberships; character data remains |

Room codes are valid for 30 minutes; memberships and rooms have no automatic inactivity expiry. Room snapshots include character data only when its owner opted to share it there; the owner can always see their own saved characters. Viewer accounts can maintain private characters, but cannot share them in a viewer-only room. Display-name collisions are resolved by user/member IDs.

## Persistent characters and publisher sessions

Own characters can be read with the normal PocketBase `characters` collection API. Others' records and all direct character writes are locked. Shared readings are read via authorized room snapshots.

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
  "source": "manual",
  "confirmed": true
}
```

Returns `{ character, serverNow }`. Existing `characterId` must belong to the authenticated account. Without an ID, the identity tuple `(user, region, server, characterName)` finds or creates a character. Value is an integer 0–1,000,000; sequence increases per device session. Supported regions: `GLOBAL_EU`, `GLOBAL_NA`, `GLOBAL_SA`, `GLOBAL_ASIA`. Packet reports are rejected in this MVP.

`POST /api/odyle/heartbeat` with `{ sessionId, characterId }` updates presence for the character only when that session owns its latest reading. It never changes the last reading timestamp. Empty `{}` can synchronize the browser's server clock without claiming a character is online.

`POST /api/odyle/invalidate` with `{ characterId, sessionId }` marks the latest reading invalid for current readiness if it belongs to that session. It **retains** value, identity and observation timestamp. Confirmed historical values are distinct from current confidence (`valid`). Readings older than 45 seconds are stale; 90 seconds without presence means offline.

## Realtime

Subscribe through the PocketBase SDK to `odyle.room.<roomId>`. The server checks room membership at subscription and before each delivery. Events contain only `{ refresh: true }`; fetch the authorized snapshot afterward and on reconnect. Removed members cannot fetch the snapshot or receive further notifications. User and provider tokens are never broadcast.
