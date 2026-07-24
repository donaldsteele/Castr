# Graph Report - c:/code/Castr  (2026-07-24)

## Corpus Check
- 41 files · ~64,110 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1725 nodes · 3378 edges · 125 communities (82 shown, 43 thin omitted)
- Extraction: 94% EXTRACTED · 6% INFERRED · 0% AMBIGUOUS · INFERRED: 199 edges (avg confidence: 0.81)
- Token cost: 0 input · 105,213 output

## Community Hubs (Navigation)
- Peer Table Interface
- Core Transfer Orchestration
- Content Key Encryption
- Manifest Codec
- Sender Identity & Runner
- Message Codec
- E2E Test Infrastructure
- Chunk Hash Value Type
- Transfer Preparation
- Chunk Packet Assembly
- In-Memory Multicast Transport
- End-to-End Transfer Tests
- UDP Multicast Transport
- CLI Command Building
- Trust Prompt Tests
- Trust Prompt Abstraction
- Packet Reassembly
- File Trust Store
- Receiver Session
- Architecture & Design Decisions
- Chunk Bitmap
- Receive View Model (GUI)
- UDP Unicast Transport
- GUI Main/Send View Models
- Avalonia GUI Project Structure
- Transfer Dashboard E2E Tests
- GUI App Bootstrap & Identity
- CLI Trust Commands
- Chunk Layout
- In-Memory Network Chaos Tests
- CLI Argument Parsing Tests
- Transfer Progress Model
- Solution & Project Layout
- Transport Integration Tests
- Wire Protocol & Packetizer
- Security & Trust Tests
- Trust Prompt Dialog (GUI)
- File Source/Sink Tests
- Real Socket Transport Tests
- Trust Decision Engine Tests
- TUI Receive Runner & Throughput
- Trust Store JSON Codec
- Session Message Handlers
- Dashboard Renderer Tests
- Network Interfaces & Path Safety
- Chunker & File Reading
- Transfer Dashboard Runner
- File System Sink
- Chunk Heatmap Rendering
- Endpoint Abstraction
- Trust Store & Seed Merging
- Transport Factory
- Large Chunk Transfer Tests
- Filtering Transport Test Support
- Public Key ID Tests
- Chaos Transport Simulation
- Dashboard Loop Tests
- Dashboard Renderer
- Chunking Module Files
- E2E Test Project Config
- Transfer Builder
- Chunker Hashing Tests
- GUI Test Project Config
- Receive Runner
- File System Source
- Memory File Sink
- CLI Test Project Config
- Trust Store Codec Tests
- TUI Test Project Config
- GUI Desktop Entry Point
- Unicast Transport Interface
- In-Memory Transport Factory
- GUI Desktop Project Config
- GUI Project Config
- Console Progress Reporter
- LLM Wiki Schema & Conventions
- Memory File Source
- Chunk Position Binding Tests
- GUI View Locator
- Trust Store Bootstrap
- Multicast Interface Discovery
- Castr Paths Config
- Transfer Progress Events
- Trust Seed File Schema
- Project Planning Conventions
- Main Window GUI Tests
- Avalonia Test App Builder
- Discovery Module Stub
- Platform & Tech Stack Decisions
- Byte Primitive
- Roadmap-First Convention
- Dictionary Primitive
- Factory Pattern Node
- GetSink Method
- HashSet Primitive
- IAsyncEnumerable Primitive
- IFileSink Interface
- Join Request Message
- Key Grant Message
- Manifest Message
- Memory File Sink Type
- Merkle Proof Type
- Repo Layout Plan
- ReadOnlyMemory Primitive
- Received Packet Type
- Signed Node
- Sources Node
- ReadOnlySpan Primitive
- Stream Primitive
- Chunk Request Message
- System Clock Interface
- Received Packet Node
- Trust Decision Type
- Bool Primitive
- Chunk Request Message Node
- IFileSource Interface
- Object Primitive
- EndPoint Type
- IPEndPoint Type
- Socket Type
- Fact Attribute
- Fact Attribute (Alt)
- Dictionary Node
- Trees Node
- ValueTask Primitive

