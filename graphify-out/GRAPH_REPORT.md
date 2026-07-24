# Graph Report - .  (2026-07-24)

## Corpus Check
- 69 files · ~49,208 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1504 nodes · 3017 edges · 103 communities (79 shown, 24 thin omitted)
- Extraction: 95% EXTRACTED · 5% INFERRED · 0% AMBIGUOUS · INFERRED: 163 edges (avg confidence: 0.81)
- Token cost: 111,784 input · 0 output

## Community Hubs (Navigation)
- Peer Table
- Project Glossary & Overview
- Manifest Binary Codec
- Message Wire Codec
- Content Encryption Key
- In-Memory Multicast Transport
- Sender Session
- Sender Identity & Send Runner
- End-to-End Transfer Tests
- Trust Prompt Interface
- CLI Command Builder
- Chunk Hash
- Network Interfaces & Path Safety
- CLI Program & Exit Codes
- Project Plan Decisions
- Chunk Bitmap
- In-Memory Trust Store
- UDP Unicast Transport
- Transfer Dashboard E2E Tests
- In-Memory Network Chaos Tests
- Receiver Session
- Gui ViewModels & Views
- Transfer Progress
- Chunk Layout & Ranges
- Multicast Fan-Out Tests
- Receive View Model
- CLI Parsing Tests
- Gui App Bootstrap & Identity
- Receiver Message Handling
- File Trust Store
- Core Protocol Test Files
- Trust Decision Engine
- Filesystem File I/O Tests
- Chunker & Memory File Source
- Send View Model
- Transfer Dashboard Renderer Tests
- Chunker
- Filesystem File Sink
- Chunk Heatmap Renderer
- UDP Transport Integration Tests
- Trust Decision Engine Tests
- Transfer Dashboard
- Core/Cli/Tui Project Dependencies
- Unicast Transport Interface
- Filesystem File Source
- Filtering Multicast Transport (test support)
- Gui Windows & Dialogs
- Trust Store JSON Codec
- Receive Runner
- Public Key Id Tests
- Chaos Transport
- Transfer Dashboard Loop Tests
- Transfer Dashboard Renderer
- Trust Store JSON Codec Tests
- Transfer Builder
- Trust Prompt View Model
- Real Transfer Repair Tests
- Chunker Tests
- Gui Tests Project Dependencies
- Memory File Sink
- Cli Tests Project Dependencies
- Gui Main Window Tests
- Tui Tests Project Dependencies
- Gui Desktop Entry Point
- In-Memory Transport Factory
- Gui Desktop Project Dependencies
- Gui Project Dependencies
- Main View Model
- Throughput Sampler
- Console Progress Reporter
- Public Key Id
- UDP Transport Factory
- Solution & Test Project Layout
- Wiki Schema & Ontology Docs
- CI & Milestone Plan Docs
- View Locator
- E2E Tests Placeholder (fragment)
- Trust Store Bootstrap
- Multicast Interfaces
- Castr Paths
- Transport Factory Interface
- Trusted Senders Seed File
- Discovery Placeholder Class (fragment)
- Trust Denied Handling (fragment)
- Platform & Tech Stack Notes (fragment)
- Roadmap Convention (fragment)
- HashSet (fragment)
- ISystemClock (fragment)
- Repo Layout Plan (fragment)
- Signed (fragment)
- Sources (fragment)
- byte (fragment)
- Dictionary (fragment)
- MerkleProof (fragment)
- ReceivedPacket (fragment)
- IFileSource (fragment)
- Fact (fragment)
- Dictionary (fragment)
- IAsyncEnumerable (fragment)
- IReadOnlyList (fragment)
- ReadOnlyMemory (fragment)
- ValueTask (fragment)
- Trees (fragment)

## God Nodes (most connected - your core abstractions)
1. `ReceiverSession` - 41 edges
2. `Castr.Core.Trust` - 34 edges
3. `M2 — CLI, TUI, Desktop GUI: implementation summary` - 31 edges
4. `Castr.Core.Protocol` - 30 edges
5. `Castr roadmap and milestone status` - 28 edges
6. `SenderSession` - 26 edges
7. `ReceiveViewModel` - 25 edges
8. `Castr wire protocol` - 25 edges
9. `Castr.Core.Security` - 22 edges
10. `Castr technology stack` - 22 edges

## Surprising Connections (you probably didn't know these)
- `CI build-and-test job` --semantically_similar_to--> `Verification/testing approach (plan)`  [INFERRED] [semantically similar]
  .github/workflows/ci.yml → raw/2026-07-24-castr-project-plan.md
