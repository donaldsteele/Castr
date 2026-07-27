# Graph Report - c:/code/Castr  (2026-07-25)

## Corpus Check
- 68 files · ~90,006 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 2339 nodes · 4791 edges · 162 communities (108 shown, 54 thin omitted)
- Extraction: 93% EXTRACTED · 7% INFERRED · 0% AMBIGUOUS · INFERRED: 326 edges (avg confidence: 0.8)
- Token cost: 126,094 input · 0 output

## Community Hubs (Navigation)
- Swarm Pull Session & Protocol Messages
- Merkle Verification & Message Codec
- In-Memory Service Discovery
- Content Key Encryption
- Transport Interfaces & In-Memory Network
- Sender Session Protocol
- Transfer Progress & Dashboard UI
- Wiki Docs & Project Plan
- E2E Test Infrastructure (Docker)
- TCP/UDP Transport & Index Hardening
- End-to-End Transfer Tests
- Protocol & Swarm Module Overview
- Manifest Codec & Signing
- Chunk Packetizer & Assembler
- In-Memory Stream Network
- CLI Command Definitions
- Trust Prompt Implementations
- Android NSD Discovery
- Packet Reassembler & Wire Packetizer
- Real TCP Swarm Pull Tests
- GUI App Shell & ViewModels
- Swarm Receive Flow Tests
- Mobile Receive ViewModel
- CLI Program Bootstrap & Paths
- Swarm Receive ViewModel
- M1.5 Concepts & Hardening Notes
- SwarmPullSession Core Logic
- Chunk Bitmap
- ChunkHash (Chunking)
- iOS App Shell
- Receiver Session Core
- iOS NWBrowser Discovery
- Android App Shell
- TUI Dashboard End-to-End Tests
- Desktop App Shell & Identity
- Repair Coordinator Planning Tests
- Receive ViewModel (Desktop)
- Filesystem File Sink & Source
- Chunk Range & Layout
- Length-Prefixed Framer Stream Tests
- CLI Parsing Tests
- Trust Decision Engine
- Security & Trust Test Namespaces
- GUI Trust Prompt Dialog
- CLI Send & Chunk Size Validation
- Length-Prefixed Framer & Pull Chunk Ops
- PeerTable Discovery Integration
- TUI Throughput Sampler & Send Runner
- Manifest Admission
- Manifest Signing Tests
- Length-Prefixed Framer Tests
- TCP Stream Client & Multicast Interfaces
- In-Memory Network Chaos Injection
- PeerTable Unit Tests
- In-Memory Multicast Transport
- In-Memory Unicast Transport
- Send ViewModel (Desktop)
- Path Safety Tests
- Network Interfaces & Path Safety
- UDP Unicast Transport
- Trust Store JSON Codec
- Chunker & IFileSource
- IPeerTable Interface
- PeerTable Implementation
- ReceiverSession Message Handlers
- FileTrustStore & PublicKeyId
- InMemoryTrustStore & Seed Merger Tests
- FileSystemFileSink & IFileSink
- Chunking Module Overview
- DiscoveredPeer & ServiceType Tests
- ITrustStore & TrustSeedMerger
- Large Chunk Transfer Tests
- Project csproj Files
- TUI Chunk Heatmap
- UDP Transport & Integration Test Namespaces
- Swarm Serve Listener
- E2E README & M3 Hardening Summary
- FileTrustStore Tests
- TCP Stream Listener
- CLI Receive Runner
- CLI Transfer Preparation
- RepairCoordinator Core
- PublicKeyId Ed25519 Tests
- FakeClock & ISystemClock
- CLI Sender Identity
- Chunker Tests
- GUI Test Project Config
- MemoryFileSink
- IServiceDiscovery Interface
- GUI TransferBuilder
- CLI End-to-End Loopback Tests
- TrustStoreJsonCodec Tests
- Desktop Program Entry Point
- GUI InMemoryTransportFactory
- Android Project Config (csproj)
- GUI Project Config (csproj)
- Discovery Test Project Config
- In-App Trust Prompt
- RepairCoordinator Ranking & Endpoint Helpers
- GUI UdpTransportFactory
- UDP Unicast Transport Tests
- Core Project Config (csproj)
- Wiki Schema & Graph Ontology
- iOS Project Config (csproj)
- MemoryFileSource
- IStreamClient Interface
- IStreamListener Interface
- MainViewModel & ViewModelBase
- ReceiverSession Progress Tracking
- GUI ITransportFactory Interface
- Trusted Senders Seed File
- Project Plan Milestones & Infra
- GUI Test App Builder
- MobileReceiveViewModel Trust Denial
- ReceiveViewModel Trust Denial
- SwarmReceiveViewModel Trust Denial
- Project Plan Platform Quirks & Tech Stack
- Byte Type Node
- Project State Convention
- ContentKey Type Node
- Dictionary Type Node
- Factory Type Node
- GetSink Helper Node
- HashSet Type Node
- IAsyncEnumerable Type Node
- IFileSink Type Node
- IPeerTable Type Node
- ISystemClock Type Node
- JoinRequestMessage Type Node
- KeyGrantMessage Type Node
- ManifestMessage Type Node
- MemoryFileSink Type Node
- MerkleProof Type Node
- MerkleTree Type Node
- Repo Solution Layout Doc
- ReadOnlyMemory Type Node
- ReadOnlySpan Type Node
- SemaphoreSlim Type Node
- Signed Type Node
- SignedManifest Type Node
- Sources Node
- MessageCodec MerkleProof Param
- MessageCodec Stream Param
- ReceiverSession ChunkRequestMessage Param
- ReceiverSession MerkleProof Param
- ReceiverSession PublicKeyId Param
- ReceiverSession ReceivedPacket Param
- ReceiverSession TrustDecision Param
- SenderSession ChunkRequestMessage Param
- SenderSession IFileSource Param
- UdpMulticastTransport Endpoint Param
- UdpMulticastTransport IPEndPoint Param
- UdpMulticastTransport Socket Param
- CLI UnitTest1 Stub
- E2E UnitTest1 Stub
- EndToEndTransferTests Dictionary Param
- EndToEndTransferTests ReceivedPacket Param
- MessageCodecTests InlineData Param
- MessageCodecTests Theory Param
- Trees Node
- TrustDecision Type Node
- ValueTask Primitive

