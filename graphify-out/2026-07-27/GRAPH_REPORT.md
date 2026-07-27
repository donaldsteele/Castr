# Graph Report - Castr  (2026-07-27)

## Corpus Check
- 259 files · ~387,247 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 2924 nodes · 7220 edges · 181 communities (166 shown, 15 thin omitted)
- Extraction: 92% EXTRACTED · 8% INFERRED · 0% AMBIGUOUS · INFERRED: 586 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `4e6530e1`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

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
- Trees Node
- TrustDecision Type Node
- ValueTask Primitive
- Castr in action
- CastrPaths
- DatagramFilters
- .SendAsync
- .PickFileAsync
- capture-gui.ps1
- Demo capture scripts
- App
- Castr.Gui.Android
- 2026-07-26 — First post-reboot wire measurement: the router cap confirmed, and Castr is near it
- Castr.Core.csproj
- .ReceiveAsync
- ServiceTypeTests
- ExitCodes.cs
- Castr.Cli.Tests/TestPorts.cs

## God Nodes (most connected - your core abstractions)
1. `ReceiverSession` - 75 edges
2. `Castr.Core.Protocol` - 63 edges
3. `Castr.Core.Transport` - 60 edges
4. `Castr.Core.Chunking` - 59 edges
5. `Castr.Core.Security` - 57 edges
6. `Castr.Core.Trust` - 57 edges
7. `Castr.Core.Manifest` - 46 edges
8. `SwarmPullSession` - 41 edges
9. `ReceiverSessionGossipAndRepairTests` - 40 edges
10. `RepairCoordinatorTests` - 39 edges

## Surprising Connections (you probably didn't know these)
- `DialogTrustPrompt` --semantically_similar_to--> `ConsoleTrustPrompt`  [INFERRED] [semantically similar]
  src/Castr.Gui/README.md → wiki/synthesis/m2-ui-summary.md
- `RepairCoordinator (plan)` --rationale_for--> `Castr repair protocol`  [INFERRED]
  raw/2026-07-24-castr-project-plan.md → wiki/concepts/repair-protocol.md
- `SinkHolder` --references--> `MemoryFileSink`  [EXTRACTED]
  tests/Castr.Gui.Tests/MobileReceiveFlowTests.cs → src/Castr.Core/Chunking/MemoryFileSink.cs
- `OutboundRecorder` --references--> `JoinRequestMessage`  [EXTRACTED]
  tests/Castr.Core.Tests/Protocol/ReceiverSessionChunkCacheTests.cs → src/Castr.Core/Protocol/Messages.cs
- `RecordedOutbound` --references--> `ChunkRequestMessage`  [EXTRACTED]
  tests/Castr.Core.Tests/Protocol/ReceiverSessionGossipAndRepairTests.cs → src/Castr.Core/Protocol/Messages.cs

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

## Communities (181 total, 15 thin omitted)

### Community 0 - "Swarm Pull Session & Protocol Messages"
Cohesion: 0.06
Nodes (38): SignedManifest, JoinRequestMessage, KeyGrantMessage, ReceiverContentSource, SenderContentSource, ISwarmContentSource, SwarmChunk, CancellationToken (+30 more)

### Community 1 - "Merkle Verification & Message Codec"
Cohesion: 0.27
Nodes (4): MessageCodecTests, Fact, InlineData, Theory

### Community 2 - "In-Memory Service Discovery"
Cohesion: 0.08
Nodes (34): IServiceDiscovery, SinkHolder, InMemoryDiscoveryNetwork, Action, ChannelReader, Dictionary, DiscoveredPeer, List (+26 more)

### Community 3 - "Content Key Encryption"
Cohesion: 0.18
Nodes (9): EncryptionKeys, int, Key, PayloadEncryptionTests, byte, Fact, InlineData, Task (+1 more)

### Community 4 - "Transport Interfaces & In-Memory Network"
Cohesion: 0.29
Nodes (6): IUnicastTransport, IUnicastTransport, InMemoryNetworkTests, Fact, Task, TimeSpan

### Community 5 - "Sender Session Protocol"
Cohesion: 0.12
Nodes (19): MerkleTree, SenderSession, bool, CancellationToken, HashSet, int, long, object (+11 more)

### Community 6 - "Transfer Progress & Dashboard UI"
Cohesion: 0.37
Nodes (4): TransferDashboardRendererTests, Fact, InlineData, Theory

### Community 7 - "Wiki Docs & Project Plan"
Cohesion: 0.07
Nodes (56): CI (Mobile / Android) workflow, CI (Mobile / iOS) workflow, CI workflow (cross-OS matrix, E2E, package), Core-first build phasing decision, Avalonia GUI framework decision, IPeerTable abstraction (plan), Mobile unicast swarm client decision, Path safety (no traversal) decision (+48 more)

### Community 8 - "E2E Test Infrastructure (Docker)"
Cohesion: 0.06
Nodes (29): Castr.Core.E2ETests, Castr.Core.E2ETests.Infrastructure, E2EFact, FactAttribute, IAsyncLifetime, ICollectionFixture, IContainer, IFutureDockerImage (+21 more)

### Community 9 - "TCP/UDP Transport & Index Hardening"
Cohesion: 0.32
Nodes (7): SessionId, ReceiverSessionIndexHardeningTests, Exception, Fact, int, Proof, Task

### Community 10 - "End-to-End Transfer Tests"
Cohesion: 0.15
Nodes (17): ChunkAndLeafIndexSwappingTransport, ChunkPositionSwappingMulticastTransport, EndToEndTransferTests, TamperingMulticastTransport, Transfer, bool, Fact, Factory (+9 more)

### Community 11 - "Protocol & Swarm Module Overview"
Cohesion: 0.14
Nodes (6): Castr.Core.Transport, Castr.Core.Transport.InMemory, Castr.Core.Tests.Protocol, Castr.Core.Tests.TestSupport, Castr.Core.Protocol, Castr.Core.Tests.Transport

