# Wiki Index

The catalog of all pages in this wiki. Each entry: a wikilink to the page and a one-line summary. The LLM reads this first when answering queries to identify candidate pages.

Keep summaries tight — one line each. The index is engineered to be cheap to read; a fat index defeats its purpose.

When this file exceeds ~300 lines or the wiki passes ~150 pages, shard into `wiki/indexes/<type>.md` and replace this file with a directory of shards. See the `scaling-playbook.md` reference in the `llm-wiki` skill for the migration procedure.

---

## Sources

- [[castr-project-plan]] — the approved founding architecture and milestone plan for Castr.

## Entities

- [[castr-project]] — overview of Castr: what it is, product shape, why mobile is architecturally different.

## Concepts

- [[wire-protocol]] — message types, two-level chunking, Merkle-root manifest design, replay protection.
- [[repair-protocol]] — peer-assisted chunk repair algorithm, desktop-multicast vs. mobile-unicast delivery, `IPeerTable` abstraction, failure modes.
- [[security-model]] — TOFU trust store, Ed25519/BLAKE3 integrity, ChaCha20-Poly1305 payload encryption, path-traversal prevention.
- [[tech-stack]] — chosen and open library/framework decisions, platform-specific quirks (Windows/Linux/macOS/iOS).

## Synthesis