## God Nodes (most connected - your core abstractions)
1. `Castr.Core.Protocol` - 51 edges
2. `ReceiverSession` - 51 edges
3. `Castr.Core.Trust` - 43 edges
4. `Castr.Core.Transport` - 39 edges
5. `SwarmPullSession` - 35 edges
6. `SenderSession` - 33 edges
7. `SwarmReceiveViewModel` - 30 edges
8. `SwarmPullSessionTests` - 30 edges
9. `MobileReceiveViewModel` - 26 edges
10. `EndToEndTransferTests` - 26 edges

## Surprising Connections (you probably didn't know these)
- `DialogTrustPrompt` --semantically_similar_to--> `ConsoleTrustPrompt`  [INFERRED] [semantically similar]
  src/Castr.Gui/README.md → wiki/synthesis/m2-ui-summary.md
- `RepairCoordinator (plan)` --rationale_for--> `Castr repair protocol`  [INFERRED]
  raw/2026-07-24-castr-project-plan.md → wiki/concepts/repair-protocol.md
- `TrustDecisionEngineTests` --references--> `PublicKeyId`  [EXTRACTED]
  tests/Castr.Core.Tests/Trust/TrustDecisionEngineTests.cs → src/Castr.Core/Security/PublicKeyId.cs
- `TrustSeedMergerTests` --references--> `PublicKeyId`  [EXTRACTED]
  tests/Castr.Core.Tests/Trust/TrustSeedMergerTests.cs → src/Castr.Core/Security/PublicKeyId.cs
- `RepairCoordinatorTests` --references--> `Endpoint`  [EXTRACTED]
  tests/Castr.Core.Tests/Protocol/RepairCoordinatorTests.cs → src/Castr.Core/Transport/Endpoint.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Manifest/chunk verification chain (Ed25519 + BLAKE3 + Merkle + AEAD)** — wiki_concepts_security_model_ed25519_signing, wiki_concepts_tech_stack_blake3_hashing, wiki_concepts_wire_protocol_merkleproof_verify, wiki_concepts_security_model_chacha20poly1305 [INFERRED 0.85]
- **Mobile swarm-pull tier components (M4)** — wiki_concepts_wire_protocol_swarmpullsession, wiki_concepts_wire_protocol_swarmservelistener, wiki_concepts_wire_protocol_lengthprefixedframer, wiki_concepts_wire_protocol_manifestadmission [EXTRACTED 1.00]
- **Dedicated, deliberately-separate mobile CI workflows** — github_workflows_ci_mobile_android_workflow, github_workflows_ci_mobile_ios_workflow, github_workflows_ci_workflow [INFERRED 0.85]
- **M3 test/CI hardening milestone deliverables** — wiki_synthesis_m3_test_ci_hardening_summary, tests_castr_core_e2etests_readme [EXTRACTED 0.90]
- **M2 Core observability & trust-prompt contract** — concept_sendersession, concept_receiversession, concept_dialogtrustprompt, concept_consoletrustprompt, concept_trustdecisionengine [EXTRACTED 1.00]
- **M1.5 payload-encryption feature (decision, implementation, and mechanism)** — wiki_synthesis_adr_0003_payload_encryption, wiki_synthesis_m1_5_encryption_summary [INFERRED 0.85]
- **Castr core architecture concept cluster** — wiki_entities_castr_project_castr, wiki_concepts_repair_protocol_repair_protocol [EXTRACTED 1.00]
- **Definition-of-done gate applied at every milestone** — raw_2026_07_24_castr_project_plan_milestones, raw_2026_07_24_castr_project_plan_graphify_llm_wiki_infra [EXTRACTED 1.00]