### Community 12 - "Manifest Codec & Signing"
Cohesion: 0.09
Nodes (16): ManifestCodec, SpanReader, byte, ReadOnlySpan, Stream, ManifestLimits, int, TransferManifest (+8 more)

### Community 13 - "Chunk Packetizer & Assembler"
Cohesion: 0.23
Nodes (9): Ciphertext, Proof, IReadOnlyList, ChunkPacketizerTests, byte, Fact, InlineData, int (+1 more)

### Community 14 - "In-Memory Stream Network"
Cohesion: 0.12
Nodes (19): InMemoryStreamClient, InMemoryStreamConnection, InMemoryStreamListener, InMemoryStreamNetwork, byte, CancellationToken, Channel, ChannelReader (+11 more)

### Community 15 - "CLI Command Definitions"
Cohesion: 0.15
Nodes (11): Command, Option, CastrCli, IAnsiConsole, IPAddress, RootCommand, TrustRunner, IAnsiConsole (+3 more)

### Community 16 - "Trust Prompt Implementations"
Cohesion: 0.11
Nodes (18): CancellationToken, Task, TrustPromptContext, AutoTrustPrompt, CancellationToken, Task, DialogTrustPrompt, CancellationToken (+10 more)

### Community 17 - "Android NSD Discovery"
Cohesion: 0.15
Nodes (9): IDiscoveryListener, IRegistrationListener, IResolveListener, NsdFailure, NsdServiceInfo, Object, DiscoveryListener, RegistrationListener (+1 more)

### Community 18 - "Packet Reassembler & Wire Packetizer"
Cohesion: 0.12
Nodes (13): PacketReassembler, Partial, byte, Dictionary, int, long, WirePacketizer, int (+5 more)

### Community 19 - "Real TCP Swarm Pull Tests"
Cohesion: 0.14
Nodes (7): Castr.Core.Swarm, Castr.Core.Time, Castr.Core.Transport.Tcp, Castr.Core.Discovery.InMemory, Castr.Core.Discovery.Tests, Castr.Core.Discovery, Castr.Core.Tests.Swarm

### Community 20 - "GUI App Shell & ViewModels"
Cohesion: 0.08
Nodes (13): Castr.Gui.Services, Castr.Gui, Castr.Gui.ViewModels, Castr.Gui.Trust, Castr.Gui.Tests, Castr.Gui.Views, IDataTemplate, ViewLocator (+5 more)

### Community 21 - "Swarm Receive Flow Tests"
Cohesion: 0.27
Nodes (8): ServedTransfer, SwarmReceiveFlowTests, AvaloniaFact, Factory, Func, GetSink, Key, Task

### Community 22 - "Mobile Receive ViewModel"
Cohesion: 0.15
Nodes (11): DiscoveredPeerItem, MobileReceiveViewModel, bool, CancellationToken, CancellationTokenSource, Func, HashSet, ObservableCollection (+3 more)

### Community 23 - "CLI Program Bootstrap & Paths"
Cohesion: 0.11
Nodes (9): Castr.Cli.Tests, Castr.Core.Trust, Castr.Cli, Castr.Core.Tests.Security, Castr.Core.Tests.Trust, Castr.Core.Security, ConsoleTrustPrompt, DatagramBudget (+1 more)

### Community 24 - "Swarm Receive ViewModel"
Cohesion: 0.16
Nodes (12): DiscoveredPeer, SwarmReceiveViewModel, bool, byte, CancellationToken, CancellationTokenSource, Func, HashSet (+4 more)

### Community 25 - "M1.5 Concepts & Hardening Notes"
Cohesion: 0.16
Nodes (23): Castr.Cli --chunk-size fail-fast guard, ConsoleTrustPrompt, ContentKey.EncryptChunk, DialogTrustPrompt, InMemoryTransportFactory, ITransportFactory, ReceiverSession, SenderSession (+15 more)

### Community 26 - "SwarmPullSession Core Logic"
Cohesion: 0.08
Nodes (21): Chunk, File, HeldChunk, LinkedListNode, HeldChunk, SwarmPullSession, SwarmPullSessionOptions, byte (+13 more)

### Community 27 - "Chunk Bitmap"
Cohesion: 0.16
Nodes (7): ChunkBitmap, byte, IEnumerable, ChunkBitmapTests, Fact, InlineData, Theory

### Community 28 - "ChunkHash (Chunking)"
Cohesion: 0.12
Nodes (16): IEquatable, ChunkHash, byte, int, ReadOnlySpan, FileSessionRegistry, DateTimeOffset, string (+8 more)

### Community 29 - "iOS App Shell"
Cohesion: 0.22
Nodes (5): AvaloniaAppDelegate, Castr.Gui.iOS, AppDelegate, AppBuilder, Application

### Community 30 - "Receiver Session Core"
Cohesion: 0.10
Nodes (23): PendingChunkServe, ChunkRequestMessage, CachedChunk, ChunkServeOutcome, ColdRebuild, PendingChunkServe, ReceiverSession, ReceiverSessionOptions (+15 more)

### Community 31 - "iOS NWBrowser Discovery"
Cohesion: 0.13
Nodes (13): DispatchQueue, NWBrowser, NWBrowseResult, NWConnection, NWListener, NetworkServiceDiscovery, bool, CancellationToken (+5 more)

### Community 32 - "Android App Shell"
Cohesion: 0.25
Nodes (5): AvaloniaAndroidApplication, App, Control, MainApplication, AppBuilder

### Community 33 - "TUI Dashboard End-to-End Tests"
Cohesion: 0.19
Nodes (9): Transfer, TransferDashboardEndToEndTests, CancellationToken, Fact, Factory, Func, GetSink, Key (+1 more)

### Community 34 - "Desktop App Shell & Identity"
Cohesion: 0.25
Nodes (4): IClassicDesktopStyleApplicationLifetime, App, CastrIdentity, Key

