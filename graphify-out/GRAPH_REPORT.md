# Graph Report - .  (2026-07-24)

## Corpus Check
- 26 files · ~32,536 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1005 nodes · 2054 edges · 60 communities (43 shown, 17 thin omitted)
- Extraction: 93% EXTRACTED · 7% INFERRED · 0% AMBIGUOUS · INFERRED: 134 edges (avg confidence: 0.81)
- Token cost: 116,727 input · 0 output

## Community Hubs (Navigation)
- Manifest Binary Codec
- Repo Layout & Component Overview
- In-Memory Multicast Transport
- Receiver Session & Repair Types
- Wire Message Codec (SpanReader)
- Solution & Package Dependencies
- Chunk Hash Value Type
- End-to-End Transfer Tests
- Repair Coordinator
- Content Key Wrap (X25519/HKDF)
- Peer Table
- Wire Message Types
- Path Safety (Traversal Prevention)
- Founding Plan: Core Architecture Decisions
- Chunk Bitmap
- In-Memory Trust Store
- Trust & Security Namespaces
- Real UDP Unicast Transport
- In-Memory Network Chaos Tests
- Real Multicast Fan-Out Tests
- Chunk Layout & Range
- Trust Store JSON Codec
- Trust Decision Engine
- File-Backed Trust Store
- Filesystem Chunk Sink Tests
- Transport Namespaces & Integration Tests
- Chunker
- Endpoint Abstraction
- Filesystem File Sink
- Manifest Signing Tests
- Filtering Multicast Transport (Test Support)
- Public Key Id Tests
- Chaos Transport (Integration Tests)
- Chunking Namespaces & Test Files
- Chunker Tests
- Filesystem File Source
- Memory File Sink
- LLM Wiki Conventions
- Memory File Source
- Public Key Id Type
- Project Infra Conventions (plan)
- CLI Test Placeholder
- E2E Test Placeholder
- Multicast Interface Enumeration
- Trust Seed Template
- TUI Placeholder
- Discovery Placeholder
- Platform Quirks & Tech Stack (plan)
- Repo Layout & GUI Placeholder (plan)
- Roadmap-First Convention
- Signed (fragment)
- Sources (fragment)
- Dictionary (fragment)
- Dictionary (fragment)
- IAsyncEnumerable (fragment)
- IReadOnlyList (fragment)
- ReadOnlyMemory (fragment)
- ValueTask (fragment)
- Merkle Trees (fragment)

## God Nodes (most connected - your core abstractions)
1. `ReceiverSession` - 28 edges
2. `Castr.Core.Security` - 24 edges
3. `Castr wire protocol` - 24 edges
4. `Castr roadmap and milestone status` - 24 edges
5. `EndToEndTransferTests` - 21 edges
6. `Castr technology stack` - 21 edges
7. `ADR-0003: Payload encryption (reverses M0 no-encryption decision)` - 21 edges
8. `ChunkHash` - 18 edges
9. `Castr.Core.Manifest` - 18 edges
10. `PublicKeyId` - 17 edges

## Surprising Connections (you probably didn't know these)
- `CI build-and-test job` --semantically_similar_to--> `Verification/testing approach (plan)`  [INFERRED] [semantically similar]
  .github/workflows/ci.yml → raw/2026-07-24-castr-project-plan.md
- `Mobile unicast swarm client decision` --rationale_for--> `Castr (product entity)`  [INFERRED]
  raw/2026-07-24-castr-project-plan.md → wiki/entities/castr-project.md
- `RepairCoordinator (plan)` --rationale_for--> `Castr repair protocol`  [INFERRED]
  raw/2026-07-24-castr-project-plan.md → wiki/concepts/repair-protocol.md
- `TrustDecisionEngineTests` --references--> `PublicKeyId`  [EXTRACTED]
  tests/Castr.Core.Tests/Trust/TrustDecisionEngineTests.cs → src/Castr.Core/Security/PublicKeyId.cs
- `TrustSeedMergerTests` --references--> `PublicKeyId`  [EXTRACTED]
  tests/Castr.Core.Tests/Trust/TrustSeedMergerTests.cs → src/Castr.Core/Security/PublicKeyId.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Castr wire protocol message set** — wiki_concepts_wire_protocol_announce, wiki_concepts_wire_protocol_manifest_message, wiki_concepts_wire_protocol_join_request, wiki_concepts_wire_protocol_key_grant, wiki_concepts_wire_protocol_chunk_data, wiki_concepts_wire_protocol_peer_have, wiki_concepts_wire_protocol_chunk_request_response, wiki_concepts_wire_protocol_transfer_complete [EXTRACTED 1.00]
