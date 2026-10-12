# Packet reader: validation pending

The Windows companion now reads packets rather than accepting typed Odyle. **No Global adapter is verified yet, and the current experimental adapter never publishes a reading.** This is a prototype, not a claim that the user's current game is supported.

## Local operation

Install [Npcap](https://npcap.com/#download) separately. The app loads its installed library from the Windows system Npcap directory; no driver is bundled or installed automatically. Restricted Npcap installations may require capture permissions. Pair the app to your dashboard account, choose the character/server, then start the reader before connecting the game.

The capture filter follows incoming established IPv4 TCP connections owned by processes named AION2/AION-2. It uses the operating system's connection table, not game memory. It supports Ethernet, raw IPv4 and loopback link types. IPv6, IP fragmentation, UDP, unsupported frame variants and encrypted payloads are unsupported. It cannot decrypt or bypass game protection. Stop/restart clears packet state. An observed identity change after a candidate stops capture and asks the player to select the correct character.

Packet bytes remain in bounded in-memory buffers. The app does not save or upload raw traffic. There is no packet injection. Streams with malformed framing, conflicting retransmissions or persistent gaps are blocked until the connection changes. A midstream start may fail; start the reader first and reconnect the game. This release only displays **unverified packet candidates** locally. Manual names/server/region label the selected character; automatic character identity is not implemented.

## Experimental reference format

The independent implementation explores public wire-format observations: length varints, `FF FF` LZ4 blocks, snapshot `0B 61`, update `0C 61`, and identity boundary `33 36`. A candidate total requires both base and dynamic components; a dynamic-only update requires a preceding snapshot for the same entity. Ambiguous snapshots and unknown layouts produce no total. Sprint/dash fields are ignored.

Reference: [Aion2 Aether Monitoring](https://github.com/gyu98-mun/Aion2_Aether_Monitoring/blob/main/aion2_core.py). Its existence does not establish Global compatibility. Its source has no reusable license and was not copied into this project. [Abyss Logs](https://github.com/karim-mo/aion2-abysslogs-dps-meter) advertises an Odyle display but does not expose its decoder source. [Npcap API](https://npcap.com/guide/npcap-devguide.html) is used through native imports; [K4os LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) is the only new managed dependency.

## Evidence required to finish Global support

Provide the actual region, server and game build, plus a short capture from **your own game connection** with recorded in-game Odyle totals before/after a known spend or recharge. Include startup snapshot, reconnect and switching between two characters with different totals. Extract only relevant packet payloads; remove network addresses, account tokens, chat and unrelated traffic before sharing. A full unfiltered PC capture is unnecessary.

Use those samples to establish the opcode/layout, identity mapping, base/dynamic meaning and zero handling. Add anonymized fixtures with expected totals and regression checks for missing baselines, retransmissions and build mismatch. Then implement a specific Global adapter and its publisher path. Only after those checks pass should its version be added to the dashboard registry. The current `PacketCandidate.IsVerified` is always false; there is no hidden force-enable switch.

Synthetic tests currently cover reordered/fragmented TCP, duplicate and conflicting overlaps, sequence rollover, framing/decompression bounds, base/dynamic updates, character resets and unverified confidence. They demonstrate implementation behavior, **not compatibility with the real game**. Npcap/live-game capture could not be tested on this machine because Npcap and a Global fixture were unavailable.