### Community 35 - "Repair Coordinator Planning Tests"
Cohesion: 0.14
Nodes (8): IReadOnlyCollection, RepairRequestPlan, Func, TimeSpan, RepairCoordinatorTests, DateTimeOffset, Fact, TimeSpan

### Community 36 - "Receive ViewModel (Desktop)"
Cohesion: 0.19
Nodes (10): ReceiveViewModel, bool, CancellationToken, CancellationTokenSource, Func, int, IReadOnlyList, RelayCommand (+2 more)

### Community 37 - "Filesystem File Sink & Source"
Cohesion: 0.06
Nodes (31): Chunker, CancellationToken, Memory, Task, ValueTask, FileSystemFileSink, bool, CancellationToken (+23 more)

### Community 38 - "Chunk Range & Layout"
Cohesion: 0.18
Nodes (8): ChunkLayout, ChunkRange, IEnumerable, int, ChunkLayoutTests, Fact, InlineData, Theory

### Community 39 - "Length-Prefixed Framer Stream Tests"
Cohesion: 0.20
Nodes (9): CancellationToken, Memory, ReadOnlyMemory, ValueTask, ChoppyConnection, CancellationToken, Memory, ReadOnlyMemory (+1 more)

### Community 40 - "CLI Parsing Tests"
Cohesion: 0.26
Nodes (5): ParsingTests, Fact, InlineData, RootCommand, Theory

### Community 41 - "Trust Decision Engine"
Cohesion: 0.35
Nodes (3): TrustDecisionEngine, TrustDecisionEngineTests, Fact

### Community 42 - "Security & Trust Test Namespaces"
Cohesion: 0.17
Nodes (10): ChunkHash, ReadOnlySpan, MerkleTreeTests, Fact, InlineData, Theory, ChunkPositionBindingTests, byte (+2 more)

### Community 43 - "GUI Trust Prompt Dialog"
Cohesion: 0.39
Nodes (4): TrustPromptViewModel, RelayCommand, Task, TaskCompletionSource

### Community 44 - "CLI Send & Chunk Size Validation"
Cohesion: 0.27
Nodes (8): SendRunner, CancellationToken, IAnsiConsole, Task, ChunkSizeValidationTests, Fact, string, Task

### Community 45 - "Length-Prefixed Framer & Pull Chunk Ops"
Cohesion: 0.29
Nodes (8): LengthPrefixedFramer, CancellationToken, int, Memory, ReadOnlyMemory, ValueTask, CancellationToken, Task

### Community 46 - "PeerTable Discovery Integration"
Cohesion: 0.20
Nodes (6): IReadOnlyList, Endpoint, IPEndPoint, PeerTableDiscoveryTests, DateTimeOffset, Fact

### Community 47 - "TUI Throughput Sampler & Send Runner"
Cohesion: 0.25
Nodes (6): Queue, ThroughputSampler, Func, Lock, TimeSpan, Stopwatch

### Community 48 - "Manifest Admission"
Cohesion: 0.19
Nodes (11): ISystemClock, SystemClock, DateTimeOffset, ITrustPrompt, CancellationToken, Task, ManifestAdmission, ManifestAdmissionResult (+3 more)

### Community 49 - "Manifest Signing Tests"
Cohesion: 0.14
Nodes (17): ManifestSigner, Key, ManifestVerifier, ManifestSigningTests, Fact, AlwaysAcceptPrompt, ServeHandle, SwarmPullSessionTests (+9 more)

### Community 50 - "Length-Prefixed Framer Tests"
Cohesion: 0.40
Nodes (5): Client, Server, LengthPrefixedFramerTests, Fact, Task

### Community 51 - "TCP Stream Client & Multicast Interfaces"
Cohesion: 0.27
Nodes (7): IStreamClient, TcpStreamClient, CancellationToken, Endpoint, IPAddress, IStreamConnection, ValueTask

### Community 52 - "In-Memory Network Chaos Injection"
Cohesion: 0.16
Nodes (11): ChaosOptions, InMemoryNetwork, ChannelWriter, Dictionary, Endpoint, List, Lock, Random (+3 more)

### Community 53 - "PeerTable Unit Tests"
Cohesion: 0.24
Nodes (8): PeerEntry, PeerTable, DateTimeOffset, Dictionary, TimeSpan, PeerTableTests, DateTimeOffset, Fact

### Community 54 - "In-Memory Multicast Transport"
Cohesion: 0.19
Nodes (9): IMulticastTransport, InMemoryMulticastTransport, CancellationToken, Channel, ChannelWriter, IAsyncEnumerable, ReadOnlyMemory, ReceivedPacket (+1 more)

### Community 55 - "In-Memory Unicast Transport"
Cohesion: 0.19
Nodes (10): IUnicastTransport, InMemoryUnicastTransport, CancellationToken, Channel, ChannelWriter, Endpoint, IAsyncEnumerable, ReadOnlyMemory (+2 more)

### Community 56 - "Send ViewModel (Desktop)"
Cohesion: 0.18
Nodes (11): MainViewModel, int, SendViewModel, bool, CancellationTokenSource, Func, int, Key (+3 more)

### Community 57 - "Path Safety Tests"
Cohesion: 0.22
Nodes (6): PathSafety, PathSafetyTests, Fact, InlineData, string, Theory

### Community 58 - "Network Interfaces & Path Safety"
Cohesion: 0.20
Nodes (7): Exception, InvalidInterfaceException, NetworkInterfaces, DatagramBudgetTooSmallException, TransferPreparation, PathTraversalException, PromptBoomException

### Community 59 - "UDP Unicast Transport"
Cohesion: 0.18
Nodes (11): UdpUnicastTransport, bool, CancellationToken, CancellationTokenSource, Channel, EndPoint, int, ReceivedPacket (+3 more)