- `DialogTrustPrompt` --semantically_similar_to--> `ConsoleTrustPrompt`  [INFERRED] [semantically similar]
  src/Castr.Gui/README.md → wiki/synthesis/m2-ui-summary.md
- `Mobile unicast swarm client decision` --rationale_for--> `Castr (product entity)`  [INFERRED]
  raw/2026-07-24-castr-project-plan.md → wiki/entities/castr-project.md
- `RepairCoordinator (plan)` --rationale_for--> `Castr repair protocol`  [INFERRED]
  raw/2026-07-24-castr-project-plan.md → wiki/concepts/repair-protocol.md
- `TrustDecisionEngineTests` --references--> `PublicKeyId`  [EXTRACTED]
  tests/Castr.Core.Tests/Trust/TrustDecisionEngineTests.cs → src/Castr.Core/Security/PublicKeyId.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **M2 Core observability & trust-prompt contract** — concept_transferprogress, concept_itrustprompt, concept_sendersession, concept_receiversession, concept_dialogtrustprompt, concept_consoletrustprompt, concept_trustdecisionengine [EXTRACTED 1.00]
- **Castr wire protocol message types** — concept_announce_message, concept_manifest_message, concept_join_request_message, concept_key_grant_message, concept_chunk_data_message, concept_peer_have_message, concept_chunk_request_response_message, concept_transfer_complete_message [EXTRACTED 1.00]
- **M2 parallel UI surfaces built on Castr.Core** — concept_castr_core, concept_castr_cli, concept_castr_tui, concept_castr_gui, concept_castr_gui_desktop [EXTRACTED 1.00]
- **M1.5 payload-encryption feature (decision, implementation, and mechanism)** — wiki_synthesis_adr_0003_payload_encryption, wiki_synthesis_m1_5_encryption_summary, wiki_concepts_security_model_payload_encryption [INFERRED 0.85]
- **Castr core architecture concept cluster** — wiki_entities_castr_project_castr, wiki_concepts_repair_protocol_repair_protocol [EXTRACTED 1.00]
- **Definition-of-done gate applied at every milestone** — raw_2026_07_24_castr_project_plan_milestones, raw_2026_07_24_castr_project_plan_graphify_llm_wiki_infra [EXTRACTED 1.00]

## Communities (103 total, 24 thin omitted)

### Community 0 - "Peer Table"
Cohesion: 0.05
Nodes (35): Castr.Core.Time, IReadOnlyCollection, IPeerTable, PeerInfo, DateTimeOffset, IReadOnlyList, PeerHaveMessage, PeerEntry (+27 more)

### Community 1 - "Project Glossary & Overview"
Cohesion: 0.08
Nodes (72): ANNOUNCE message, Avalonia UI, BLAKE3, Castr.Cli, Castr.Cli --chunk-size fail-fast guard, Castr.Core, Castr.Core.Discovery, Castr.Gui (+64 more)

### Community 2 - "Manifest Binary Codec"
Cohesion: 0.06
Nodes (23): Castr.Core.Tests.Manifest, Castr.Core.Manifest, ManifestCodec, SpanReader, byte, ReadOnlySpan, Stream, ManifestSigner (+15 more)

### Community 3 - "Message Wire Codec"
Cohesion: 0.08
Nodes (22): SpanReader, MessageCodec, SpanReader, byte, int, MerkleProof, ReadOnlySpan, Stream (+14 more)

### Community 4 - "Content Encryption Key"
Cohesion: 0.06
Nodes (30): ChunkHash, KeyAgreementAlgorithm, KeyDerivationAlgorithm, SharedSecret, ContentKey, AeadAlgorithm, int, Key (+22 more)

### Community 5 - "In-Memory Multicast Transport"
Cohesion: 0.05
Nodes (38): Castr.Core.Transport.InMemory, IMulticastTransport, Lock, ChaosOptions, InMemoryMulticastTransport, CancellationToken, Channel, ChannelWriter (+30 more)

### Community 6 - "Sender Session"
Cohesion: 0.10
Nodes (26): IFileSource, JoinRequestMessage, MerkleTree, SenderSession, bool, CancellationToken, ChunkRequestMessage, HashSet (+18 more)

### Community 7 - "Sender Identity & Send Runner"
Cohesion: 0.07
Nodes (23): SenderIdentity, Key, PublicKeyId, SendOptions, SendRunner, CancellationToken, IAnsiConsole, Task (+15 more)

