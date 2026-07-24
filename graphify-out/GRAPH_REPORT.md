# Graph Report - .  (2026-07-24)

## Corpus Check
- Corpus is ~25,616 words - fits in a single context window. You may not need a graph.

## Summary
- 855 nodes · 1861 edges · 38 communities (34 shown, 4 thin omitted)
- Extraction: 92% EXTRACTED · 8% INFERRED · 0% AMBIGUOUS · INFERRED: 158 edges (avg confidence: 0.82)
- Token cost: 0 input · 125,280 output

## Community Hubs (Navigation)
- Multicast Transport Interface
- Filesystem Chunk Sink/Source
- Project Conventions & Infra
- Chunk Reading & Hashing
- Solution & Package Deps
- Manifest Signing & Verification
- In-Memory Transport (Chaos)
- End-to-End Transfer Test Setup
- Chunk Hash Value Type
- Peer Table
- Repair Coordinator
- Message Codec Decode Path
- Manifest Binary Codec
- Core Chunking/Manifest Namespaces
- Chunk Bitmap
- In-Memory Trust Store
- Trust & Security Namespaces
- Real UDP Unicast Transport
- Trust Store JSON Codec
- Trust Decision Outcome
- Transport Namespaces & Tests
- Real UDP Multicast Transport
- File Sink Interface
- File-Backed Trust Store
- Protocol/Time Namespaces
- Wire Codec Span Reader
- Public Key Id Round-Trip
- Wire Message Types
- Clock Abstraction (Fake/Real)
- Public Key Id Type
- CLI Test Placeholder
- E2E Test Placeholder
- Multicast Interface Enumeration
- Trust Seed Template
- TUI Placeholder
- Discovery Placeholder
- Repo Layout Documentation

## God Nodes (most connected - your core abstractions)
1. `Castr.Core.Chunking` - 26 edges
2. `ReceiverSession` - 23 edges
3. `ChunkHash` - 18 edges
4. `Castr.Core.Security` - 18 edges
5. `EndToEndTransferTests` - 18 edges
6. `PublicKeyId` - 17 edges
7. `Castr.Core.Transport` - 17 edges
8. `TrustEntry` - 17 edges
9. `Castr.Core.Manifest` - 16 edges
10. `Castr.Core.Trust` - 16 edges

## Surprising Connections (you probably didn't know these)
- `CI build-and-test job` --semantically_similar_to--> `Verification/testing approach (plan)`  [INFERRED] [semantically similar]
  .github/workflows/ci.yml → raw/2026-07-24-castr-project-plan.md
- `Castr README overview` --semantically_similar_to--> `Castr (product entity)`  [INFERRED] [semantically similar]
  README.md → wiki/entities/castr-project.md
- `Mobile unicast swarm client decision` --rationale_for--> `Castr (product entity)`  [INFERRED]
  raw/2026-07-24-castr-project-plan.md → wiki/entities/castr-project.md
- `Avalonia GUI framework decision` --rationale_for--> `Castr technology stack`  [INFERRED]
  raw/2026-07-24-castr-project-plan.md → wiki/concepts/tech-stack.md
- `Integrity-only payload security decision` --rationale_for--> `Castr security model`  [INFERRED]
  raw/2026-07-24-castr-project-plan.md → wiki/concepts/security-model.md

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Castr core architecture concept cluster** — wiki_entities_castr_project_castr, wiki_concepts_wire_protocol_wire_protocol, wiki_concepts_repair_protocol_repair_protocol, wiki_concepts_security_model_security_model, wiki_concepts_tech_stack_tech_stack, wiki_synthesis_roadmap_roadmap [EXTRACTED 1.00]
- **M0 spike risks and their resolving ADRs** — raw_2026_07_24_castr_project_plan_tech_stack, wiki_synthesis_adr_0001_ed25519_library_adr, wiki_synthesis_adr_0002_mobile_discovery_adr [INFERRED 0.85]
- **Definition-of-done gate applied at every milestone** — raw_2026_07_24_castr_project_plan_milestones, raw_2026_07_24_castr_project_plan_graphify_llm_wiki_infra, wiki_synthesis_roadmap_roadmap [EXTRACTED 1.00]

## Communities (38 total, 4 thin omitted)

### Community 0 - "Multicast Transport Interface"
Cohesion: 0.06
Nodes (39): IAsyncDisposable, ReceivedPacket, IMulticastTransport, CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask, IUnicastTransport (+31 more)

### Community 1 - "Filesystem Chunk Sink/Source"
Cohesion: 0.06
Nodes (26): bool, Exception, IDisposable, FileSystemFileSink, CancellationToken, ReadOnlyMemory, SafeFileHandle, string (+18 more)