### Community 60 - "Trust Store JSON Codec"
Cohesion: 0.28
Nodes (7): TrustEntryDto, TrustStoreDocument, TrustStoreJsonCodec, DateTimeOffset, JsonSerializerOptions, List, TrustEntryDto

### Community 61 - "Chunker & IFileSource"
Cohesion: 0.16
Nodes (9): ReceiveOptions, SendOptions, DatagramSizeValidationTests, Fact, InlineData, RootCommand, string, Task (+1 more)

### Community 62 - "IPeerTable Interface"
Cohesion: 0.23
Nodes (6): IPeerTable, PeerInfo, DateTimeOffset, int, IReadOnlyList, IReadOnlyList

### Community 63 - "PeerTable Implementation"
Cohesion: 0.22
Nodes (5): MessageType, DatagramFiltersTests, Fact, InlineData, Theory

### Community 64 - "ReceiverSession Message Handlers"
Cohesion: 0.17
Nodes (8): MerkleProof, Partial, byte, List, ChunkPacketizer, int, ChunkPacketMessage, IReadOnlyList

### Community 65 - "FileTrustStore & PublicKeyId"
Cohesion: 0.18
Nodes (5): PrivateKey, PublicKeyId, string, TrustDecision, TrustOutcome

### Community 66 - "InMemoryTrustStore & Seed Merger Tests"
Cohesion: 0.37
Nodes (4): SessionRegistryTests, Fact, Key, Task

### Community 67 - "FileSystemFileSink & IFileSink"
Cohesion: 0.12
Nodes (16): 2026-07-25 — M9 stage 1: proof-aware chunk slicing, and a 1472-byte datagram budget, A crash the new knob made reachable, found in review and fixed, "Degrades, never corrupts" was half right — QA measured a **stranded chunk**, now fixed, Loss behaviour — fewer, larger datagrams, re-validated unmodified, Measured wire composition (passive read-only sniffer, one lossless run per arm), One derived consequence, disclosed rather than hidden, Predictions, derived from the code and written down before the run, Read the win as datagram count, not bandwidth — and not as a syscall ceiling (+8 more)

### Community 68 - "Chunking Module Overview"
Cohesion: 0.09
Nodes (8): Castr.Core.Tests.Manifest, Castr.Core.Manifest, Castr.Core.Tests.Chunking, Castr.Core.Chunking, MerkleProofStep, MerkleSide, ManifestFileEntry, ManifestAdmissionOutcome

### Community 70 - "ITrustStore & TrustSeedMerger"
Cohesion: 0.15
Nodes (11): IReadOnlyList, ITrustStore, IReadOnlyList, TrustEntry, TrustEntrySource, TrustStatus, TrustSeedMerger, IEnumerable (+3 more)

### Community 71 - "Large Chunk Transfer Tests"
Cohesion: 0.32
Nodes (6): LargeChunkTransferTests, CancellationToken, Fact, IPAddress, Task, TimeSpan

### Community 72 - "Project csproj Files"
Cohesion: 0.14
Nodes (10): net10.0, Spectre.Console (0.49.1), Microsoft.NET.Sdk, net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.8.0), Spectre.Console.Testing (0.49.1), xunit (2.5.3) (+2 more)

### Community 73 - "TUI Chunk Heatmap"
Cohesion: 0.21
Nodes (8): char, IRenderable, Measurement, RenderOptions, Segment, ChunkHeatmap, IEnumerable, Style

### Community 74 - "UDP Transport & Integration Test Namespaces"
Cohesion: 0.21
Nodes (3): Castr.Core.IntegrationTests, Castr.Core.Transport.Udp, TestPorts

### Community 75 - "Swarm Serve Listener"
Cohesion: 0.21
Nodes (10): ChunkPullRequestMessage, SwarmServeListener, CancellationToken, int, List, SemaphoreSlim, Task, IStreamConnection (+2 more)

### Community 76 - "E2E README & M3 Hardening Summary"
Cohesion: 0.22
Nodes (11): Castr.Core.E2ETests README (container fan-out E2E tier), [E2EFact] opt-in gating (CASTR_E2E env var + reachable Docker), tc netem MTU-size-matched real packet loss injection, M3 — Test/CI hardening: implementation summary, Real chunk/wire-packet split implementation (WirePacketizer/ChunkPacketizer), Testcontainers E2E fan-out suite (real Docker bridge multicast + tc netem loss), macOS CI fix: UdpMulticastTransport missing IP_MULTICAST_IF (broken since M1), Reassembly memory-exhaustion DoS fix (unbounded PacketCount allocation) (+3 more)

### Community 77 - "FileTrustStore Tests"
Cohesion: 0.15
Nodes (9): FileTrustStore, IReadOnlyList, string, InMemoryTrustStore, Dictionary, IReadOnlyList, FileTrustStoreTests, Fact (+1 more)

### Community 78 - "TCP Stream Listener"
Cohesion: 0.22
Nodes (7): IStreamListener, TcpStreamListener, CancellationToken, Endpoint, IStreamConnection, ValueTask, TcpListener

### Community 79 - "CLI Receive Runner"
Cohesion: 0.54
Nodes (4): ReceiveRunner, CancellationToken, IAnsiConsole, Task

### Community 80 - "CLI Transfer Preparation"
Cohesion: 0.25
Nodes (4): PreparedTransfer, CancellationToken, Key, Task

### Community 81 - "RepairCoordinator Core"
Cohesion: 0.15
Nodes (9): RepairCoordinator, RepairOptions, DateTimeOffset, Dictionary, double, IEnumerable, int, Random (+1 more)

### Community 82 - "PublicKeyId Ed25519 Tests"
Cohesion: 0.23
Nodes (6): ReadOnlySpan, PublicKeyIdTests, Fact, TransferFlowTests, AvaloniaFact, Task