### Community 8 - "End-to-End Transfer Tests"
Cohesion: 0.13
Nodes (20): Factory, GetSink, IAsyncEnumerable, IFileSink, IReadOnlyList, MemoryFileSink, ReadOnlyMemory, EndToEndTransferTests (+12 more)

### Community 9 - "Trust Prompt Interface"
Cohesion: 0.10
Nodes (21): ConsoleTrustPrompt, CancellationToken, Task, ITrustPrompt, TrustPromptContext, CancellationToken, Task, AutoTrustPrompt (+13 more)

### Community 10 - "CLI Command Builder"
Cohesion: 0.13
Nodes (14): Command, Option, CastrCli, IAnsiConsole, IPAddress, RootCommand, TrustStatus, TrustRunner (+6 more)

### Community 11 - "Chunk Hash"
Cohesion: 0.12
Nodes (12): IEquatable, ChunkHash, byte, int, ReadOnlySpan, ReadOnlySpan, ChunkHashTests, Fact (+4 more)

### Community 12 - "Network Interfaces & Path Safety"
Cohesion: 0.13
Nodes (11): Exception, InvalidInterfaceException, NetworkInterfaces, IPAddress, PathSafety, PathTraversalException, PathSafetyTests, Fact (+3 more)

### Community 13 - "CLI Program & Exit Codes"
Cohesion: 0.13
Nodes (9): Castr.Cli.Tests, Castr.Core.Trust, Castr.Cli, ExitCodes, int, TrustDecision, TrustOutcome, TrustSeedMerger (+1 more)

### Community 14 - "Project Plan Decisions"
Cohesion: 0.11
Nodes (23): Core-first build phasing decision, Avalonia GUI framework decision, IPeerTable abstraction (plan), Mobile unicast swarm client decision, Path safety (no traversal) decision, Integrity-only payload security decision, Castr approved project plan, Repair protocol design (plan) (+15 more)

### Community 15 - "Chunk Bitmap"
Cohesion: 0.18
Nodes (7): ChunkBitmap, byte, IEnumerable, ChunkBitmapTests, Fact, InlineData, Theory

### Community 16 - "In-Memory Trust Store"
Cohesion: 0.17
Nodes (10): InMemoryTrustStore, Dictionary, IReadOnlyList, ITrustStore, IReadOnlyList, TrustEntry, IEnumerable, IReadOnlyList (+2 more)

### Community 17 - "UDP Unicast Transport"
Cohesion: 0.17
Nodes (13): IUnicastTransport, Endpoint, UdpUnicastTransport, CancellationToken, EndPoint, IAsyncEnumerable, ReadOnlyMemory, ReceivedPacket (+5 more)

### Community 18 - "Transfer Dashboard E2E Tests"
Cohesion: 0.14
Nodes (12): Transfer, TransferDashboardEndToEndTests, CancellationToken, Fact, Factory, Func, GetSink, IFileSink (+4 more)

### Community 19 - "In-Memory Network Chaos Tests"
Cohesion: 0.29
Nodes (7): Endpoint, IMulticastTransport, IUnicastTransport, InMemoryNetworkTests, Fact, Task, TimeSpan

### Community 20 - "Receiver Session"
Cohesion: 0.11
Nodes (17): byte, ContentKey, Dictionary, IPeerTable, RepairCoordinator, SignedManifest, ReceiverSession, ReceiverSessionOptions (+9 more)

### Community 21 - "Gui ViewModels & Views"
Cohesion: 0.17
Nodes (9): Castr.Gui.Services, Castr.Gui, Castr.Gui.ViewModels, Castr.Gui.Trust, Castr.Gui.Tests, Castr.Gui.Views, TransferFlowTests, AvaloniaFact (+1 more)

### Community 22 - "Transfer Progress"
Cohesion: 0.18
Nodes (9): double, TransferPhase, TransferProgress, TransferRole, TransferProgressViewModel, bool, int, long (+1 more)

### Community 23 - "Chunk Layout & Ranges"
Cohesion: 0.20
Nodes (8): ChunkLayout, ChunkRange, IEnumerable, int, ChunkLayoutTests, Fact, InlineData, Theory

### Community 24 - "Multicast Fan-Out Tests"
Cohesion: 0.18
Nodes (11): IMulticastTransport, CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask, RealMulticastFanOutTests, CancellationToken, Fact (+3 more)

