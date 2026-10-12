# Odyle API v1

PocketBase 0.40.5. JSON requests, `Authorization: Bearer <member token>` or PocketBase SDK auth headers. All dates come from the server. Production uses HTTPS.

## Provisioning

`POST /api/odyle/create` (public, rate limited):

```json
{ "name": "Evening expedition", "displayName": "Leader", "cost": 40 }
```

Response: `{ token, record, room, sessionId, invites: [{ code, role, expiresAt }] }`. The record has role `owner`; invites have role `reporter` or `viewer`. Save leader credentials securely. Never distribute the leader token.

`POST /api/odyle/join` (public, rate limited):

```json
{ "code": "12-CHARACTER-CODE", "displayName": "Player" }
```

Response: `{ token, record, room, sessionId }`. Invite determines role. Code whitespace/hyphens are ignored. Invitations expire in 30 minutes. Tokens are scoped to one member and room, with 24-hour lifetime.

## Authenticated actions

| POST route suffix under `/api/odyle/` | Body | Role | Result |
| --- | --- | --- | --- |
| `session` | `{}` | owner/reporter | New `{ sessionId, sequence: 0 }`; clears confidence |
| `sharing` | `{ "enabled": true }` | owner/reporter | Opt in/out; clears prior reading |
| `report` | See below | owner/reporter | Own normalized reading only |
| `invalidate` | `{}` | owner/reporter | Clear confidence and reading |
| `heartbeat` | `{}` | all | Update presence, return `{ serverNow }` |
| `settings` | `{ "cost": 30 }` | owner | Update cube cost, 1–1000 |
| `invites` | `{}` | owner | Revoke old invites and return new codes |
| `revoke` | `{ "memberId": "id" }` | owner | Remove another credential in the room |
| `leave` | `{}` | all | Delete self; owner closes the entire room |

Example report:

```json
{
  "sessionId": "returned-publisher-session",
  "sequence": 1,
  "region": "GLOBAL_EU",
  "server": "Your server",
  "characterName": "Your character",
  "value": 0,
  "source": "manual",
  "confirmed": true
}
```

Regions: `GLOBAL_EU`, `GLOBAL_NA`, `GLOBAL_SA`, `GLOBAL_ASIA`. Values are integers 0–1,000,000. Sequence must increase in the active publisher session; packet reports are rejected in this MVP. `confirmed` means the person explicitly checked their in-game total. Never extrapolate regeneration.

## Read and subscribe

Use normal PocketBase collection APIs for `rooms` and `members`. Collection rules permit only the authenticated member's room. All direct collection writes are locked; mutations go through the routes above. The `invites` collection is inaccessible to clients.

Subscribe through the PocketBase SDK to `members/*` and your room record in `rooms`. Load a complete room/member snapshot on initial connection and every SSE reconnect. The hidden `sessionId` field is returned only by provisioning and `session`, not regular record reads. A member record may contain zero internally while unconfirmed; use `confirmed` and `lastReadingAt` before displaying the numeric value. Never convert unknown to zero.

`POST /api/collections/members/auth-refresh` refreshes a still-valid credential. Rotation, joining and auth refresh do not reconfirm energy. Credential deletion revokes future requests and live delivery.