### Community 84 - "CLI Sender Identity"
Cohesion: 0.17
Nodes (10): SessionBindingDto, SessionBindingDto, SessionRegistryDocument, SessionRegistryJsonCodec, IReadOnlyList, JsonSerializerOptions, List, SessionBinding (+2 more)

### Community 85 - "Chunker Tests"
Cohesion: 0.20
Nodes (8): TransferProgress, TransferProgressViewModel, bool, double, int, long, string, ProgressRecorder

### Community 86 - "GUI Test Project Config"
Cohesion: 0.25
Nodes (7): Avalonia.Headless.XUnit (12.1.0), xunit.v3 (3.2.2), net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.12.0), xunit.runner.visualstudio (3.1.4), Microsoft.NET.Sdk

### Community 87 - "MemoryFileSink"
Cohesion: 0.06
Nodes (54): ChunkCount, OutboundRecorder, Recorded, RecordedOutbound, IFileSink, IReadableFileSink, CancellationToken, Memory (+46 more)

### Community 88 - "IServiceDiscovery Interface"
Cohesion: 0.20
Nodes (7): IServiceDiscovery, CancellationToken, IAsyncEnumerable, string, Task, ServedTransfer, ValueTask

### Community 89 - "GUI TransferBuilder"
Cohesion: 0.40
Nodes (4): TransferBuilder, CancellationToken, Key, Task

### Community 90 - "CLI End-to-End Loopback Tests"
Cohesion: 0.12
Nodes (11): IDisposable, SenderIdentity, Key, FileSystemFileSource, SafeFileHandle, EndToEndLoopbackTests, Fact, string (+3 more)

### Community 91 - "TrustStoreJsonCodec Tests"
Cohesion: 0.32
Nodes (4): Merged, Store, TrustStoreJsonCodecTests, Fact

### Community 92 - "Desktop Program Entry Point"
Cohesion: 0.33
Nodes (4): Castr.Gui.Desktop, Program, AppBuilder, STAThread

### Community 93 - "GUI InMemoryTransportFactory"
Cohesion: 0.20
Nodes (8): RenderSignal, CancellationToken, SemaphoreSlim, Task, TimeSpan, RenderSignalTests, Fact, Task

### Community 94 - "Android Project Config (csproj)"
Cohesion: 0.29
Nodes (6): Avalonia.Android (12.1.0), net10.0-android, Avalonia (12.1.0), Avalonia.Fonts.Inter (12.1.0), Avalonia.Themes.Fluent (12.1.0), Microsoft.NET.Sdk

### Community 95 - "GUI Project Config (csproj)"
Cohesion: 0.29
Nodes (6): CommunityToolkit.Mvvm (8.4.2), net10.0, Avalonia (12.1.0), Avalonia.Fonts.Inter (12.1.0), Avalonia.Themes.Fluent (12.1.0), Microsoft.NET.Sdk

### Community 96 - "Discovery Test Project Config"
Cohesion: 0.29
Nodes (6): net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.8.0), xunit (2.5.3), xunit.runner.visualstudio (2.5.3), Microsoft.NET.Sdk

### Community 97 - "In-App Trust Prompt"
Cohesion: 0.29
Nodes (5): ObservableObject, InAppTrustPrompt, CancellationToken, Task, ViewModelBase

### Community 98 - "RepairCoordinator Ranking & Endpoint Helpers"
Cohesion: 0.18
Nodes (9): ReceivedPacket, IAsyncEnumerable, IAsyncEnumerable, PacedRecordingTransport, RecordingMulticastTransport, CancellationToken, IAsyncEnumerable, ReadOnlyMemory (+1 more)

### Community 99 - "GUI UdpTransportFactory"
Cohesion: 0.13
Nodes (10): IAsyncDisposable, IMulticastTransport, IStreamListener, InMemoryTransportFactory, int, ITransportFactory, PreparedTransfer, UdpTransportFactory (+2 more)

### Community 100 - "UDP Unicast Transport Tests"
Cohesion: 0.30
Nodes (5): IAsyncEnumerable, ReadOnlyMemory, UdpUnicastTransportTests, Fact, Task

### Community 101 - "Core Project Config (csproj)"
Cohesion: 0.21
Nodes (9): ContentKey, AeadAlgorithm, int, Key, ReadOnlySpan, Span, EncryptedChunkHasher, CancellationToken (+1 more)

### Community 102 - "Wiki Schema & Graph Ontology"
Cohesion: 0.40
Nodes (6): LLM Wiki maintenance convention, Wiki page template, Wiki Graph Ontology, Wiki Graph Layer docs, Wiki optional graph metadata (graph: key), Wiki Schema (conventions)

### Community 103 - "iOS Project Config (csproj)"
Cohesion: 0.18
Nodes (9): Avalonia.iOS (12.1.0), net10.0, net10.0-android, net10.0-ios, Microsoft.NET.Sdk, net10.0, net10.0-ios, Avalonia.Fonts.Inter (12.1.0) (+1 more)

### Community 104 - "MemoryFileSource"
Cohesion: 0.18
Nodes (10): CancellationToken, ReadOnlyMemory, ValueTask, RealMulticastFanOutTests, CancellationToken, Fact, Task, SmokeTest (+2 more)

### Community 105 - "IStreamClient Interface"
Cohesion: 0.15
Nodes (10): SpanReader, AnnounceMessage, ChunkDataMessage, ChunkPullResponseMessage, KeyUnavailableMessage, ManifestMessage, ManifestRequestMessage, PacketFragmentMessage (+2 more)

### Community 106 - "IStreamListener Interface"
Cohesion: 0.27
Nodes (5): ContentKeyDeterminismTests, byte, Fact, InlineData, Theory

### Community 107 - "MainViewModel & ViewModelBase"
Cohesion: 0.18
Nodes (10): ChunkIndices, FileIndex, PeerHaveMessage, FileTrafficObserver, RecordedOutbound, Dictionary, IReadOnlyList, List (+2 more)