### Community 25 - "Receive View Model"
Cohesion: 0.16
Nodes (13): ReceiveViewModel, bool, CancellationToken, CancellationTokenSource, Func, IMulticastTransport, int, ISystemClock (+5 more)

### Community 26 - "CLI Parsing Tests"
Cohesion: 0.24
Nodes (6): ParsingTests, Fact, InlineData, RootCommand, Theory, UnknownSenderPolicy

### Community 27 - "Gui App Bootstrap & Identity"
Cohesion: 0.16
Nodes (8): Application, IClassicDesktopStyleApplicationLifetime, App, CastrIdentity, Key, StoragePickers, Task, Visual

### Community 28 - "Receiver Message Handling"
Cohesion: 0.28
Nodes (7): KeyGrantMessage, ManifestMessage, MerkleProof, ReceivedPacket, CancellationToken, ChunkRequestMessage, Task

### Community 29 - "File Trust Store"
Cohesion: 0.19
Nodes (6): FileTrustStore, IReadOnlyList, string, FileTrustStoreTests, Fact, string

### Community 30 - "Core Protocol Test Files"
Cohesion: 0.22
Nodes (4): Castr.Tui.Tests, Castr.Core.Tests.Protocol, Castr.Tui, Castr.Core.Protocol

### Community 31 - "Trust Decision Engine"
Cohesion: 0.17
Nodes (6): Castr.Core.Tests.Security, Castr.Core.Tests.Trust, Castr.Core.Security, TrustDecisionEngine, TrustEntrySource, TrustStatus

### Community 32 - "Filesystem File I/O Tests"
Cohesion: 0.25
Nodes (7): CancellationToken, ReadOnlyMemory, ValueTask, FileSystemFileSourceSinkTests, Fact, string, Task

### Community 33 - "Chunker & Memory File Source"
Cohesion: 0.18
Nodes (6): Castr.Core.Tests.Chunking, Castr.Core.Chunking, MemoryFileSource, CancellationToken, Memory, ValueTask

### Community 34 - "Send View Model"
Cohesion: 0.21
Nodes (10): SendViewModel, bool, CancellationTokenSource, Func, IMulticastTransport, int, Key, RelayCommand (+2 more)

### Community 35 - "Transfer Dashboard Renderer Tests"
Cohesion: 0.37
Nodes (4): TransferDashboardRendererTests, Fact, InlineData, Theory

### Community 36 - "Chunker"
Cohesion: 0.26
Nodes (9): Chunker, CancellationToken, Memory, Task, ValueTask, IFileSource, CancellationToken, Memory (+1 more)

### Community 37 - "Filesystem File Sink"
Cohesion: 0.17
Nodes (8): bool, FileSystemFileSink, SafeFileHandle, string, IFileSink, CancellationToken, ReadOnlyMemory, ValueTask

### Community 38 - "Chunk Heatmap Renderer"
Cohesion: 0.21
Nodes (8): char, IEnumerable, IRenderable, Measurement, RenderOptions, Segment, ChunkHeatmap, Style

### Community 39 - "UDP Transport Integration Tests"
Cohesion: 0.23
Nodes (5): Castr.Core.Transport, Castr.Core.IntegrationTests, Castr.Core.Transport.Udp, Castr.Core.Tests.Transport, SmokeTest

### Community 41 - "Transfer Dashboard"
Cohesion: 0.25
Nodes (7): Action, TransferDashboard, CancellationToken, Func, IAnsiConsole, Task, TimeSpan

### Community 42 - "Core/Cli/Tui Project Dependencies"
Cohesion: 0.20
Nodes (11): Castr.Core, Castr.Core.Discovery, System.CommandLine (2.0.0), Castr.Cli, net10.0, Spectre.Console (0.49.1), Microsoft.NET.Sdk, Castr.Tui (+3 more)

### Community 43 - "Unicast Transport Interface"
Cohesion: 0.20
Nodes (7): IAsyncDisposable, ReceivedPacket, IUnicastTransport, CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask

### Community 44 - "Filesystem File Source"
Cohesion: 0.18
Nodes (7): IDisposable, FileSystemFileSource, CancellationToken, Memory, SafeFileHandle, ValueTask, TempDir

### Community 45 - "Filtering Multicast Transport (test support)"
Cohesion: 0.24
Nodes (6): Castr.Core.Tests.TestSupport, FilteringMulticastTransport, CancellationToken, IAsyncEnumerable, ReadOnlyMemory, ValueTask