- **NSec.Cryptography-backed encryption primitives** — wiki_concepts_tech_stack_nsec_cryptography, wiki_concepts_tech_stack_chacha20_poly1305, wiki_concepts_tech_stack_x25519, wiki_concepts_tech_stack_hkdf_sha256 [EXTRACTED 1.00]
- **M1.5 payload-encryption feature (decision, implementation, and mechanism)** — wiki_synthesis_adr_0003_payload_encryption, wiki_synthesis_m1_5_encryption_summary, wiki_concepts_security_model_payload_encryption, wiki_concepts_wire_protocol_join_request_key_grant_handshake [INFERRED 0.85]
- **Castr core architecture concept cluster** — wiki_entities_castr_project_castr, wiki_concepts_repair_protocol_repair_protocol [EXTRACTED 1.00]
- **Definition-of-done gate applied at every milestone** — raw_2026_07_24_castr_project_plan_milestones, raw_2026_07_24_castr_project_plan_graphify_llm_wiki_infra [EXTRACTED 1.00]

## Communities (60 total, 17 thin omitted)

### Community 0 - "Manifest Binary Codec"
Cohesion: 0.07
Nodes (19): Castr.Core.Tests.Manifest, Castr.Core.Manifest, Castr.Core.Tests.Protocol, Castr.Core.Protocol, ManifestCodec, SpanReader, byte, ReadOnlySpan (+11 more)

### Community 1 - "Repo Layout & Component Overview"
Cohesion: 0.09
Nodes (61): we-are-starting-a-tender-bee.md (original approved plan file, outside repo), M1.5 deliberate deviations (JOIN_REQUEST/KEY_GRANT over shared multicast; content key injected not constructed), M1.5 non-blocking hardening notes (session-ID uniqueness caller contract, key-material zeroing), castr-project-plan (approved M0 project plan source), Castr README, src/Castr.Cli (command-line entrypoint), src/Castr.Core (protocol state machines, chunker, Merkle/manifest, trust store, transport abstractions), src/Castr.Core.Discovery (peer discovery abstraction + platform mDNS impls) (+53 more)

### Community 2 - "In-Memory Multicast Transport"
Cohesion: 0.05
Nodes (38): Castr.Core.Transport.InMemory, IMulticastTransport, Lock, ChaosOptions, InMemoryMulticastTransport, CancellationToken, Channel, ChannelWriter (+30 more)

### Community 3 - "Receiver Session & Repair Types"
Cohesion: 0.06
Nodes (32): ChunkHash, Dictionary, HashSet, IPeerTable, ISystemClock, RepairCoordinator, SignedManifest, ReceiverSession (+24 more)

### Community 4 - "Wire Message Codec (SpanReader)"
Cohesion: 0.11
Nodes (13): SpanReader, MessageCodec, SpanReader, byte, int, MerkleProof, ReadOnlySpan, Stream (+5 more)

### Community 5 - "Solution & Package Dependencies"
Cohesion: 0.05
Nodes (34): Blake3 (3.0.2), NSec.Cryptography (26.4.0), net10.0, Microsoft.NET.Sdk, net10.0, Microsoft.NET.Sdk, net10.0, Microsoft.NET.Sdk (+26 more)

### Community 6 - "Chunk Hash Value Type"
Cohesion: 0.10
Nodes (17): IEquatable, ChunkHash, byte, int, ReadOnlySpan, ChunkHash, MerkleProof, MerkleProofStep (+9 more)

### Community 7 - "End-to-End Transfer Tests"
Cohesion: 0.13
Nodes (21): Factory, GetSink, IAsyncEnumerable, IFileSink, IReadOnlyList, MemoryFileSink, ReadOnlyMemory, EndToEndTransferTests (+13 more)

### Community 8 - "Repair Coordinator"
Cohesion: 0.09
Nodes (19): Castr.Core.Time, IReadOnlyCollection, RepairCoordinator, RepairOptions, RepairRequestPlan, DateTimeOffset, Dictionary, Func (+11 more)

### Community 9 - "Content Key Wrap (X25519/HKDF)"
Cohesion: 0.11
Nodes (19): KeyAgreementAlgorithm, KeyDerivationAlgorithm, SharedSecret, ContentKeyWrap, AeadAlgorithm, byte, int, Key (+11 more)