### Community 109 - "GUI ITransportFactory Interface"
Cohesion: 0.21
Nodes (9): DiscoveryListener, NsdManager, RegistrationListener, NsdServiceDiscovery, bool, CancellationToken, IAsyncEnumerable, Task (+1 more)

### Community 110 - "Trusted Senders Seed File"
Cohesion: 0.40
Nodes (4): comment, entries, $schema, version

### Community 111 - "Project Plan Milestones & Infra"
Cohesion: 0.50
Nodes (4): graphify codebase graph convention, graphify + llm-wiki mandatory infrastructure (plan), Milestone plan M0-M5 (plan), Verification/testing approach (plan)

### Community 112 - "GUI Test App Builder"
Cohesion: 0.15
Nodes (13): 2026-07-25 — M8: `CastrPaths.DefaultChunkSize` 8192 → 262144, and re-validation of M7's repair constants, A degraded run set was discarded in between — the rule catching itself, ⚠️ But do not read 36× as a safety factor — it is a property of an unloaded host, `CarouselIdleThreshold` re-validated — the margin is 36×, not the 32× degradation predicted, Docker-gated E2E suite — the strongest single signal, and it is green, Post-review hardening (systems-design MERGE-WITH-CHANGES), ⚠️ Read this first: on this host the multicast **group address** is worth up to 1.8x, Sanity-check against the recorded baseline (rule: a row that will not reconcile is a measurement problem) (+5 more)

### Community 113 - "MobileReceiveViewModel Trust Denial"
Cohesion: 0.22
Nodes (10): KeyAgreementAlgorithm, KeyDerivationAlgorithm, SharedSecret, ContentKeyWrap, AeadAlgorithm, byte, int, Key (+2 more)

### Community 114 - "ReceiveViewModel Trust Denial"
Cohesion: 0.15
Nodes (11): System.CommandLine (2.0.0), net10.0, Spectre.Console (0.49.1), Microsoft.NET.Sdk, net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.8.0), Spectre.Console.Testing (0.49.1) (+3 more)

### Community 115 - "SwarmReceiveViewModel Trust Denial"
Cohesion: 0.35
Nodes (6): SenderSessionPipeliningTests, Transfer, Fact, Func, Task, TimeSpan

### Community 117 - "Byte Type Node"
Cohesion: 0.15
Nodes (13): A crash the new knob made reachable, Deliberately not done, with reasons, M9 stage 1 — datagram efficiency, Measured, against a prediction made first, MTU auto-derivation: implemented, then removed — and why that is the right call, ⚠️ The absolute throughput figures above are loopback-only, The budget must be constant for the life of a session, Two arithmetic corrections this produced (+5 more)

### Community 119 - "ContentKey Type Node"
Cohesion: 0.17
Nodes (12): 2026-07-25 — M10: bounding `ReceiverSession._chunkCache` (memory, not throughput), Docker netem E2E tier — green, and it genuinely exercised the cold path, Environment — read this before comparing to any other row in this file, Gen2 collections went UP — 18 to 154 — and that is the correct outcome, Headline — peak retained receiver memory, 2 GiB single-file transfer, No throughput regression — 100 MiB, warm, interleaved, n=6 per arm, run twice, Reproduction, The change (+4 more)

### Community 120 - "Dictionary Type Node"
Cohesion: 0.21
Nodes (8): IStreamConnection, TcpStreamConnection, CancellationToken, Endpoint, Memory, ReadOnlyMemory, Socket, ValueTask

### Community 122 - "GetSink Helper Node"
Cohesion: 0.45
Nodes (3): InMemoryStreamNetworkTests, Fact, Task

### Community 123 - "HashSet Type Node"
Cohesion: 0.18
Nodes (11): 2026-07-25 — Instrumented measurement campaign (91 real transfers), A/B matrix, Both sides are bound by per-datagram cost, not bytes and not crypto, ⚠️ But the waste is accidentally load-bearing, Corrections to earlier records in this file, Known test issue, pre-existing, Ranked by measured payoff, ⚠️ Read this first: the OS page cache is a ~2× confounder (+3 more)

### Community 124 - "IAsyncEnumerable Type Node"
Cohesion: 0.18
Nodes (10): Castr — milestone plan, Definition of done, every milestone, M11 — Clear the small backlog, M12 — Fan-out scaling, M13 — Release automation, M14 — Documentation reconciliation, Part 1 — Completed, Part 2 — Remaining (+2 more)

### Community 126 - "IPeerTable Type Node"
Cohesion: 0.49
Nodes (3): UdpMulticastTransportTests, Fact, Task

### Community 127 - "ISystemClock Type Node"
Cohesion: 0.29
Nodes (7): TransferDashboard, Action, CancellationToken, Func, IAnsiConsole, Task, TimeSpan

### Community 128 - "JoinRequestMessage Type Node"
Cohesion: 0.24
Nodes (7): ConcurrencyProbeTransport, CancellationToken, IAsyncEnumerable, int, Lock, ReadOnlyMemory, ValueTask

### Community 129 - "KeyGrantMessage Type Node"
Cohesion: 0.44
Nodes (5): SwarmServeListenerTests, Fact, Func, Task, TimeSpan

### Community 130 - "ManifestMessage Type Node"
Cohesion: 0.40
Nodes (5): FakeProgressSource, TransferDashboardLoopTests, bool, Fact, Task

### Community 131 - "MemoryFileSink Type Node"
Cohesion: 0.20
Nodes (10): Design sketch, Open questions, Prerequisites, Prior art: this is UFTP's design, Proposal: section-based repair gating, The actual motivation: deleting a bug class, The idea, What this is *not*: a throughput change (+2 more)

### Community 132 - "MerkleProof Type Node"
Cohesion: 0.36
Nodes (4): Color, TransferDashboardRenderer, IRenderable, Text