## God Nodes (most connected - your core abstractions)
1. `ReceiverSession` - 48 edges
2. `Castr.Core.Protocol` - 38 edges
3. `Castr.Core.Trust` - 30 edges
4. `SenderSession` - 28 edges
5. `ReceiveViewModel` - 25 edges
6. `TrustPromptAndProgressTests` - 23 edges
7. `EndToEndTransferTests` - 22 edges
8. `Castr roadmap and milestone status` - 20 edges
9. `SendViewModel` - 19 edges
10. `ChunkHash` - 18 edges

## Surprising Connections (you probably didn't know these)
- `DialogTrustPrompt` --semantically_similar_to--> `ConsoleTrustPrompt`  [INFERRED] [semantically similar]
  src/Castr.Gui/README.md → wiki/synthesis/m2-ui-summary.md
- `Mobile unicast swarm client decision` --rationale_for--> `Castr (product entity)`  [INFERRED]
  raw/2026-07-24-castr-project-plan.md → wiki/entities/castr-project.md
- `RepairCoordinator (plan)` --rationale_for--> `Castr repair protocol`  [INFERRED]
  raw/2026-07-24-castr-project-plan.md → wiki/concepts/repair-protocol.md
- `CI: e2e-docker job (Docker-gated Testcontainers tier)` --references--> `[E2EFact] opt-in gating (CASTR_E2E env var + reachable Docker)`  [INFERRED]
  .github/workflows/ci.yml → tests/Castr.Core.E2ETests/README.md
- `TrustDecisionEngineTests` --references--> `PublicKeyId`  [EXTRACTED]
  tests/Castr.Core.Tests/Trust/TrustDecisionEngineTests.cs → src/Castr.Core/Security/PublicKeyId.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **M3 test/CI hardening milestone deliverables** — wiki_synthesis_roadmap_m3, wiki_synthesis_m3_test_ci_hardening_summary, github_workflows_ci_e2e_docker, github_workflows_ci_build_and_test, tests_castr_core_e2etests_readme, wiki_concepts_wire_protocol_two_level_chunking [EXTRACTED 0.90]
- **Castr payload encryption design flow** — wiki_concepts_security_model_payload_encryption, wiki_concepts_wire_protocol_join_key_grant_handshake, wiki_concepts_wire_protocol_merkle_manifest, wiki_concepts_tech_stack_nsec_cryptography [EXTRACTED 0.90]
- **Two-tier CI test gating (fast matrix + opt-in Docker E2E)** — github_workflows_ci_build_and_test, github_workflows_ci_e2e_docker, tests_castr_core_e2etests_readme_e2efact_gating [EXTRACTED 0.90]
- **M2 Core observability & trust-prompt contract** — concept_sendersession, concept_receiversession, concept_dialogtrustprompt, concept_consoletrustprompt, concept_trustdecisionengine [EXTRACTED 1.00]
- **M1.5 payload-encryption feature (decision, implementation, and mechanism)** — wiki_synthesis_adr_0003_payload_encryption, wiki_synthesis_m1_5_encryption_summary, wiki_concepts_security_model_payload_encryption [INFERRED 0.85]
- **Castr core architecture concept cluster** — wiki_entities_castr_project_castr, wiki_concepts_repair_protocol_repair_protocol [EXTRACTED 1.00]
- **Definition-of-done gate applied at every milestone** — raw_2026_07_24_castr_project_plan_milestones, raw_2026_07_24_castr_project_plan_graphify_llm_wiki_infra [EXTRACTED 1.00]

## Communities (125 total, 43 thin omitted)

### Community 0 - "Peer Table Interface"
Cohesion: 0.06
Nodes (33): Castr.Core.Time, IReadOnlyCollection, IPeerTable, PeerInfo, DateTimeOffset, IReadOnlyList, PeerHaveMessage, PeerEntry (+25 more)

### Community 1 - "Core Transfer Orchestration"
Cohesion: 0.06
Nodes (71): Castr.Cli --chunk-size fail-fast guard, ConsoleTrustPrompt, ContentKey.EncryptChunk, DialogTrustPrompt, InMemoryTransportFactory, ITransportFactory, ReceiverSession, SenderSession (+63 more)