## Communities (162 total, 54 thin omitted)

### Community 0 - "Swarm Pull Session & Protocol Messages"
Cohesion: 0.05
Nodes (55): AnnounceMessage, ChunkDataMessage, ChunkRequestMessage, ChunkResponseMessage, JoinRequestMessage, KeyGrantMessage, KeyUnavailableMessage, ManifestMessage (+47 more)

### Community 1 - "Merkle Verification & Message Codec"
Cohesion: 0.06
Nodes (28): ChunkHash, InlineData, ReceivedPacket, SpanReader, ChunkHash, MerkleProof, MerkleProofStep, MerkleSide (+20 more)

### Community 2 - "In-Memory Service Discovery"
Cohesion: 0.06
Nodes (41): Action, IServiceDiscovery, SinkHolder, InMemoryDiscoveryNetwork, ChannelReader, Dictionary, DiscoveredPeer, Endpoint (+33 more)

### Community 3 - "Content Key Encryption"
Cohesion: 0.06
Nodes (34): KeyAgreementAlgorithm, KeyDerivationAlgorithm, SharedSecret, ContentKey, AeadAlgorithm, int, Key, ReadOnlySpan (+26 more)

### Community 4 - "Transport Interfaces & In-Memory Network"
Cohesion: 0.06
Nodes (36): IAsyncDisposable, ReceivedPacket, IMulticastTransport, CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask, Endpoint (+28 more)

### Community 5 - "Sender Session Protocol"
Cohesion: 0.08
Nodes (34): IFileSource, ITrustPrompt, SenderContentSource, SenderSession, bool, CancellationToken, HashSet, int (+26 more)

### Community 6 - "Transfer Progress & Dashboard UI"
Cohesion: 0.06
Nodes (32): Color, double, ConsoleProgressReporter, int, object, TransferPhase, TransferProgress, TransferRole (+24 more)

### Community 7 - "Wiki Docs & Project Plan"
Cohesion: 0.07
Nodes (56): CI (Mobile / Android) workflow, CI (Mobile / iOS) workflow, CI workflow (cross-OS matrix, E2E, package), Core-first build phasing decision, Avalonia GUI framework decision, IPeerTable abstraction (plan), Mobile unicast swarm client decision, Path safety (no traversal) decision (+48 more)

### Community 8 - "E2E Test Infrastructure (Docker)"
Cohesion: 0.06
Nodes (31): Castr.Core.E2ETests, Castr.Core.E2ETests.Infrastructure, E2EFact, FactAttribute, IAsyncLifetime, ICollectionFixture, IContainer, IFutureDockerImage (+23 more)

### Community 9 - "TCP/UDP Transport & Index Hardening"
Cohesion: 0.06
Nodes (31): EndPoint, IMulticastTransport, IPEndPoint, IStreamConnection, SessionId, Socket, TcpStreamConnection, CancellationToken (+23 more)

### Community 10 - "End-to-End Transfer Tests"
Cohesion: 0.13
Nodes (22): ChunkAndLeafIndexSwappingTransport, ChunkPositionSwappingMulticastTransport, EndToEndTransferTests, TamperingMulticastTransport, Transfer, bool, CancellationToken, Fact (+14 more)

### Community 11 - "Protocol & Swarm Module Overview"
Cohesion: 0.10
Nodes (12): Castr.Core.Swarm, Castr.Core.Transport, Castr.Core.Tests.Manifest, Castr.Core.Transport.InMemory, Castr.Core.Manifest, Castr.Core.Tests.Protocol, Castr.Core.Tests.TestSupport, Castr.Gui.Tests (+4 more)

### Community 12 - "Manifest Codec & Signing"
Cohesion: 0.09
Nodes (14): ManifestCodec, SpanReader, byte, ReadOnlySpan, Stream, ManifestSigner, Key, ManifestVerifier (+6 more)

### Community 13 - "Chunk Packetizer & Assembler"
Cohesion: 0.10
Nodes (20): Ciphertext, ChunkPacketAssembler, Partial, byte, Dictionary, int, long, MerkleProof (+12 more)

### Community 14 - "In-Memory Stream Network"
Cohesion: 0.10
Nodes (23): Channel, InMemoryStreamClient, InMemoryStreamConnection, InMemoryStreamListener, InMemoryStreamNetwork, byte, CancellationToken, ChannelReader (+15 more)

### Community 15 - "CLI Command Definitions"
Cohesion: 0.12
Nodes (14): Command, Option, CastrCli, IAnsiConsole, IPAddress, RootCommand, TrustStatus, TrustRunner (+6 more)