### Community 133 - "MerkleTree Type Node"
Cohesion: 0.22
Nodes (9): M5/M6 showcase demo captures, M6 round 1 — sender-side send-window pipelining, M6 round 2 — after the receiver-side fix, M6 round 2 — independent QA re-measurement of round 1, M6 round 2 — shared-gate prototype (rejected), M6 round 3 — independent confirmation of the receiver fix, Measured runs, Post-M6 field report (+1 more)

### Community 135 - "ReadOnlyMemory Type Node"
Cohesion: 0.22
Nodes (9): Approach A — Pipeline the sender's send loop *(shipped, then reverted to a no-op)*, Approach B — A shared gate to make the send window a true global bound *(prototyped, rejected)*, Approach C — Decouple the socket read from downstream processing *(shipped — this was the real fix)*, Approach D — Explicit socket buffer sizing *(shipped, adopted from UFTP)*, Approach E — Port UFTP's TFMCC congestion control *(rejected)*, Approach F — Adopt UFTP's NACK-only, section-aggregated feedback *(identified; not yet implemented)*, Approaches considered, and why we chose what we chose, Ruled out before writing any code (+1 more)

### Community 136 - "ReadOnlySpan Type Node"
Cohesion: 0.22
Nodes (5): EventArgs, MainWindow, TrustPromptDialog, Window, WindowClosingEventArgs

### Community 137 - "SemaphoreSlim Type Node"
Cohesion: 0.22
Nodes (8): Testcontainers (4.13.0), net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.8.0), NSec.Cryptography (26.4.0), xunit (2.5.3), xunit.runner.visualstudio (2.5.3), Microsoft.NET.Sdk

### Community 138 - "Signed Type Node"
Cohesion: 0.22
Nodes (4): ChunkPacketAssembler, Dictionary, int, long

### Community 139 - "SignedManifest Type Node"
Cohesion: 0.28
Nodes (6): ChaosTransport, CancellationToken, IAsyncEnumerable, Random, ReadOnlyMemory, ValueTask

### Community 140 - "Sources Node"
Cohesion: 0.36
Nodes (5): RealTransferRepairTests, CancellationToken, Fact, Task, TimeSpan

### Community 141 - "MessageCodec MerkleProof Param"
Cohesion: 0.22
Nodes (8): Correctness of what things are called, M11 — clearing the small backlog, Memory and lifetime bounds, Not done, Rules earned, Security and robustness, The E2E loss filter was measuring something other than what it claimed, The one that changed the wire

### Community 142 - "MessageCodec Stream Param"
Cohesion: 0.32
Nodes (4): MessageCodec, byte, int, Stream

### Community 143 - "ReceiverSession ChunkRequestMessage Param"
Cohesion: 0.32
Nodes (5): ScriptedTransport, CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask

### Community 144 - "ReceiverSession MerkleProof Param"
Cohesion: 0.32
Nodes (5): ScriptedTransport, CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask

### Community 145 - "ReceiverSession PublicKeyId Param"
Cohesion: 0.32
Nodes (5): FilteringMulticastTransport, CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask

### Community 148 - "SenderSession ChunkRequestMessage Param"
Cohesion: 0.29
Nodes (7): 2026-07-25 — M7 implementation A/B (P2 filters, P1 PEER_HAVE coalescing, P0 repair bounding), ⚠️ Host state was degraded — goodput figures here are NOT comparable to the campaign above, P2 is a correctness fix, and must not borrow credit from P1, Test-suite note, Watermark isolated (stage4 vs stage3), What this run set does and does not support, What to measure next

### Community 149 - "SenderSession IFileSource Param"
Cohesion: 0.29
Nodes (7): 2026-07-25 — Showcase demo re-capture (post-M8), Capture-tooling notes worth not rediscovering, Conventions, Derived overhead (computed from code, not measured), Independent corroboration from data already in this file, The gossip term is quadratic in file size, Throughput run log

### Community 150 - "UdpMulticastTransport Endpoint Param"
Cohesion: 0.10
Nodes (19): From, ReceivedBytes, SocketOptionName, Endpoint, UdpMulticastTransport, bool, CancellationToken, CancellationTokenSource (+11 more)

### Community 151 - "UdpMulticastTransport IPEndPoint Param"
Cohesion: 0.29
Nodes (7): 1. Measure the real binaries over a real socket, or don't claim a number, 2. The person who wrote the change cannot be the one who validates it, 3. Predict the cost from the code first, then measure — the disagreement is the finding, Current state, How Castr's performance work is done, The three rules, What we got wrong, on the record

### Community 152 - "UdpMulticastTransport Socket Param"
Cohesion: 0.29
Nodes (6): Avalonia.Desktop (12.1.0), AvaloniaUI.DiagnosticsSupport (2.2.3), net10.0, Avalonia (12.1.0), Avalonia.Fonts.Inter (12.1.0), Microsoft.NET.Sdk

### Community 153 - "CLI UnitTest1 Stub"
Cohesion: 0.33
Nodes (3): ConsoleProgressReporter, int, object

### Community 154 - "E2E UnitTest1 Stub"
Cohesion: 0.29
Nodes (4): IPAddress, MulticastInterfaces, IPAddress, IReadOnlyList

### Community 155 - "EndToEndTransferTests Dictionary Param"
Cohesion: 0.29
Nodes (3): MobileReceiveView, SwarmReceiveView, UserControl

### Community 156 - "EndToEndTransferTests ReceivedPacket Param"
Cohesion: 0.29
Nodes (6): net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.8.0), xunit (2.5.3), xunit.runner.visualstudio (2.5.3), Microsoft.NET.Sdk

### Community 157 - "MessageCodecTests InlineData Param"
Cohesion: 0.29
Nodes (6): net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.8.0), xunit (2.5.3), xunit.runner.visualstudio (2.5.3), Microsoft.NET.Sdk