### Community 2 - "Content Key Encryption"
Cohesion: 0.06
Nodes (35): ChunkHash, KeyAgreementAlgorithm, KeyDerivationAlgorithm, SharedSecret, ContentKey, AeadAlgorithm, int, Key (+27 more)

### Community 3 - "Manifest Codec"
Cohesion: 0.07
Nodes (19): Castr.Core.Tests.Manifest, Castr.Core.Manifest, ManifestCodec, SpanReader, byte, ReadOnlySpan, Stream, ManifestSigner (+11 more)

### Community 4 - "Sender Identity & Runner"
Cohesion: 0.05
Nodes (27): IDisposable, SenderIdentity, Key, PublicKeyId, SendOptions, SendRunner, CancellationToken, IAnsiConsole (+19 more)

### Community 5 - "Message Codec"
Cohesion: 0.11
Nodes (13): ReadOnlySpan, SpanReader, MessageCodec, SpanReader, byte, int, MerkleProof, ManifestMessage (+5 more)

### Community 6 - "E2E Test Infrastructure"
Cohesion: 0.06
Nodes (31): Castr.Core.E2ETests, Castr.Core.E2ETests.Infrastructure, E2EFact, FactAttribute, IAsyncLifetime, ICollectionFixture, IContainer, IFutureDockerImage (+23 more)

### Community 7 - "Chunk Hash Value Type"
Cohesion: 0.10
Nodes (17): IEquatable, ChunkHash, byte, int, ReadOnlySpan, ChunkHash, MerkleProof, MerkleProofStep (+9 more)

### Community 8 - "Transfer Preparation"
Cohesion: 0.08
Nodes (27): IFileSource, MerkleTree, object, PreparedTransfer, TransferPreparation, CancellationToken, IMulticastTransport, Key (+19 more)

### Community 9 - "Chunk Packet Assembly"
Cohesion: 0.10
Nodes (20): Ciphertext, ChunkPacketAssembler, Partial, byte, Dictionary, int, long, MerkleProof (+12 more)

### Community 10 - "In-Memory Multicast Transport"
Cohesion: 0.07
Nodes (28): Castr.Core.Transport.InMemory, Lock, ChaosOptions, InMemoryMulticastTransport, CancellationToken, Channel, ChannelWriter, IAsyncEnumerable (+20 more)

### Community 11 - "End-to-End Transfer Tests"
Cohesion: 0.12
Nodes (22): EndToEndTransferTests, TamperingMulticastTransport, Transfer, CancellationToken, Fact, Factory, Func, GetSink (+14 more)

### Community 12 - "UDP Multicast Transport"
Cohesion: 0.08
Nodes (24): EndPoint, IMulticastTransport, IPEndPoint, SessionId, Socket, UdpMulticastTransport, CancellationToken, IAsyncEnumerable (+16 more)

### Community 13 - "CLI Command Building"
Cohesion: 0.13
Nodes (14): Command, Option, CastrCli, IAnsiConsole, IPAddress, RootCommand, TrustStatus, TrustRunner (+6 more)

### Community 14 - "Trust Prompt Tests"
Cohesion: 0.16
Nodes (18): ITrustPrompt, StubTrustPrompt, ThrowingTrustPrompt, Transfer, TrustPromptAndProgressTests, CancellationToken, Fact, Factory (+10 more)

### Community 15 - "Trust Prompt Abstraction"
Cohesion: 0.11
Nodes (18): CancellationToken, Task, ITrustPrompt, TrustPromptContext, CancellationToken, Task, AutoTrustPrompt, CancellationToken (+10 more)

### Community 16 - "Packet Reassembly"
Cohesion: 0.16
Nodes (11): PacketReassembler, Partial, byte, Dictionary, int, long, IReadOnlyList, WirePacketizerTests (+3 more)

### Community 17 - "File Trust Store"
Cohesion: 0.14
Nodes (10): PublicKeyId, string, FileTrustStore, IReadOnlyList, string, InMemoryTrustStore, Dictionary, IReadOnlyList (+2 more)