### Community 16 - "Trust Prompt Implementations"
Cohesion: 0.11
Nodes (19): ConsoleTrustPrompt, CancellationToken, Task, ITrustPrompt, TrustPromptContext, CancellationToken, Task, AutoTrustPrompt (+11 more)

### Community 17 - "Android NSD Discovery"
Cohesion: 0.09
Nodes (18): DiscoveryListener, IDiscoveryListener, IRegistrationListener, IResolveListener, NsdFailure, NsdManager, NsdServiceInfo, Object (+10 more)

### Community 18 - "Packet Reassembler & Wire Packetizer"
Cohesion: 0.14
Nodes (13): PacketReassembler, Partial, byte, Dictionary, int, long, WirePacketizer, int (+5 more)

### Community 19 - "Real TCP Swarm Pull Tests"
Cohesion: 0.17
Nodes (15): RealTcpSwarmPullTests, ServeHandle, Transfer, Endpoint, Fact, Factory, Func, GetSink (+7 more)

### Community 20 - "GUI App Shell & ViewModels"
Cohesion: 0.12
Nodes (10): Castr.Gui.Services, Castr.Gui, Castr.Gui.ViewModels, Castr.Gui.Trust, Castr.Gui.Views, MainWindowTests, AvaloniaFact, TransferFlowTests (+2 more)

### Community 21 - "Swarm Receive Flow Tests"
Cohesion: 0.15
Nodes (15): ServedTransfer, ServedTransfer, SwarmReceiveFlowTests, AvaloniaFact, Endpoint, Factory, Func, GetSink (+7 more)

### Community 22 - "Mobile Receive ViewModel"
Cohesion: 0.11
Nodes (16): DiscoveredPeerItem, MobileReceiveViewModel, bool, CancellationToken, CancellationTokenSource, Endpoint, Func, HashSet (+8 more)

### Community 23 - "CLI Program Bootstrap & Paths"
Cohesion: 0.12
Nodes (13): Castr.Cli.Tests, Castr.Core.Trust, Castr.Cli, FileTrustStore, Merged, CastrPaths, int, IPAddress (+5 more)

### Community 24 - "Swarm Receive ViewModel"
Cohesion: 0.11
Nodes (16): SwarmPullSessionOptions, SwarmReceiveViewModel, bool, byte, CancellationToken, CancellationTokenSource, Func, HashSet (+8 more)

### Community 25 - "M1.5 Concepts & Hardening Notes"
Cohesion: 0.16
Nodes (23): Castr.Cli --chunk-size fail-fast guard, ConsoleTrustPrompt, ContentKey.EncryptChunk, DialogTrustPrompt, InMemoryTransportFactory, ITransportFactory, ReceiverSession, SenderSession (+15 more)

### Community 26 - "SwarmPullSession Core Logic"
Cohesion: 0.10
Nodes (18): IEnumerable, SwarmPullSession, byte, ContentKey, Dictionary, Func, HashSet, int (+10 more)

### Community 27 - "Chunk Bitmap"
Cohesion: 0.17
Nodes (7): ChunkBitmap, byte, IEnumerable, ChunkBitmapTests, Fact, InlineData, Theory

### Community 28 - "ChunkHash (Chunking)"
Cohesion: 0.18
Nodes (7): IEquatable, ChunkHash, byte, int, ReadOnlySpan, ChunkHashTests, Fact

### Community 29 - "iOS App Shell"
Cohesion: 0.10
Nodes (9): AvaloniaAppDelegate, Castr.Gui.iOS, App, AppDelegate, AppBuilder, Application, MobileReceiveView, SwarmReceiveView (+1 more)

### Community 30 - "Receiver Session Core"
Cohesion: 0.10
Nodes (20): ChunkPacketAssembler, RepairCoordinator, ReceiverContentSource, ReceiverSession, ReceiverSessionOptions, byte, ContentKey, Dictionary (+12 more)

### Community 31 - "iOS NWBrowser Discovery"
Cohesion: 0.11
Nodes (14): DispatchQueue, NWBrowser, NWBrowseResult, NWConnection, NWListener, DiscoveredPeer, NetworkServiceDiscovery, bool (+6 more)

### Community 32 - "Android App Shell"
Cohesion: 0.11
Nodes (11): AvaloniaAndroidApplication, AvaloniaMainActivity, Control, Castr.Gui.Android, Castr.Core.Transport.Tcp, IDataTemplate, App, MainActivity (+3 more)

### Community 33 - "TUI Dashboard End-to-End Tests"
Cohesion: 0.15
Nodes (12): Transfer, TransferDashboardEndToEndTests, CancellationToken, Fact, Factory, Func, GetSink, IFileSink (+4 more)

### Community 34 - "Desktop App Shell & Identity"
Cohesion: 0.13
Nodes (10): Application, IClassicDesktopStyleApplicationLifetime, App, CastrIdentity, Key, StoragePickers, Task, MainWindow (+2 more)

