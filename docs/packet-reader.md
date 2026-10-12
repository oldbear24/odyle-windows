# Packet reader: validation pending

The Windows companion now reads packets rather than accepting typed Odyle. **No Global adapter is verified yet, and the current experimental adapter never publishes a reading.** This is a prototype, not a claim that the user's current game is supported.

## Local operation

Install [Npcap](https://npcap.com/#download) separately. The app loads its installed library from the Windows system Npcap directory; no driver is bundled or installed automatically. Restricted Npcap installations may require capture permissions. Pair the app to your dashboard account, then start the reader before logging the character into the game. An empty account can start detection immediately; no prior character record is needed.

Npcap receives incoming TCP traffic from game port 13328. Before decoding a packet, the app checks that its IPv4 connection belongs to a process named AION2/AION-2. Other processes and the game's HTTPS/login connections are ignored. The discovery filter stays open across reconnects so the initial handshake and snapshot are not lost to the two-second connection refresh. It uses the operating system's connection table, not game memory. It supports Ethernet, raw IPv4 and loopback link types. IPv6, IP fragmentation, UDP, unsupported frame variants and encrypted payloads are unsupported. It cannot decrypt or bypass game protection. Stop/restart clears packet state. A changed own-character ID clears the Odyle baseline and initiates identification of the new character; an initial or repeated identity message alone does not constitute a switch.

Packet bytes remain in bounded in-memory buffers. The app does not save or upload raw traffic. There is no packet injection. Streams with malformed framing, conflicting retransmissions or persistent gaps are blocked until the connection changes. A midstream start waits for a complete protocol heartbeat to establish framing; it cannot recover a snapshot or identity that already passed. Start the reader first and reconnect the game. This release only displays **unverified packet candidates** locally, including both components and their sum.

## Automatic character setup

The independently implemented `3336` own-character decoder reads a varint ID, four-byte mask, one-byte mask, UTF-8 byte count and name, little-endian server ID and class ID. A valid name with a known Global server/region is saved via the metadata-only API and selected automatically. Repeated messages are deduplicated; account-owned records are reused. The class is a structural check, not currently a stored/displayed profile field. Raw identifiers and packet payloads are not sent to the backend.

Global server IDs encode faction, region and server number. The name/ID facts come from the public [Global server table](https://github.com/cyberbadger6969/aion2-dps-meter/blob/main/data/servers.json), and identity fields from its [protocol documentation](https://github.com/cyberbadger6969/aion2-dps-meter/blob/main/src/AionMeter.Core/Protocol/PacketParser.cs). For example, `2304` maps to Lumiel / Global EU. Unknown mappings, malformed text and unnamed tutorial characters do not produce an automatic account record. Manual identity setup remains available; it cannot supply an Odyle value.

Automatic detection is covered by synthetic wire checks and native UI/API checks. The later passive check on this machine ended after the game connection closed, before a new own-character message was observed; automatic identification has not yet been confirmed against this user's live character. This does not change the limited startup Odyle evidence below.

## Experimental reference format

The independent implementation explores public wire-format observations: length varints, optional `F0`–`FE` transport flags, `FF FF` LZ4 blocks, snapshot `0B 61`, update `0C 61`, and own identity `33 36`. Snapshot candidates can contain two explicit values, or the reference single-value form with an implicit zero base. A dynamic-only update requires a preceding snapshot for the same stat ID. Ambiguous snapshots and unknown layouts produce no total. Sprint/dash fields are ignored.

Reference: [Aion2 Aether Monitoring](https://github.com/gyu98-mun/Aion2_Aether_Monitoring/blob/main/aion2_core.py). Its existence does not establish Global compatibility. Its source has no reusable license and was not copied into this project. [Abyss Logs](https://github.com/karim-mo/aion2-abysslogs-dps-meter) advertises an Odyle display but does not expose its decoder source. [Npcap API](https://npcap.com/guide/npcap-devguide.html) is used through native imports; [K4os LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) is the only new managed dependency.

[AionMeter for Global](https://github.com/cyberbadger6969/aion2-dps-meter) documents the Global game port, heartbeat framing and transport flag. Those wire facts informed the independent implementation; its GPL source was not incorporated. Its public combat parser does not establish our Odyle decoder's correctness.

## Live check: EU Global, Lumiel

On 2026-10-13, the passive diagnostic ran against the user's own running AION2 client, Steam build **25800038**, with Npcap **1.10.7**. It successfully decoded live heartbeat and gameplay frames. After the user logged the character in again, it observed `0B61` with a 487-byte payload and extracted **0 + 390 = 390**, matching the two values the user reported seeing in the game. The same message appeared on two capture interfaces; those duplicates are not independent validation samples. Live `0C61` frames with eight-byte payloads included header `00 04 0C`: this is another stat field (12), not the reference Odyle field (1). It must be ignored without erasing the Odyle baseline. Seeing the opcode alone does not demonstrate an Odyle update.

No raw payloads, network endpoints, player identifiers or account data were retained. This is an observation log, not a replayable captured fixture. The user opted not to spend or recharge Odyle during this check. Therefore live update semantics, another character/value, and all-zero behavior remain unverified. The production registry stays empty and the experimental adapter still cannot publish. This successful startup read does not claim full Global support.

## Evidence required to finish Global support

Provide the actual region, server and game build, plus a short capture from **your own game connection** with recorded in-game Odyle totals before/after a known spend or recharge. Include startup snapshot, reconnect and switching between two characters with different totals. Extract only relevant packet payloads; remove network addresses, account tokens, chat and unrelated traffic before sharing. A full unfiltered PC capture is unnecessary.

Use those samples to establish the opcode/layout, identity mapping, base/dynamic meaning and zero handling. Add anonymized fixtures with expected totals and regression checks for missing baselines, retransmissions and build mismatch. Then implement a specific Global adapter and its publisher path. Only after those checks pass should its version be added to the dashboard registry. The current `PacketCandidate.IsVerified` is always false; there is no hidden force-enable switch.

Synthetic tests cover reordered/fragmented TCP, duplicate and conflicting overlaps, sequence rollover, heartbeat synchronization, flagged frames, framing/decompression bounds, single/two-component snapshots, ambiguous snapshots, base/dynamic updates, repeated and changed own identities, and unverified confidence. They demonstrate implementation behavior; the live startup observation above supplies the limited real-game evidence.