### Community 18 - "Receiver Session"
Cohesion: 0.09
Nodes (20): ContentKey, IPeerTable, ISystemClock, RepairCoordinator, SemaphoreSlim, SignedManifest, ReceiverSession, ReceiverSessionOptions (+12 more)

### Community 19 - "Architecture & Design Decisions"
Cohesion: 0.11
Nodes (23): Core-first build phasing decision, Avalonia GUI framework decision, IPeerTable abstraction (plan), Mobile unicast swarm client decision, Path safety (no traversal) decision, Integrity-only payload security decision, Castr approved project plan, Repair protocol design (plan) (+15 more)

### Community 20 - "Chunk Bitmap"
Cohesion: 0.17
Nodes (7): ChunkBitmap, byte, IEnumerable, ChunkBitmapTests, Fact, InlineData, Theory

### Community 21 - "Receive View Model (GUI)"
Cohesion: 0.13
Nodes (16): IReadOnlyList, ReceiveViewModel, bool, CancellationToken, CancellationTokenSource, Func, IMulticastTransport, int (+8 more)

### Community 22 - "UDP Unicast Transport"
Cohesion: 0.17
Nodes (13): IUnicastTransport, Endpoint, UdpUnicastTransport, CancellationToken, EndPoint, IAsyncEnumerable, ReadOnlyMemory, ReceivedPacket (+5 more)

### Community 23 - "GUI Main/Send View Models"
Cohesion: 0.13
Nodes (14): ObservableObject, MainViewModel, int, SendViewModel, bool, CancellationTokenSource, Func, IMulticastTransport (+6 more)

### Community 24 - "Avalonia GUI Project Structure"
Cohesion: 0.17
Nodes (9): Castr.Gui.Services, Castr.Gui, Castr.Gui.ViewModels, Castr.Gui.Trust, Castr.Gui.Tests, Castr.Gui.Views, TransferFlowTests, AvaloniaFact (+1 more)

### Community 25 - "Transfer Dashboard E2E Tests"
Cohesion: 0.15
Nodes (12): Transfer, TransferDashboardEndToEndTests, CancellationToken, Fact, Factory, Func, GetSink, IFileSink (+4 more)

### Community 26 - "GUI App Bootstrap & Identity"
Cohesion: 0.13
Nodes (10): Application, IClassicDesktopStyleApplicationLifetime, App, CastrIdentity, Key, StoragePickers, Task, MainWindow (+2 more)

### Community 27 - "CLI Trust Commands"
Cohesion: 0.17
Nodes (7): Castr.Cli.Tests, Castr.Core.Trust, Castr.Cli, ConsoleTrustPrompt, ExitCodes, int, UnknownSenderPolicy

### Community 28 - "Chunk Layout"
Cohesion: 0.20
Nodes (8): ChunkLayout, ChunkRange, IEnumerable, int, ChunkLayoutTests, Fact, InlineData, Theory

### Community 29 - "In-Memory Network Chaos Tests"
Cohesion: 0.31
Nodes (6): Endpoint, IMulticastTransport, IUnicastTransport, InMemoryNetworkTests, Fact, Task

### Community 30 - "CLI Argument Parsing Tests"
Cohesion: 0.24
Nodes (6): ParsingTests, Fact, InlineData, RootCommand, Theory, UnknownSenderPolicy

### Community 31 - "Transfer Progress Model"
Cohesion: 0.18
Nodes (9): double, TransferPhase, TransferProgress, TransferRole, TransferProgressViewModel, bool, int, long (+1 more)

### Community 32 - "Solution & Project Layout"
Cohesion: 0.16
Nodes (12): Castr.Core.Discovery, Castr.Core.IntegrationTests, Castr.Core.Tests, System.CommandLine (2.0.0), Castr.Cli, net10.0, Spectre.Console (0.49.1), Microsoft.NET.Sdk (+4 more)