### Community 35 - "Repair Coordinator Planning Tests"
Cohesion: 0.23
Nodes (6): IReadOnlyCollection, Func, TimeSpan, RepairCoordinatorTests, DateTimeOffset, Fact

### Community 36 - "Receive ViewModel (Desktop)"
Cohesion: 0.15
Nodes (14): IReadOnlyList, ReceiveViewModel, bool, CancellationToken, CancellationTokenSource, Func, IMulticastTransport, int (+6 more)

### Community 37 - "Filesystem File Sink & Source"
Cohesion: 0.18
Nodes (10): CancellationToken, ReadOnlyMemory, ValueTask, CancellationToken, Memory, ValueTask, FileSystemFileSourceSinkTests, Fact (+2 more)

### Community 38 - "Chunk Range & Layout"
Cohesion: 0.20
Nodes (8): ChunkLayout, ChunkRange, IEnumerable, int, ChunkLayoutTests, Fact, InlineData, Theory

### Community 39 - "Length-Prefixed Framer Stream Tests"
Cohesion: 0.16
Nodes (12): IStreamConnection, CancellationToken, Endpoint, Memory, ReadOnlyMemory, ValueTask, ChoppyConnection, CancellationToken (+4 more)

### Community 40 - "CLI Parsing Tests"
Cohesion: 0.24
Nodes (6): ParsingTests, Fact, InlineData, RootCommand, Theory, UnknownSenderPolicy

### Community 41 - "Trust Decision Engine"
Cohesion: 0.23
Nodes (5): TrustDecision, TrustOutcome, UnknownSenderPolicy, TrustDecisionEngineTests, Fact

### Community 42 - "Security & Trust Test Namespaces"
Cohesion: 0.14
Nodes (6): Castr.Core.Tests.Security, Castr.Core.Tests.Trust, Castr.Core.Security, TrustDecisionEngine, TrustEntrySource, TrustStatus

### Community 43 - "GUI Trust Prompt Dialog"
Cohesion: 0.16
Nodes (7): EventArgs, TrustPromptViewModel, RelayCommand, Task, TrustPromptDialog, TaskCompletionSource, WindowClosingEventArgs

### Community 44 - "CLI Send & Chunk Size Validation"
Cohesion: 0.28
Nodes (8): SendOptions, CancellationToken, IAnsiConsole, Task, ChunkSizeValidationTests, Fact, string, Task

### Community 45 - "Length-Prefixed Framer & Pull Chunk Ops"
Cohesion: 0.29
Nodes (8): LengthPrefixedFramer, CancellationToken, int, Memory, ReadOnlyMemory, ValueTask, CancellationToken, Task

### Community 46 - "PeerTable Discovery Integration"
Cohesion: 0.28
Nodes (4): PeerTableDiscoveryTests, DateTimeOffset, Endpoint, Fact

### Community 47 - "TUI Throughput Sampler & Send Runner"
Cohesion: 0.15
Nodes (8): Castr.Tui.Tests, Castr.Tui, Queue, SendRunner, ThroughputSampler, Func, TimeSpan, Stopwatch

### Community 48 - "Manifest Admission"
Cohesion: 0.21
Nodes (12): ManifestAdmission, ManifestAdmissionOutcome, ManifestAdmissionResult, CancellationToken, ISystemClock, ITrustPrompt, ITrustStore, PublicKeyId (+4 more)

### Community 49 - "Manifest Signing Tests"
Cohesion: 0.31
Nodes (3): ManifestSigningTests, Fact, TransferManifest

### Community 50 - "Length-Prefixed Framer Tests"
Cohesion: 0.40
Nodes (5): Client, Server, LengthPrefixedFramerTests, Fact, Task

### Community 51 - "TCP Stream Client & Multicast Interfaces"
Cohesion: 0.18
Nodes (9): IPAddress, IStreamClient, TcpStreamClient, CancellationToken, Endpoint, IStreamConnection, ValueTask, MulticastInterfaces (+1 more)

### Community 52 - "In-Memory Network Chaos Injection"
Cohesion: 0.18
Nodes (10): Lock, ChaosOptions, InMemoryNetwork, ChannelWriter, Dictionary, List, Random, ReceivedPacket (+2 more)

### Community 53 - "PeerTable Unit Tests"
Cohesion: 0.48
Nodes (3): PeerTableTests, DateTimeOffset, Fact

### Community 54 - "In-Memory Multicast Transport"
Cohesion: 0.19
Nodes (8): InMemoryMulticastTransport, CancellationToken, Channel, ChannelWriter, IAsyncEnumerable, ReadOnlyMemory, ReceivedPacket, ValueTask

### Community 55 - "In-Memory Unicast Transport"
Cohesion: 0.19
Nodes (9): InMemoryUnicastTransport, CancellationToken, Channel, ChannelWriter, Endpoint, IAsyncEnumerable, ReadOnlyMemory, ReceivedPacket (+1 more)