### Community 2 - "Project Conventions & Infra"
Cohesion: 0.09
Nodes (50): graphify codebase graph convention, LLM Wiki maintenance convention, Project state / roadmap-first convention, CI build-and-test job, Core-first build phasing decision, graphify + llm-wiki mandatory infrastructure (plan), Avalonia GUI framework decision, IPeerTable abstraction (plan) (+42 more)

### Community 3 - "Chunk Reading & Hashing"
Cohesion: 0.08
Nodes (24): Chunker, CancellationToken, Memory, Task, ValueTask, ChunkLayout, ChunkRange, IEnumerable (+16 more)

### Community 4 - "Solution & Package Deps"
Cohesion: 0.05
Nodes (34): Blake3 (3.0.2), NSec.Cryptography (26.4.0), net10.0, Microsoft.NET.Sdk, net10.0, Microsoft.NET.Sdk, net10.0, Microsoft.NET.Sdk (+26 more)

### Community 5 - "Manifest Signing & Verification"
Cohesion: 0.11
Nodes (19): ManifestSigner, Key, ManifestVerifier, ChunkHash, MerkleProof, SignedManifest, ReceiverSession, byte (+11 more)

### Community 6 - "In-Memory Transport (Chaos)"
Cohesion: 0.07
Nodes (29): Castr.Core.Transport.InMemory, Lock, ChaosOptions, InMemoryMulticastTransport, CancellationToken, Channel, ChannelWriter, IAsyncEnumerable (+21 more)

### Community 7 - "End-to-End Transfer Test Setup"
Cohesion: 0.13
Nodes (18): Factory, GetSink, Signed, Sources, MerkleTree, SenderSession, CancellationToken, Task (+10 more)

### Community 8 - "Chunk Hash Value Type"
Cohesion: 0.12
Nodes (12): IEquatable, ChunkHash, byte, int, ReadOnlySpan, ReadOnlySpan, ChunkHashTests, Fact (+4 more)

### Community 9 - "Peer Table"
Cohesion: 0.14
Nodes (14): IPeerTable, PeerInfo, DateTimeOffset, IReadOnlyList, PeerHaveMessage, PeerEntry, PeerTable, DateTimeOffset (+6 more)

### Community 10 - "Repair Coordinator"
Cohesion: 0.10
Nodes (15): IReadOnlyCollection, RepairCoordinator, RepairOptions, RepairRequestPlan, DateTimeOffset, Dictionary, Func, IReadOnlyList (+7 more)

### Community 11 - "Message Codec Decode Path"
Cohesion: 0.17
Nodes (10): SpanReader, MessageCodec, byte, int, Stream, ManifestMessage, MessageCodecTests, Fact (+2 more)

### Community 12 - "Manifest Binary Codec"
Cohesion: 0.15
Nodes (9): ManifestCodec, SpanReader, byte, ReadOnlySpan, Stream, TransferManifest, int, ManifestCodecTests (+1 more)

### Community 13 - "Core Chunking/Manifest Namespaces"
Cohesion: 0.14
Nodes (7): Castr.Core.Tests.Manifest, Castr.Core.Manifest, Castr.Core.Tests.Chunking, Castr.Core.Chunking, MerkleProofStep, MerkleSide, ManifestFileEntry

### Community 14 - "Chunk Bitmap"
Cohesion: 0.18
Nodes (7): ChunkBitmap, byte, IEnumerable, ChunkBitmapTests, Fact, InlineData, Theory

### Community 15 - "In-Memory Trust Store"
Cohesion: 0.18
Nodes (10): InMemoryTrustStore, Dictionary, IReadOnlyList, ITrustStore, IReadOnlyList, TrustEntry, IEnumerable, IReadOnlyList (+2 more)

### Community 16 - "Trust & Security Namespaces"
Cohesion: 0.16
Nodes (8): Castr.Core.Trust, Castr.Core.Tests.Security, Castr.Core.Tests.Trust, Castr.Core.Security, TrustDecisionEngine, TrustEntrySource, TrustStatus, TrustSeedMerger

### Community 17 - "Real UDP Unicast Transport"
Cohesion: 0.17
Nodes (13): IUnicastTransport, Endpoint, UdpUnicastTransport, CancellationToken, EndPoint, IAsyncEnumerable, ReadOnlyMemory, ReceivedPacket (+5 more)

### Community 18 - "Trust Store JSON Codec"
Cohesion: 0.16
Nodes (10): JsonSerializerOptions, TrustEntryDto, TrustStoreDocument, TrustStoreJsonCodec, DateTimeOffset, IReadOnlyList, List, TrustStoreJsonCodecTests (+2 more)

### Community 19 - "Trust Decision Outcome"
Cohesion: 0.24
Nodes (5): TrustDecision, TrustOutcome, UnknownSenderPolicy, TrustDecisionEngineTests, Fact