### Community 46 - "Gui Windows & Dialogs"
Cohesion: 0.20
Nodes (5): EventArgs, MainWindow, TrustPromptDialog, Window, WindowClosingEventArgs

### Community 47 - "Trust Store JSON Codec"
Cohesion: 0.24
Nodes (7): JsonSerializerOptions, TrustEntryDto, TrustStoreDocument, TrustStoreJsonCodec, DateTimeOffset, List, TrustEntryDto

### Community 48 - "Receive Runner"
Cohesion: 0.40
Nodes (5): ReceiveOptions, ReceiveRunner, CancellationToken, IAnsiConsole, Task

### Community 49 - "Public Key Id Tests"
Cohesion: 0.38
Nodes (3): ReadOnlySpan, PublicKeyIdTests, Fact

### Community 50 - "Chaos Transport"
Cohesion: 0.24
Nodes (6): ChaosTransport, CancellationToken, IAsyncEnumerable, Random, ReadOnlyMemory, ValueTask

### Community 51 - "Transfer Dashboard Loop Tests"
Cohesion: 0.40
Nodes (5): FakeProgressSource, TransferDashboardLoopTests, bool, Fact, Task

### Community 52 - "Transfer Dashboard Renderer"
Cohesion: 0.36
Nodes (4): Color, TransferDashboardRenderer, IRenderable, Text

### Community 53 - "Trust Store JSON Codec Tests"
Cohesion: 0.36
Nodes (3): IReadOnlyList, TrustStoreJsonCodecTests, Fact

### Community 54 - "Transfer Builder"
Cohesion: 0.25
Nodes (6): PreparedTransfer, TransferBuilder, CancellationToken, IMulticastTransport, Key, Task

### Community 55 - "Trust Prompt View Model"
Cohesion: 0.33
Nodes (4): TrustPromptViewModel, RelayCommand, Task, TaskCompletionSource

### Community 56 - "Real Transfer Repair Tests"
Cohesion: 0.36
Nodes (5): RealTransferRepairTests, CancellationToken, Fact, Task, TimeSpan

### Community 57 - "Chunker Tests"
Cohesion: 0.58
Nodes (3): ChunkerTests, Fact, Task

### Community 58 - "Gui Tests Project Dependencies"
Cohesion: 0.25
Nodes (8): Avalonia.Headless.XUnit (12.1.0), xunit.v3 (3.2.2), Castr.Gui.Tests, net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.12.0), xunit.runner.visualstudio (3.1.4), Microsoft.NET.Sdk

### Community 59 - "Memory File Sink"
Cohesion: 0.25
Nodes (5): MemoryFileSink, byte, CancellationToken, ReadOnlyMemory, ValueTask

### Community 60 - "Cli Tests Project Dependencies"
Cohesion: 0.25
Nodes (8): Castr.Cli.Tests, net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.8.0), Spectre.Console.Testing (0.49.1), xunit (2.5.3), xunit.runner.visualstudio (2.5.3), Microsoft.NET.Sdk

### Community 61 - "Gui Main Window Tests"
Cohesion: 0.25
Nodes (4): MainWindowTests, AvaloniaFact, TestAppBuilder, AppBuilder

### Community 62 - "Tui Tests Project Dependencies"
Cohesion: 0.25
Nodes (8): Castr.Tui.Tests, net10.0, coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.8.0), Spectre.Console.Testing (0.49.1), xunit (2.5.3), xunit.runner.visualstudio (2.5.3), Microsoft.NET.Sdk

### Community 63 - "Gui Desktop Entry Point"
Cohesion: 0.33
Nodes (4): Castr.Gui.Desktop, Program, AppBuilder, STAThread

### Community 64 - "In-Memory Transport Factory"
Cohesion: 0.33
Nodes (4): InMemoryNetwork, InMemoryTransportFactory, IMulticastTransport, int

### Community 65 - "Gui Desktop Project Dependencies"
Cohesion: 0.29
Nodes (7): Avalonia.Desktop (12.1.0), AvaloniaUI.DiagnosticsSupport (2.2.3), Castr.Gui.Desktop, net10.0, Avalonia (12.1.0), Avalonia.Fonts.Inter (12.1.0), Microsoft.NET.Sdk

### Community 66 - "Gui Project Dependencies"
Cohesion: 0.29
Nodes (7): Avalonia.Themes.Fluent (12.1.0), CommunityToolkit.Mvvm (8.4.2), Castr.Gui, net10.0, Avalonia (12.1.0), Avalonia.Fonts.Inter (12.1.0), Microsoft.NET.Sdk