### Community 10 - "Peer Table"
Cohesion: 0.14
Nodes (14): IPeerTable, PeerInfo, DateTimeOffset, IReadOnlyList, PeerHaveMessage, PeerEntry, PeerTable, DateTimeOffset (+6 more)

### Community 11 - "Wire Message Types"
Cohesion: 0.17
Nodes (14): MerkleTree, AnnounceMessage, ChunkDataMessage, ChunkRequestMessage, ChunkResponseMessage, JoinRequestMessage, KeyGrantMessage, MessageType (+6 more)

### Community 12 - "Path Safety (Traversal Prevention)"
Cohesion: 0.15
Nodes (10): Exception, IDisposable, PathSafety, PathTraversalException, PathSafetyTests, TempDir, Fact, InlineData (+2 more)

### Community 13 - "Founding Plan: Core Architecture Decisions"
Cohesion: 0.11
Nodes (23): Core-first build phasing decision, Avalonia GUI framework decision, IPeerTable abstraction (plan), Mobile unicast swarm client decision, Path safety (no traversal) decision, Integrity-only payload security decision, Castr approved project plan, Repair protocol design (plan) (+15 more)

### Community 14 - "Chunk Bitmap"
Cohesion: 0.17
Nodes (7): ChunkBitmap, byte, IEnumerable, ChunkBitmapTests, Fact, InlineData, Theory

### Community 15 - "In-Memory Trust Store"
Cohesion: 0.18
Nodes (10): InMemoryTrustStore, Dictionary, IReadOnlyList, ITrustStore, IReadOnlyList, TrustEntry, IEnumerable, IReadOnlyList (+2 more)

### Community 16 - "Trust & Security Namespaces"
Cohesion: 0.16
Nodes (7): Castr.Core.Trust, Castr.Core.Tests.Security, Castr.Core.Tests.Trust, Castr.Core.Security, TrustEntrySource, TrustStatus, TrustSeedMerger

### Community 17 - "Real UDP Unicast Transport"
Cohesion: 0.17
Nodes (13): IUnicastTransport, Endpoint, UdpUnicastTransport, CancellationToken, EndPoint, IAsyncEnumerable, ReadOnlyMemory, ReceivedPacket (+5 more)

### Community 18 - "In-Memory Network Chaos Tests"
Cohesion: 0.29
Nodes (7): Endpoint, IMulticastTransport, IUnicastTransport, InMemoryNetworkTests, Fact, Task, TimeSpan

### Community 19 - "Real Multicast Fan-Out Tests"
Cohesion: 0.18
Nodes (12): IAsyncDisposable, IMulticastTransport, CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask, RealMulticastFanOutTests, CancellationToken (+4 more)

### Community 20 - "Chunk Layout & Range"
Cohesion: 0.20
Nodes (8): ChunkLayout, ChunkRange, IEnumerable, int, ChunkLayoutTests, Fact, InlineData, Theory

### Community 21 - "Trust Store JSON Codec"
Cohesion: 0.18
Nodes (10): JsonSerializerOptions, TrustEntryDto, TrustStoreDocument, TrustStoreJsonCodec, DateTimeOffset, IReadOnlyList, List, TrustStoreJsonCodecTests (+2 more)

### Community 22 - "Trust Decision Engine"
Cohesion: 0.22
Nodes (6): TrustDecision, TrustOutcome, TrustDecisionEngine, UnknownSenderPolicy, TrustDecisionEngineTests, Fact

### Community 23 - "File-Backed Trust Store"
Cohesion: 0.22
Nodes (6): FileTrustStore, IReadOnlyList, string, FileTrustStoreTests, Fact, string

### Community 24 - "Filesystem Chunk Sink Tests"
Cohesion: 0.25
Nodes (7): CancellationToken, ReadOnlyMemory, ValueTask, FileSystemFileSourceSinkTests, Fact, string, Task

### Community 25 - "Transport Namespaces & Integration Tests"
Cohesion: 0.19
Nodes (5): Castr.Core.Transport, Castr.Core.IntegrationTests, Castr.Core.Transport.Udp, Castr.Core.Tests.Transport, SmokeTest

### Community 26 - "Chunker"
Cohesion: 0.26
Nodes (9): Chunker, CancellationToken, Memory, Task, ValueTask, IFileSource, CancellationToken, Memory (+1 more)

### Community 27 - "Endpoint Abstraction"
Cohesion: 0.21
Nodes (8): Endpoint, ReceivedPacket, IPEndPoint, IUnicastTransport, CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask

