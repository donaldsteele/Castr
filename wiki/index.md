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