### Community 56 - "Send ViewModel (Desktop)"
Cohesion: 0.21
Nodes (10): SendViewModel, bool, CancellationTokenSource, Func, IMulticastTransport, int, Key, RelayCommand (+2 more)

### Community 57 - "Path Safety Tests"
Cohesion: 0.23
Nodes (5): PathSafetyTests, Fact, InlineData, string, Theory

### Community 58 - "Network Interfaces & Path Safety"
Cohesion: 0.18
Nodes (7): Exception, InvalidInterfaceException, NetworkInterfaces, IPAddress, PathSafety, PathTraversalException, PromptBoomException

### Community 59 - "UDP Unicast Transport"
Cohesion: 0.23
Nodes (9): IUnicastTransport, UdpUnicastTransport, CancellationToken, EndPoint, IAsyncEnumerable, ReadOnlyMemory, ReceivedPacket, Socket (+1 more)

### Community 60 - "Trust Store JSON Codec"
Cohesion: 0.23
Nodes (9): JsonSerializerOptions, TrustEntry, TrustEntryDto, TrustStoreDocument, TrustStoreJsonCodec, DateTimeOffset, IReadOnlyList, List (+1 more)

### Community 61 - "Chunker & IFileSource"
Cohesion: 0.26
Nodes (9): Chunker, CancellationToken, Memory, Task, ValueTask, IFileSource, CancellationToken, Memory (+1 more)

### Community 62 - "IPeerTable Interface"
Cohesion: 0.22
Nodes (7): IPeerTable, PeerInfo, DateTimeOffset, Endpoint, int, IReadOnlyList, PeerHaveMessage

### Community 63 - "PeerTable Implementation"
Cohesion: 0.22
Nodes (7): PeerEntry, PeerTable, DateTimeOffset, Dictionary, Endpoint, IReadOnlyList, TimeSpan

### Community 65 - "FileTrustStore & PublicKeyId"
Cohesion: 0.21
Nodes (5): PublicKeyId, string, FileTrustStore, IReadOnlyList, string

### Community 66 - "InMemoryTrustStore & Seed Merger Tests"
Cohesion: 0.26
Nodes (5): InMemoryTrustStore, Dictionary, IReadOnlyList, TrustSeedMergerTests, Fact

### Community 67 - "FileSystemFileSink & IFileSink"
Cohesion: 0.17
Nodes (8): bool, FileSystemFileSink, SafeFileHandle, string, IFileSink, CancellationToken, ReadOnlyMemory, ValueTask

### Community 68 - "Chunking Module Overview"
Cohesion: 0.21
Nodes (4): Castr.Core.Tests.Chunking, Castr.Core.Chunking, FileSystemFileSource, SafeFileHandle

### Community 69 - "DiscoveredPeer & ServiceType Tests"
Cohesion: 0.21
Nodes (5): Castr.Core.Discovery.Tests, DiscoveredPeerTests, Fact, ServiceTypeTests, Fact

### Community 70 - "ITrustStore & TrustSeedMerger"
Cohesion: 0.20
Nodes (5): ITrustStore, IReadOnlyList, TrustSeedMerger, IEnumerable, IReadOnlyList

### Community 71 - "Large Chunk Transfer Tests"
Cohesion: 0.32
Nodes (6): LargeChunkTransferTests, CancellationToken, Fact, IPAddress, Task, TimeSpan

### Community 72 - "Project csproj Files"
Cohesion: 0.18
Nodes (8): Castr.Cli, Castr.Gui.Desktop, Castr.Tui, Castr.Cli.Tests, Castr.Core.E2ETests, Castr.Core.IntegrationTests, Castr.Core.Tests, Castr.Tui.Tests

### Community 73 - "TUI Chunk Heatmap"
Cohesion: 0.24
Nodes (7): char, IRenderable, Measurement, RenderOptions, Segment, ChunkHeatmap, Style

### Community 74 - "UDP Transport & Integration Test Namespaces"
Cohesion: 0.22
Nodes (3): Castr.Core.IntegrationTests, Castr.Core.Transport.Udp, SmokeTest

### Community 75 - "Swarm Serve Listener"
Cohesion: 0.44
Nodes (5): ChunkPullRequestMessage, ChunkPullResponseMessage, SwarmServeListener, CancellationToken, Task

### Community 76 - "E2E README & M3 Hardening Summary"
Cohesion: 0.22
Nodes (11): Castr.Core.E2ETests README (container fan-out E2E tier), [E2EFact] opt-in gating (CASTR_E2E env var + reachable Docker), tc netem MTU-size-matched real packet loss injection, M3 — Test/CI hardening: implementation summary, Real chunk/wire-packet split implementation (WirePacketizer/ChunkPacketizer), Testcontainers E2E fan-out suite (real Docker bridge multicast + tc netem loss), macOS CI fix: UdpMulticastTransport missing IP_MULTICAST_IF (broken since M1), Reassembly memory-exhaustion DoS fix (unbounded PacketCount allocation) (+3 more)