### Community 20 - "Transport Namespaces & Tests"
Cohesion: 0.21
Nodes (5): Castr.Core.Transport, Castr.Core.IntegrationTests, Castr.Core.Transport.Udp, Castr.Core.Tests.Transport, SmokeTest

### Community 21 - "Real UDP Multicast Transport"
Cohesion: 0.21
Nodes (10): IMulticastTransport, UdpMulticastTransport, CancellationToken, EndPoint, IAsyncEnumerable, IPEndPoint, ReadOnlyMemory, ReceivedPacket (+2 more)

### Community 22 - "File Sink Interface"
Cohesion: 0.14
Nodes (9): IFileSink, CancellationToken, ReadOnlyMemory, ValueTask, MemoryFileSink, byte, CancellationToken, ReadOnlyMemory (+1 more)

### Community 23 - "File-Backed Trust Store"
Cohesion: 0.23
Nodes (6): FileTrustStore, IReadOnlyList, string, FileTrustStoreTests, Fact, string

### Community 24 - "Protocol/Time Namespaces"
Cohesion: 0.24
Nodes (5): Castr.Core.Time, Castr.Core.Tests.Protocol, Castr.Core.Tests.TestSupport, Castr.Core.Protocol, ReceiverSessionOptions

### Community 26 - "Public Key Id Round-Trip"
Cohesion: 0.38
Nodes (3): ReadOnlySpan, PublicKeyIdTests, Fact

### Community 27 - "Wire Message Types"
Cohesion: 0.25
Nodes (7): AnnounceMessage, ChunkDataMessage, ChunkRequestMessage, ChunkResponseMessage, MessageType, TransferCompleteMessage, TransferOutcome

### Community 28 - "Clock Abstraction (Fake/Real)"
Cohesion: 0.39
Nodes (5): FakeClock, DateTimeOffset, ISystemClock, SystemClock, DateTimeOffset

### Community 30 - "CLI Test Placeholder"
Cohesion: 0.40
Nodes (3): Castr.Cli.Tests, UnitTest1, Fact

### Community 31 - "E2E Test Placeholder"
Cohesion: 0.40
Nodes (3): Castr.Core.E2ETests, UnitTest1, Fact

### Community 32 - "Multicast Interface Enumeration"
Cohesion: 0.40
Nodes (3): IPAddress, MulticastInterfaces, IReadOnlyList

### Community 33 - "Trust Seed Template"
Cohesion: 0.40
Nodes (4): comment, entries, $schema, version

### Community 36 - "Repo Layout Documentation"
Cohesion: 1.00
Nodes (3): Repo/solution layout (plan), Castr repo layout (README), Castr.Gui deliberately not yet scaffolded

## Knowledge Gaps
- **67 isolated node(s):** `net10.0`, `Microsoft.NET.Sdk`, `net10.0`, `Microsoft.NET.Sdk`, `Castr.Core.Discovery` (+62 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **4 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Castr.Core.Chunking` connect `Core Chunking/Manifest Namespaces` to `Filesystem Chunk Sink/Source`, `Chunk Reading & Hashing`, `Chunk Hash Value Type`, `Manifest Binary Codec`, `Transport Namespaces & Tests`, `File Sink Interface`, `Protocol/Time Namespaces`, `Wire Message Types`?**
  _High betweenness centrality (0.107) - this node is a cross-community bridge._
- **Why does `ReceiverSession` connect `Manifest Signing & Verification` to `Multicast Transport Interface`, `End-to-End Transfer Test Setup`, `Peer Table`, `Repair Coordinator`, `In-Memory Trust Store`, `Protocol/Time Namespaces`, `Clock Abstraction (Fake/Real)`?**
  _High betweenness centrality (0.102) - this node is a cross-community bridge._
- **Why does `Castr.Core.Protocol` connect `Protocol/Time Namespaces` to `Peer Table`, `Repair Coordinator`, `Core Chunking/Manifest Namespaces`, `Transport Namespaces & Tests`, `Wire Message Types`?**
  _High betweenness centrality (0.068) - this node is a cross-community bridge._
- **What connects `net10.0`, `Microsoft.NET.Sdk`, `net10.0` to the rest of the system?**
  _67 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Multicast Transport Interface` be split into smaller, more focused modules?**
  _Cohesion score 0.05755693581780538 - nodes in this community are weakly interconnected._
- **Should `Filesystem Chunk Sink/Source` be split into smaller, more focused modules?**
  _Cohesion score 0.06259426847662142 - nodes in this community are weakly interconnected._
- **Should `Project Conventions & Infra` be split into smaller, more focused modules?**
  _Cohesion score 0.09306122448979592 - nodes in this community are weakly interconnected._