### Community 33 - "Transport Integration Tests"
Cohesion: 0.18
Nodes (6): Castr.Core.Transport, Castr.Core.IntegrationTests, Castr.Core.Transport.Udp, Castr.Core.Tests.Transport, RealMulticastFanOutTests, SmokeTest

### Community 34 - "Wire Protocol & Packetizer"
Cohesion: 0.21
Nodes (4): Castr.Core.Tests.Protocol, Castr.Core.Protocol, WirePacketizer, int

### Community 35 - "Security & Trust Tests"
Cohesion: 0.15
Nodes (6): Castr.Core.Tests.Security, Castr.Core.Tests.Trust, Castr.Core.Security, TrustDecisionEngine, TrustEntrySource, TrustStatus

### Community 36 - "Trust Prompt Dialog (GUI)"
Cohesion: 0.17
Nodes (7): EventArgs, TrustPromptViewModel, RelayCommand, Task, TrustPromptDialog, TaskCompletionSource, WindowClosingEventArgs

### Community 37 - "File Source/Sink Tests"
Cohesion: 0.25
Nodes (7): CancellationToken, ReadOnlyMemory, ValueTask, FileSystemFileSourceSinkTests, Fact, string, Task

### Community 38 - "Real Socket Transport Tests"
Cohesion: 0.19
Nodes (9): CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask, CancellationToken, Fact, Task, Fact (+1 more)

### Community 39 - "Trust Decision Engine Tests"
Cohesion: 0.28
Nodes (4): TrustDecision, TrustOutcome, TrustDecisionEngineTests, Fact

### Community 40 - "TUI Receive Runner & Throughput"
Cohesion: 0.16
Nodes (8): Castr.Tui.Tests, Castr.Tui, Queue, ReceiveOptions, ThroughputSampler, Func, TimeSpan, Stopwatch

### Community 41 - "Trust Store JSON Codec"
Cohesion: 0.21
Nodes (9): JsonSerializerOptions, TrustEntry, TrustEntryDto, TrustStoreDocument, TrustStoreJsonCodec, DateTimeOffset, IReadOnlyList, List (+1 more)

### Community 42 - "Session Message Handlers"
Cohesion: 0.41
Nodes (3): CancellationToken, MerkleProof, Task

### Community 43 - "Dashboard Renderer Tests"
Cohesion: 0.37
Nodes (4): TransferDashboardRendererTests, Fact, InlineData, Theory

### Community 44 - "Network Interfaces & Path Safety"
Cohesion: 0.18
Nodes (7): Exception, InvalidInterfaceException, NetworkInterfaces, IPAddress, PathSafety, PathTraversalException, PromptBoomException

### Community 45 - "Chunker & File Reading"
Cohesion: 0.26
Nodes (9): Chunker, CancellationToken, Memory, Task, ValueTask, IFileSource, CancellationToken, Memory (+1 more)

### Community 46 - "Transfer Dashboard Runner"
Cohesion: 0.24
Nodes (8): Action, TransferDashboard, CancellationToken, Func, IAnsiConsole, Task, TimeSpan, TransferProgress

### Community 47 - "File System Sink"
Cohesion: 0.17
Nodes (8): bool, FileSystemFileSink, SafeFileHandle, string, IFileSink, CancellationToken, ReadOnlyMemory, ValueTask

### Community 48 - "Chunk Heatmap Rendering"
Cohesion: 0.21
Nodes (8): char, IEnumerable, IRenderable, Measurement, RenderOptions, Segment, ChunkHeatmap, Style

### Community 49 - "Endpoint Abstraction"
Cohesion: 0.20
Nodes (7): Endpoint, ReceivedPacket, IPEndPoint, CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask

### Community 50 - "Trust Store & Seed Merging"
Cohesion: 0.20
Nodes (5): ITrustStore, IReadOnlyList, TrustSeedMerger, IEnumerable, IReadOnlyList

### Community 51 - "Transport Factory"
Cohesion: 0.20
Nodes (6): ITransportFactory, IMulticastTransport, UdpTransportFactory, IMulticastTransport, int, IPAddress