- [[roadmap]] — milestone status and the durable cross-session task list. Read this first when resuming work.
- [[adr-0001-ed25519-library]] — decision: NSec.Cryptography for Ed25519 signing; solution retargeted net8.0 → net10.0 LTS as a consequence.
- [[adr-0002-mobile-discovery]] — decision: native NsdManager (Android) / NWBrowser (iOS) for mobile peer discovery; iOS Info.plist requirements.
- [[m1-core-summary]] — M1 complete: what was built, key design realization (proof caching over tree reconstruction), documented scope trims, testing approach, open risks.
- [[adr-0003-payload-encryption]] — decision: encrypt chunk payloads (ChaCha20-Poly1305 + X25519 + HKDF), reversing the original M0 no-encryption call; implemented and QA-reviewed as M1.5.
- [[m1.5-encryption-summary]] — M1.5 complete: X25519/JOIN_REQUEST/KEY_GRANT handshake, ciphertext-Merkle chunks, 186+4 tests, QA-confirmed tamper/AAD/nonce/MITM/TOFU checks, deliberate deviations.
- [[m2-ui-summary]] — M2 complete: Core progress/trust-prompt contract, Castr.Tui dashboard, Castr.Gui.Desktop (Avalonia), Castr.Cli (send/receive/trust); 244 tests; QA PASS-WITH-CONCERNS (chunk-size/UDP transport gap, mitigated not fixed).
- [[m3-test-ci-hardening-summary]] — M3 complete: real chunk/wire-packet split, Testcontainers E2E fan-out (real netem loss), security test pass, macOS CI multicast bug found+fixed (broken since M1), Tui flaky-test fix; 295 tests; QA PASS.
- [[m4-mobile-summary]] — M4 complete: TCP unicast swarm-pull tier + native mDNS discovery, Android/iOS GUI heads (real APK/Xcode CI verification), a real Merkle position-relabeling defect found+fixed in the M1-era primitive, libsodium iOS-Simulator gap and view-model duplication documented; 359 tests.
- [[m6-throughput-pipelining]] — M6 complete: root-caused the ~1.6-2.4 MB/s demo-plateau to receiver-side serialization (not sender-side, round 1's first guess); fixed via a channel-decoupled receive loop + explicit socket buffers; three rounds, two independent QA+systems-design reviews; 367 tests.
- [[m7-repair-amplification]] — M7 **complete, merged** (`02dbab0`): repair-storm bounding, PEER_HAVE coalescing, sender own-echo filter. Wire amplification 2.4× → 1.1× and the periodic stall gone; the "+112.6% goodput" claim was **withdrawn** as degraded-host recovery. Two liveness bugs found by review across three rounds, both from the same "never reached vs. finished are indistinguishable" conflation; 439 tests.
- **M8 — default chunk size 8 KB → 256 KB** (merged, `50e4cf4`; recorded in `docs/benchmarks/throughput-runs.md` and [[roadmap]] rather than its own page). Brings the CLI and GUI in line with what `Castr.Core` and [[wire-protocol]] already specified. 1.33× measured, and **2.80× under real netem loss** — coarser repair granularity was a hypothesised regression that measured as an improvement. Also fixed a GUI `Maximum="60000"` cap that would have silently clamped the new default back down, and tightened a reassembly memory bound from ~2.1 GB to ~0.9 MB.

- [[m9-datagram-efficiency]] — M9 stage 1: proof space reserved on packet 0 only, datagram budget 1200 → 1472. 309 → 184 datagrams per 256 KiB chunk (1.68×), **1.41×** goodput (post-review re-measurement; the pre-review figure was 1.42×), prediction exact to the datagram; needs no wire-format change, and the mixed-slicing hazard is a *stranded chunk* (found by QA, fixed) rather than mere degradation — which is also why MTU auto-derivation was implemented and then removed.

- [[m11-backlog-clearance]] — M11 complete: ten backlog items in ten commits; 498 → **538 tests**, 0 warnings, Docker netem tier green *and run in-loop* for the first time since M3. Headline is **offset-keyed fragment reassembly** — `ChunkPacketMessage` trades `PacketIndex`+`PacketCount` for `FragmentOffset`, which retires M9's mixed-slicing stranding class, removes the claimed-count allocation, and drops the "`--datagram-size` must match on every peer" contract; **`FormatVersion` 1 → 2**. Also: session ids bound to transfers (persistently — a process-lifetime registry would enforce nothing), manifests range-checked at admission, three memory/lifetime bounds, and an E2E loss filter that had been sparing one packet in every 184.

- [[m12a-fanout-baseline]] — M12a complete (measurement only): the real fan-out baseline. **3 receivers cost 16% of goodput on the wire, not 41%** (8.545 vs 10.163 MB/s), and **wire amplification is 1.029× flat from 1 to 5 receivers** — the quoted **5.08× is withdrawn**, `CHUNK_PACKET` is 73,600 in every arm and repair traffic is zero. The same-host fan-out cost is the kernel's inline copy (25.0 µs per extra local receiver) converted into lost goodput by a serialized send window, not a protocol cost. First measurement of the **receiver's datagram ceiling: ≥150,000 datagrams/s loss-free**, ~20× more than Castr asks of it. Harness committed at `tools/bench/`; found a concurrent-receiver defect in M11's session registry.

- [[proposal-section-based-repair]] — **proposal, not a decision**: replace the carousel-idle heuristic with an explicit sender-emitted section-completion signal. Motivated by deleting the bug class behind both M7 liveness defects, *not* by throughput. **M12a strengthened that stance and killed the one figure pointing the other way** (amplification measured 1.029× *flat* against receiver count, zero duplicate chunks, zero repair traffic at N ≤ 5 — the 5.08× fan-out row is withdrawn). Blocked on nothing; the open design question is now what acceptance evidence is even possible, since the lossless case shows nothing.

## Documentation outside the wiki

Kept here so the index surfaces them; these are public-facing repo docs, not wiki pages.

- `docs/METHODOLOGY.md` — how Castr's performance work is done: the three measurement rules, every throughput approach shipped/reverted/rejected with reasoning (including why porting UFTP's TFMCC was rejected after reading its source), and an on-the-record "what we got wrong" section. Read alongside [[m6-throughput-pipelining]].
- `docs/benchmarks/throughput-runs.md` — append-only log of every real throughput measurement, plus derived-from-code overhead tables (labelled derived vs. measured). The durable home for numbers that previously survived only as prose.
- `docs/SHOWCASE.md` — the three use-case demos with real captured media (see M5 in [[roadmap]]).
