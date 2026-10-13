# Packet reader

The Windows companion reads Odyle from the game's own network traffic and publishes it to the paired account. It never accepts typed values. Readings carry the adapter name `global-reference-v1`; the backend stores a reading only when its registry (`backend/pb_hooks/lib/protocols.cjs` in the dashboard repository) lists that name, so an outdated or unknown client cannot write totals.

## Local operation

Install [Npcap](https://npcap.com/#download) separately. The app loads its installed library from the Windows system Npcap directory; no driver is bundled or installed automatically. Restricted Npcap installations may require capture permissions. Pair the app to your dashboard account, then **start the reader before logging the character into the game**. An empty account can start detection immediately; no prior character record is needed.

Npcap receives incoming TCP traffic from game port 13328. Before decoding a packet, the app checks that its IPv4 connection belongs to a process named AION2/AION-2 using the operating system's connection table, not game memory. Other processes and the game's HTTPS/login connections are ignored. The discovery filter stays open across reconnects so the initial handshake and snapshot are not lost to the two-second connection refresh. Ethernet, raw IPv4 and loopback link types are supported; IPv6, IP fragmentation, UDP, unsupported frame variants and encrypted payloads are not. It cannot decrypt or bypass game protection.

Packet bytes remain in bounded in-memory buffers. The app does not save or upload raw traffic and never injects packets. Streams with malformed framing, conflicting retransmissions or persistent gaps are blocked until the connection changes. A midstream start waits for a complete protocol heartbeat to establish framing; it cannot recover a snapshot or identity that already passed, so the reader shows **Game already connected · relog or change channel** until a new snapshot arrives. A changed own-character ID clears the Odyle baseline and starts identifying the new character; an initial or repeated identity message alone is not a switch.

## What is published and when

* The first decoded value (snapshot, or a live update on top of a known snapshot) is sent immediately to the selected character. When the game's own-character message has not been seen yet and the account has several characters, the app waits three seconds for it so the reading is attached to the character the game reports.
* While game packets keep arriving, the current value is re-sent every 20 seconds together with the presence heartbeat. The dashboard treats a value as live while these confirmations continue and as stale 45 seconds after they stop.
* No game packets for 20 seconds, stopping the reader, switching characters, disconnecting or quitting marks the reading stale on the server; the last value is kept.
* A duplicate of the same frame seen on a second capture interface is ignored. A server refusal (unknown adapter, expired session) shows **Not saved** with the server's message and retries after a minute; an expired publisher session is rotated automatically.

The card shows both components as `base + bonus` so a mismatch with the in-game display is easy to spot.

## Automatic character setup

The independently implemented `3336` own-character decoder reads a varint ID, four-byte mask, one-byte mask, UTF-8 byte count and name, little-endian server ID and class ID. A valid name with a known Global server/region is saved via the metadata-only API and selected automatically. Repeated messages are deduplicated; account-owned records are reused. The class is a structural check, not a stored profile field. Raw identifiers and packet payloads are not sent to the backend.

Global server IDs encode faction, region and server number. The name/ID facts come from the public [Global server table](https://github.com/cyberbadger6969/aion2-dps-meter/blob/main/data/servers.json), and identity fields from its [protocol documentation](https://github.com/cyberbadger6969/aion2-dps-meter/blob/main/src/AionMeter.Core/Protocol/PacketParser.cs). For example, `2304` maps to Lumiel / Global EU. Unknown mappings, malformed text and unnamed tutorial characters do not produce an automatic account record; the app opens manual setup with the detected name instead. Manual identity setup cannot supply an Odyle value.

## Wire format

The decoder follows public wire-format observations: length varints, optional `F0`–`FE` transport flags, `FF FF` LZ4 blocks, snapshot `0B 61`, update `0C 61`, and own identity `33 36`. Snapshot frames can contain two explicit values, or the single-value form with an implicit zero base. A dynamic-only update requires a preceding snapshot for the same stat ID. Ambiguous snapshots and unknown layouts produce no total. Sprint/dash fields and other stat IDs are ignored without disturbing the Odyle baseline.

References: [Aion2 Aether Monitoring](https://github.com/gyu98-mun/Aion2_Aether_Monitoring/blob/main/aion2_core.py) (format observations; its source has no reusable license and was not copied), [AionMeter for Global](https://github.com/cyberbadger6969/aion2-dps-meter) (Global port, heartbeat framing and transport flag; GPL source not incorporated), [Npcap API](https://npcap.com/guide/npcap-devguide.html) through native imports, and [K4os LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) as the only managed dependency.

## Live evidence

On 2026-10-13 the passive diagnostic ran against the author's own AION2 client, Steam build **25800038**, EU Global / Lumiel, with Npcap **1.10.7**. It decoded live heartbeat and gameplay frames, and after a relog observed `0B61` with a 487-byte payload and extracted **0 + 390 = 390**, matching the two values shown in the game. Live `0C61` frames with eight-byte payloads carried stat field 12, not the Odyle field 1; they are ignored without erasing the baseline. The live-update path for field 1 follows the documented format but has not yet been compared against an in-game spend or recharge: if the number on the card ever differs from the game, note the game value, the card's `base + bonus` and the time, and open an issue. No raw payloads, network endpoints, player identifiers or account data were retained.

Synthetic checks cover reordered/fragmented TCP, duplicate and conflicting overlaps, sequence rollover, heartbeat synchronization, flagged frames, framing/decompression bounds, single/two-component snapshots, ambiguous snapshots, base/dynamic updates, repeated and changed own identities, and the UI/API publishing path against a disposable backend.