### Community 52 - "Large Chunk Transfer Tests"
Cohesion: 0.32
Nodes (6): LargeChunkTransferTests, CancellationToken, Fact, IPAddress, Task, TimeSpan

### Community 53 - "Filtering Transport Test Support"
Cohesion: 0.24
Nodes (6): Castr.Core.Tests.TestSupport, FilteringMulticastTransport, CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask

### Community 54 - "Public Key ID Tests"
Cohesion: 0.38
Nodes (3): ReadOnlySpan, PublicKeyIdTests, Fact

### Community 55 - "Chaos Transport Simulation"
Cohesion: 0.24
Nodes (6): ChaosTransport, CancellationToken, IAsyncEnumerable, Random, ReadOnlyMemory, ValueTask

### Community 56 - "Dashboard Loop Tests"
Cohesion: 0.40
Nodes (5): FakeProgressSource, TransferDashboardLoopTests, bool, Fact, Task

### Community 57 - "Dashboard Renderer"
Cohesion: 0.36
Nodes (4): Color, TransferDashboardRenderer, IRenderable, Text

### Community 59 - "E2E Test Project Config"
Cohesion: 0.22
Nodes (8): net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.8.0), NSec.Cryptography (26.4.0), Testcontainers (4.13.0), xunit (2.5.3), xunit.runner.visualstudio (2.5.3), Microsoft.NET.Sdk

### Community 60 - "Transfer Builder"
Cohesion: 0.25
Nodes (6): PreparedTransfer, TransferBuilder, CancellationToken, IMulticastTransport, Key, Task

### Community 61 - "Chunker Hashing Tests"
Cohesion: 0.58
Nodes (3): ChunkerTests, Fact, Task

### Community 62 - "GUI Test Project Config"
Cohesion: 0.25
Nodes (8): Avalonia.Headless.XUnit (12.1.0), xunit.v3 (3.2.2), Castr.Gui.Tests, net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.12.0), xunit.runner.visualstudio (3.1.4), Microsoft.NET.Sdk

### Community 63 - "Receive Runner"
Cohesion: 0.54
Nodes (4): ReceiveRunner, CancellationToken, IAnsiConsole, Task

### Community 64 - "File System Source"
Cohesion: 0.25
Nodes (5): FileSystemFileSource, CancellationToken, Memory, SafeFileHandle, ValueTask

### Community 65 - "Memory File Sink"
Cohesion: 0.25
Nodes (5): MemoryFileSink, byte, CancellationToken, ReadOnlyMemory, ValueTask

### Community 66 - "CLI Test Project Config"
Cohesion: 0.25
Nodes (8): Castr.Cli.Tests, net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.8.0), Spectre.Console.Testing (0.49.1), xunit (2.5.3), xunit.runner.visualstudio (2.5.3), Microsoft.NET.Sdk

### Community 68 - "TUI Test Project Config"
Cohesion: 0.25
Nodes (8): Castr.Tui.Tests, net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.8.0), Spectre.Console.Testing (0.49.1), xunit (2.5.3), xunit.runner.visualstudio (2.5.3), Microsoft.NET.Sdk

### Community 69 - "GUI Desktop Entry Point"
Cohesion: 0.33
Nodes (4): Castr.Gui.Desktop, Program, AppBuilder, STAThread

### Community 70 - "Unicast Transport Interface"
Cohesion: 0.48
Nodes (4): IAsyncDisposable, IMulticastTransport, IUnicastTransport, TimeSpan

### Community 71 - "In-Memory Transport Factory"
Cohesion: 0.33
Nodes (4): InMemoryNetwork, InMemoryTransportFactory, IMulticastTransport, int

### Community 72 - "GUI Desktop Project Config"
Cohesion: 0.29
Nodes (7): Avalonia.Desktop (12.1.0), AvaloniaUI.DiagnosticsSupport (2.2.3), Castr.Gui.Desktop, net10.0, Avalonia (12.1.0), Avalonia.Fonts.Inter (12.1.0), Microsoft.NET.Sdk