### Community 77 - "FileTrustStore Tests"
Cohesion: 0.27
Nodes (3): FileTrustStoreTests, Fact, string

### Community 78 - "TCP Stream Listener"
Cohesion: 0.22
Nodes (7): IStreamListener, TcpStreamListener, CancellationToken, Endpoint, IStreamConnection, ValueTask, TcpListener

### Community 79 - "CLI Receive Runner"
Cohesion: 0.40
Nodes (5): ReceiveOptions, ReceiveRunner, CancellationToken, IAnsiConsole, Task

### Community 80 - "CLI Transfer Preparation"
Cohesion: 0.22
Nodes (6): PreparedTransfer, TransferPreparation, CancellationToken, IMulticastTransport, Key, Task

### Community 81 - "RepairCoordinator Core"
Cohesion: 0.22
Nodes (6): RepairCoordinator, RepairOptions, RepairRequestPlan, DateTimeOffset, Dictionary, Random

### Community 82 - "PublicKeyId Ed25519 Tests"
Cohesion: 0.38
Nodes (3): ReadOnlySpan, PublicKeyIdTests, Fact

### Community 83 - "FakeClock & ISystemClock"
Cohesion: 0.36
Nodes (6): Castr.Core.Time, FakeClock, DateTimeOffset, ISystemClock, SystemClock, DateTimeOffset

### Community 84 - "CLI Sender Identity"
Cohesion: 0.22
Nodes (5): IDisposable, SenderIdentity, Key, PublicKeyId, TempDir

### Community 85 - "Chunker Tests"
Cohesion: 0.58
Nodes (3): ChunkerTests, Fact, Task

### Community 86 - "GUI Test Project Config"
Cohesion: 0.25
Nodes (8): Avalonia.Headless.XUnit (12.1.0), xunit.v3 (3.2.2), Castr.Gui.Tests, net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.12.0), xunit.runner.visualstudio (3.1.4), Microsoft.NET.Sdk

### Community 87 - "MemoryFileSink"
Cohesion: 0.25
Nodes (5): MemoryFileSink, byte, CancellationToken, ReadOnlyMemory, ValueTask

### Community 88 - "IServiceDiscovery Interface"
Cohesion: 0.29
Nodes (5): IServiceDiscovery, CancellationToken, IAsyncEnumerable, string, Task

### Community 89 - "GUI TransferBuilder"
Cohesion: 0.25
Nodes (6): PreparedTransfer, TransferBuilder, CancellationToken, IMulticastTransport, Key, Task

### Community 90 - "CLI End-to-End Loopback Tests"
Cohesion: 0.32
Nodes (5): EndToEndLoopbackTests, Fact, string, Task, TimeSpan

### Community 92 - "Desktop Program Entry Point"
Cohesion: 0.33
Nodes (4): Castr.Gui.Desktop, Program, AppBuilder, STAThread

### Community 93 - "GUI InMemoryTransportFactory"
Cohesion: 0.33
Nodes (4): InMemoryNetwork, InMemoryTransportFactory, IMulticastTransport, int

### Community 94 - "Android Project Config (csproj)"
Cohesion: 0.29
Nodes (7): Avalonia.Android (12.1.0), Castr.Gui.Android, net10.0-android, Avalonia (12.1.0), Avalonia.Fonts.Inter (12.1.0), Avalonia.Themes.Fluent (12.1.0), Microsoft.NET.Sdk

### Community 95 - "GUI Project Config (csproj)"
Cohesion: 0.29
Nodes (7): CommunityToolkit.Mvvm (8.4.2), Castr.Gui, net10.0, Avalonia (12.1.0), Avalonia.Fonts.Inter (12.1.0), Avalonia.Themes.Fluent (12.1.0), Microsoft.NET.Sdk

### Community 96 - "Discovery Test Project Config"
Cohesion: 0.29
Nodes (7): xunit (2.5.3), Castr.Core.Discovery.Tests, net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.8.0), xunit.runner.visualstudio (2.5.3), Microsoft.NET.Sdk

### Community 97 - "In-App Trust Prompt"
Cohesion: 0.29
Nodes (6): ObservableObject, InAppTrustPrompt, CancellationToken, Task, TrustPromptContext, TrustPromptViewModel

### Community 98 - "RepairCoordinator Ranking & Endpoint Helpers"
Cohesion: 0.33
Nodes (3): IReadOnlyList, Endpoint, IPEndPoint

### Community 99 - "GUI UdpTransportFactory"
Cohesion: 0.33
Nodes (4): UdpTransportFactory, IMulticastTransport, int, IPAddress

### Community 100 - "UDP Unicast Transport Tests"
Cohesion: 0.57
Nodes (3): UdpUnicastTransportTests, Fact, Task