### Community 28 - "Filesystem File Sink"
Cohesion: 0.17
Nodes (8): bool, FileSystemFileSink, SafeFileHandle, string, IFileSink, CancellationToken, ReadOnlyMemory, ValueTask

### Community 30 - "Filtering Multicast Transport (Test Support)"
Cohesion: 0.24
Nodes (6): Castr.Core.Tests.TestSupport, FilteringMulticastTransport, CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask

### Community 31 - "Public Key Id Tests"
Cohesion: 0.38
Nodes (3): ReadOnlySpan, PublicKeyIdTests, Fact

### Community 32 - "Chaos Transport (Integration Tests)"
Cohesion: 0.24
Nodes (6): ChaosTransport, CancellationToken, IAsyncEnumerable, Random, ReadOnlyMemory, ValueTask

### Community 34 - "Chunker Tests"
Cohesion: 0.58
Nodes (3): ChunkerTests, Fact, Task

### Community 35 - "Filesystem File Source"
Cohesion: 0.25
Nodes (5): FileSystemFileSource, CancellationToken, Memory, SafeFileHandle, ValueTask

### Community 36 - "Memory File Sink"
Cohesion: 0.25
Nodes (5): MemoryFileSink, byte, CancellationToken, ReadOnlyMemory, ValueTask

### Community 37 - "LLM Wiki Conventions"
Cohesion: 0.40
Nodes (6): LLM Wiki maintenance convention, Wiki page template, Wiki Graph Ontology, Wiki Graph Layer docs, Wiki optional graph metadata (graph: key), Wiki Schema (conventions)

### Community 38 - "Memory File Source"
Cohesion: 0.33
Nodes (4): MemoryFileSource, CancellationToken, Memory, ValueTask

### Community 40 - "Project Infra Conventions (plan)"
Cohesion: 0.40
Nodes (5): graphify codebase graph convention, CI build-and-test job, graphify + llm-wiki mandatory infrastructure (plan), Milestone plan M0-M5 (plan), Verification/testing approach (plan)

### Community 41 - "CLI Test Placeholder"
Cohesion: 0.40
Nodes (3): Castr.Cli.Tests, UnitTest1, Fact

### Community 42 - "E2E Test Placeholder"
Cohesion: 0.40
Nodes (3): Castr.Core.E2ETests, UnitTest1, Fact

### Community 43 - "Multicast Interface Enumeration"
Cohesion: 0.40
Nodes (3): IPAddress, MulticastInterfaces, IReadOnlyList

### Community 44 - "Trust Seed Template"
Cohesion: 0.40
Nodes (4): comment, entries, $schema, version

## Knowledge Gaps
- **84 isolated node(s):** `net10.0`, `Microsoft.NET.Sdk`, `net10.0`, `Microsoft.NET.Sdk`, `Castr.Core.Discovery` (+79 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **17 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Castr.Core.Manifest` connect `Manifest Binary Codec` to `Receiver Session & Repair Types`, `Chunk Hash Value Type`, `Content Key Wrap (X25519/HKDF)`, `Wire Message Types`, `Transport Namespaces & Integration Tests`?**
  _High betweenness centrality (0.217) - this node is a cross-community bridge._
- **Why does `Castr.Core.Protocol` connect `Manifest Binary Codec` to `Repair Coordinator`, `Peer Table`, `Wire Message Types`, `Chunk Bitmap`, `Transport Namespaces & Integration Tests`, `Filtering Multicast Transport (Test Support)`?**
  _High betweenness centrality (0.190) - this node is a cross-community bridge._
- **Why does `Castr.Core.Security` connect `Trust & Security Namespaces` to `Manifest Binary Codec`, `Receiver Session & Repair Types`, `Public Key Id Type`, `Content Key Wrap (X25519/HKDF)`, `Path Safety (Traversal Prevention)`, `Transport Namespaces & Integration Tests`?**
  _High betweenness centrality (0.164) - this node is a cross-community bridge._
- **What connects `net10.0`, `Microsoft.NET.Sdk`, `net10.0` to the rest of the system?**
  _84 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Manifest Binary Codec` be split into smaller, more focused modules?**
  _Cohesion score 0.06721311475409836 - nodes in this community are weakly interconnected._
- **Should `Repo Layout & Component Overview` be split into smaller, more focused modules?**
  _Cohesion score 0.08797814207650273 - nodes in this community are weakly interconnected._
- **Should `In-Memory Multicast Transport` be split into smaller, more focused modules?**
  _Cohesion score 0.05012531328320802 - nodes in this community are weakly interconnected._