### Community 73 - "GUI Project Config"
Cohesion: 0.29
Nodes (7): Avalonia.Themes.Fluent (12.1.0), CommunityToolkit.Mvvm (8.4.2), Castr.Gui, net10.0, Avalonia (12.1.0), Avalonia.Fonts.Inter (12.1.0), Microsoft.NET.Sdk

### Community 74 - "Console Progress Reporter"
Cohesion: 0.33
Nodes (3): ConsoleProgressReporter, int, object

### Community 75 - "LLM Wiki Schema & Conventions"
Cohesion: 0.40
Nodes (6): LLM Wiki maintenance convention, Wiki page template, Wiki Graph Ontology, Wiki Graph Layer docs, Wiki optional graph metadata (graph: key), Wiki Schema (conventions)

### Community 76 - "Memory File Source"
Cohesion: 0.33
Nodes (4): MemoryFileSource, CancellationToken, Memory, ValueTask

### Community 77 - "Chunk Position Binding Tests"
Cohesion: 0.33
Nodes (4): ChunkPositionBindingTests, byte, Fact, Task

### Community 78 - "GUI View Locator"
Cohesion: 0.40
Nodes (3): Control, IDataTemplate, ViewLocator

### Community 79 - "Trust Store Bootstrap"
Cohesion: 0.40
Nodes (4): FileTrustStore, Merged, TrustStoreBootstrap, Store

### Community 80 - "Multicast Interface Discovery"
Cohesion: 0.40
Nodes (3): IPAddress, MulticastInterfaces, IReadOnlyList

### Community 81 - "Castr Paths Config"
Cohesion: 0.40
Nodes (4): CastrPaths, int, IPAddress, string

### Community 83 - "Trust Seed File Schema"
Cohesion: 0.40
Nodes (4): comment, entries, $schema, version

### Community 84 - "Project Planning Conventions"
Cohesion: 0.50
Nodes (4): graphify codebase graph convention, graphify + llm-wiki mandatory infrastructure (plan), Milestone plan M0-M5 (plan), Verification/testing approach (plan)

## Knowledge Gaps
- **100 isolated node(s):** `Castr.Core.Discovery`, `Class1`, `MerkleSide`, `MerkleProofStep`, `TrustOutcome` (+95 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **43 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Castr.Core.Protocol` connect `Wire Protocol & Packetizer` to `Peer Table Interface`, `Transport Integration Tests`, `Message Codec`, `TUI Receive Runner & Throughput`, `Transfer Preparation`, `Console Progress Reporter`, `Chunk Packet Assembly`, `Transfer Dashboard Runner`, `Packet Reassembly`, `Receiver Session`, `Chunk Bitmap`, `Filtering Transport Test Support`, `Avalonia GUI Project Structure`, `Dashboard Renderer`, `Transfer Builder`, `Transfer Progress Model`?**
  _High betweenness centrality (0.314) - this node is a cross-community bridge._
- **Why does `Castr.Core.Manifest` connect `Manifest Codec` to `Transport Integration Tests`, `Content Key Encryption`, `Chunk Hash Value Type`?**
  _High betweenness centrality (0.126) - this node is a cross-community bridge._
- **Why does `Castr.Core.Trust` connect `CLI Trust Commands` to `Security & Trust Tests`, `Trust Decision Engine Tests`, `TUI Receive Runner & Throughput`, `Trust Store JSON Codec`, `Trust Prompt Abstraction`, `File Trust Store`, `Trust Store & Seed Merging`, `Avalonia GUI Project Structure`?**
  _High betweenness centrality (0.107) - this node is a cross-community bridge._
- **What connects `Castr.Core.Discovery`, `Class1`, `MerkleSide` to the rest of the system?**
  _100 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Peer Table Interface` be split into smaller, more focused modules?**
  _Cohesion score 0.059298245614035086 - nodes in this community are weakly interconnected._
- **Should `Core Transfer Orchestration` be split into smaller, more focused modules?**
  _Cohesion score 0.05875251509054326 - nodes in this community are weakly interconnected._
- **Should `Content Key Encryption` be split into smaller, more focused modules?**
  _Cohesion score 0.05548654244306418 - nodes in this community are weakly interconnected._