### Community 159 - "Trees Node"
Cohesion: 0.29
Nodes (7): M6 — Send-path throughput/pipelining: investigation and fix, Round 1: pipeline the sender (partially right, wrong default), Round 2: the real fix is receiver-side, Round 3: final sign-off, What's still open, What was ruled out first, Where this fits

### Community 160 - "TrustDecision Type Node"
Cohesion: 0.29
Nodes (7): M7 — Repair amplification, PEER_HAVE gossip, and the sender's own echo, Round 2: a liveness regression QA reproduced, Round 3: the same modelling error, in the opposite direction, Still open, The measurement claim was withdrawn, What shipped, Where this fits

### Community 161 - "ValueTask Primitive"
Cohesion: 0.33
Nodes (6): 2026-07-25 — Root cause of the multicast group-address confounder: leaked loopback memberships, The real-NIC ~12 MB/s cap is the link partner, and one systems-design prediction is refuted, Two traps to carry forward, What it is, What it means for every number in this file, What this retracts

### Community 162 - "Castr in action"
Cohesion: 0.33
Nodes (6): Castr in action, How these were made, 🖥️ The fleet push — headless CLI, 🎮 The LAN party — desktop GUI, 🧪 The test lab — the colorful TUI, What's not shown here yet

### Community 163 - "CastrPaths"
Cohesion: 0.33
Nodes (4): CastrPaths, int, IPAddress, string

### Community 164 - "DatagramFilters"
Cohesion: 0.40
Nodes (4): DatagramFilters, DatagramFilter, int, ReadOnlySpan

### Community 165 - ".SendAsync"
Cohesion: 0.33
Nodes (4): CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask

### Community 166 - ".PickFileAsync"
Cohesion: 0.47
Nodes (3): StoragePickers, Task, Visual

### Community 168 - "Demo capture scripts"
Cohesion: 0.33
Nodes (5): Demo capture scripts, Files, Known gotchas (found the hard way — don't rediscover these), Prerequisites, Usage

### Community 170 - "Castr.Gui.Android"
Cohesion: 0.40
Nodes (3): AvaloniaMainActivity, Castr.Gui.Android, MainActivity

### Community 171 - "2026-07-26 — First post-reboot wire measurement: the router cap confirmed, and Castr is near it"
Cohesion: 0.40
Nodes (5): 2026-07-26 — First post-reboot wire measurement: the router cap confirmed, and Castr is near it, Correction, same day: the leaked memberships came back within hours, and force-killing processes is why, Methodology note — a harness bug that produced a plausible wrong answer, The consequence, stated plainly, This confirms the prediction, and the tightness is the tell

### Community 172 - "Castr.Core.csproj"
Cohesion: 0.40
Nodes (4): Blake3 (3.0.2), net10.0, NSec.Cryptography (26.4.0), Microsoft.NET.Sdk

## Knowledge Gaps
- **288 isolated node(s):** `net10.0`, `System.CommandLine (2.0.0)`, `Spectre.Console (0.49.1)`, `Microsoft.NET.Sdk`, `net10.0` (+283 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **15 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `ReceiverSession` connect `Receiver Session Core` to `Swarm Pull Session & Protocol Messages`, `Sender Session Protocol`, `Signed Type Node`, `End-to-End Transfer Tests`, `Sources Node`, `Packet Reassembler & Wire Packetizer`, `Real TCP Swarm Pull Tests`, `TUI Dashboard End-to-End Tests`, `Receive ViewModel (Desktop)`, `Manifest Admission`, `Manifest Signing Tests`, `IPeerTable Interface`, `ITrustStore & TrustSeedMerger`, `Large Chunk Transfer Tests`, `CLI Receive Runner`, `RepairCoordinator Core`, `MemoryFileSink`, `GUI UdpTransportFactory`, `Core Project Config (csproj)`, `ReceiverSession Progress Tracking`, `ISystemClock Type Node`?**
  _High betweenness centrality (0.067) - this node is a cross-community bridge._
- **Why does `Castr.Core.Transport` connect `Protocol & Swarm Module Overview` to `RepairCoordinator Ranking & Endpoint Helpers`, `GUI UdpTransportFactory`, `Chunking Module Overview`, `Transport Interfaces & In-Memory Network`, `UDP Transport & Integration Test Namespaces`, `Swarm Serve Listener`, `ReceiverSession ReceivedPacket Param`, `Real TCP Swarm Pull Tests`, `GUI App Shell & ViewModels`, `CLI Program Bootstrap & Paths`, `SwarmPullSession Core Logic`?**
  _High betweenness centrality (0.058) - this node is a cross-community bridge._
- **Why does `Castr.Core.Protocol` connect `Protocol & Swarm Module Overview` to `Chunking Module Overview`, `MerkleProof Type Node`, `IStreamClient Interface`, `UDP Transport & Integration Test Namespaces`, `ReceiverSession Progress Tracking`, `Packet Reassembler & Wire Packetizer`, `Real TCP Swarm Pull Tests`, `GUI App Shell & ViewModels`, `ReceiverSession ReceivedPacket Param`, `CLI Program Bootstrap & Paths`, `CLI UnitTest1 Stub`, `Network Interfaces & Path Safety`, `Chunk Bitmap`?**
  _High betweenness centrality (0.057) - this node is a cross-community bridge._
- **What connects `net10.0`, `System.CommandLine (2.0.0)`, `Spectre.Console (0.49.1)` to the rest of the system?**
  _288 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Swarm Pull Session & Protocol Messages` be split into smaller, more focused modules?**
  _Cohesion score 0.06486486486486487 - nodes in this community are weakly interconnected._
- **Should `In-Memory Service Discovery` be split into smaller, more focused modules?**
  _Cohesion score 0.07680491551459294 - nodes in this community are weakly interconnected._
- **Should `Sender Session Protocol` be split into smaller, more focused modules?**
  _Cohesion score 0.12141779788838612 - nodes in this community are weakly interconnected._