### Community 101 - "Core Project Config (csproj)"
Cohesion: 0.33
Nodes (6): Castr.Core, Castr.Core.Discovery, net10.0, net10.0-android, net10.0-ios, Microsoft.NET.Sdk

### Community 102 - "Wiki Schema & Graph Ontology"
Cohesion: 0.40
Nodes (6): LLM Wiki maintenance convention, Wiki page template, Wiki Graph Ontology, Wiki Graph Layer docs, Wiki optional graph metadata (graph: key), Wiki Schema (conventions)

### Community 103 - "iOS Project Config (csproj)"
Cohesion: 0.33
Nodes (6): Avalonia.iOS (12.1.0), Castr.Gui.iOS, net10.0, net10.0-ios, Avalonia.Fonts.Inter (12.1.0), Microsoft.NET.Sdk

### Community 104 - "MemoryFileSource"
Cohesion: 0.33
Nodes (4): MemoryFileSource, CancellationToken, Memory, ValueTask

### Community 105 - "IStreamClient Interface"
Cohesion: 0.33
Nodes (4): IStreamClient, CancellationToken, Endpoint, ValueTask

### Community 106 - "IStreamListener Interface"
Cohesion: 0.33
Nodes (4): IStreamListener, CancellationToken, Endpoint, ValueTask

### Community 107 - "MainViewModel & ViewModelBase"
Cohesion: 0.33
Nodes (3): MainViewModel, int, ViewModelBase

### Community 110 - "Trusted Senders Seed File"
Cohesion: 0.40
Nodes (4): comment, entries, $schema, version

### Community 111 - "Project Plan Milestones & Infra"
Cohesion: 0.50
Nodes (4): graphify codebase graph convention, graphify + llm-wiki mandatory infrastructure (plan), Milestone plan M0-M5 (plan), Verification/testing approach (plan)

## Knowledge Gaps
- **102 isolated node(s):** `TrustOutcome`, `TrustEntrySource`, `Castr.Core.E2ETests`, `Castr.Core.Tests.TestSupport`, `$schema` (+97 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **54 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Castr.Core.Protocol` connect `Protocol & Swarm Module Overview` to `Swarm Pull Session & Protocol Messages`, `Transfer Progress & Dashboard UI`, `UDP Transport & Integration Test Namespaces`, `Chunk Packetizer & Assembler`, `CLI Receive Runner`, `CLI Transfer Preparation`, `RepairCoordinator Core`, `Packet Reassembler & Wire Packetizer`, `TUI Throughput Sampler & Send Runner`, `GUI App Shell & ViewModels`, `Chunk Bitmap`, `iOS App Shell`, `IPeerTable Interface`?**
  _High betweenness centrality (0.200) - this node is a cross-community bridge._
- **Why does `Castr.Core.Trust` connect `CLI Program Bootstrap & Paths` to `Android App Shell`, `FileTrustStore & PublicKeyId`, `InMemoryTrustStore & Seed Merger Tests`, `ITrustStore & TrustSeedMerger`, `Trust Decision Engine`, `Security & Trust Test Namespaces`, `Protocol & Swarm Module Overview`, `GUI Trust Prompt Dialog`, `UDP Transport & Integration Test Namespaces`, `CLI Command Definitions`, `Trust Prompt Implementations`, `CLI Receive Runner`, `Manifest Admission`, `TUI Throughput Sampler & Send Runner`, `GUI App Shell & ViewModels`, `iOS App Shell`?**
  _High betweenness centrality (0.135) - this node is a cross-community bridge._
- **Why does `Castr.Core.Transport` connect `Protocol & Swarm Module Overview` to `Android App Shell`, `RepairCoordinator Ranking & Endpoint Helpers`, `Transport Interfaces & In-Memory Network`, `DiscoveredPeer & ServiceType Tests`, `Length-Prefixed Framer Stream Tests`, `IStreamClient Interface`, `IStreamListener Interface`, `UDP Transport & Integration Test Namespaces`, `RepairCoordinator Core`, `Android NSD Discovery`, `IPeerTable Interface`, `iOS NWBrowser Discovery`?**
  _High betweenness centrality (0.102) - this node is a cross-community bridge._
- **What connects `TrustOutcome`, `TrustEntrySource`, `Castr.Core.E2ETests` to the rest of the system?**
  _102 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Swarm Pull Session & Protocol Messages` be split into smaller, more focused modules?**
  _Cohesion score 0.0502970297029703 - nodes in this community are weakly interconnected._
- **Should `Merkle Verification & Message Codec` be split into smaller, more focused modules?**
  _Cohesion score 0.06177076183939602 - nodes in this community are weakly interconnected._
- **Should `In-Memory Service Discovery` be split into smaller, more focused modules?**
  _Cohesion score 0.06351236146632566 - nodes in this community are weakly interconnected._