### Community 67 - "Main View Model"
Cohesion: 0.29
Nodes (4): ObservableObject, MainViewModel, int, ViewModelBase

### Community 68 - "Throughput Sampler"
Cohesion: 0.29
Nodes (5): Queue, ThroughputSampler, Func, TimeSpan, Stopwatch

### Community 69 - "Console Progress Reporter"
Cohesion: 0.33
Nodes (3): ConsoleProgressReporter, int, object

### Community 71 - "UDP Transport Factory"
Cohesion: 0.33
Nodes (4): UdpTransportFactory, IMulticastTransport, int, IPAddress

### Community 72 - "Solution & Test Project Layout"
Cohesion: 0.33
Nodes (3): Castr.Core.E2ETests, Castr.Core.IntegrationTests, Castr.Core.Tests

### Community 73 - "Wiki Schema & Ontology Docs"
Cohesion: 0.40
Nodes (6): LLM Wiki maintenance convention, Wiki page template, Wiki Graph Ontology, Wiki Graph Layer docs, Wiki optional graph metadata (graph: key), Wiki Schema (conventions)

### Community 74 - "CI & Milestone Plan Docs"
Cohesion: 0.40
Nodes (5): graphify codebase graph convention, CI build-and-test job, graphify + llm-wiki mandatory infrastructure (plan), Milestone plan M0-M5 (plan), Verification/testing approach (plan)

### Community 75 - "View Locator"
Cohesion: 0.40
Nodes (3): Control, IDataTemplate, ViewLocator

### Community 76 - "E2E Tests Placeholder (fragment)"
Cohesion: 0.40
Nodes (3): Castr.Core.E2ETests, UnitTest1, Fact

### Community 77 - "Trust Store Bootstrap"
Cohesion: 0.40
Nodes (4): FileTrustStore, Merged, TrustStoreBootstrap, Store

### Community 78 - "Multicast Interfaces"
Cohesion: 0.40
Nodes (3): IPAddress, MulticastInterfaces, IReadOnlyList

### Community 79 - "Castr Paths"
Cohesion: 0.40
Nodes (4): CastrPaths, int, IPAddress, string

### Community 81 - "Trusted Senders Seed File"
Cohesion: 0.40
Nodes (4): comment, entries, $schema, version

## Knowledge Gaps
- **91 isolated node(s):** `Castr.Core.Discovery`, `Class1`, `MerkleSide`, `MerkleProofStep`, `TrustOutcome` (+86 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **24 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Castr.Core.Protocol` connect `Core Protocol Test Files` to `Peer Table`, `Message Wire Codec`, `Console Progress Reporter`, `Sender Identity & Send Runner`, `UDP Transport Integration Tests`, `Transfer Dashboard`, `Filtering Multicast Transport (test support)`, `Receive Runner`, `Receiver Session`, `Gui ViewModels & Views`, `Transfer Progress`, `Transfer Builder`, `Transfer Dashboard Renderer`?**
  _High betweenness centrality (0.329) - this node is a cross-community bridge._
- **Why does `Castr.Core.Trust` connect `CLI Program & Exit Codes` to `Trust Prompt Interface`, `Trust Store JSON Codec`, `Receive Runner`, `In-Memory Trust Store`, `Receiver Session`, `Gui ViewModels & Views`, `Trust Prompt View Model`, `File Trust Store`, `Core Protocol Test Files`, `Trust Decision Engine`?**
  _High betweenness centrality (0.161) - this node is a cross-community bridge._
- **Why does `Castr.Core.Manifest` connect `Manifest Binary Codec` to `Message Wire Codec`, `Content Encryption Key`, `Core Protocol Test Files`, `UDP Transport Integration Tests`?**
  _High betweenness centrality (0.156) - this node is a cross-community bridge._
- **What connects `Castr.Core.Discovery`, `Class1`, `MerkleSide` to the rest of the system?**
  _91 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Peer Table` be split into smaller, more focused modules?**
  _Cohesion score 0.05493827160493827 - nodes in this community are weakly interconnected._
- **Should `Project Glossary & Overview` be split into smaller, more focused modules?**
  _Cohesion score 0.08059467918622848 - nodes in this community are weakly interconnected._
- **Should `Manifest Binary Codec` be split into smaller, more focused modules?**
  _Cohesion score 0.05879917184265011 - nodes in this community are weakly interconnected._