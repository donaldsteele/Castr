---
type: synthesis
title: "ADR-0002: Mobile peer discovery — native NsdManager (Android) / NWBrowser (iOS), Info.plist requirements"
tags: [decision, spike-result, platform-quirk]
sources: [castr-project-plan]
created: 2026-07-24
updated: 2026-07-24
---

# ADR-0002: Mobile peer discovery — native NsdManager (Android) / NWBrowser (iOS)

**Status: Research-validated; hands-on device validation deferred to M4.** Resolves the second open risk flagged in [[tech-stack]].

## Decision

Confirmed as directly implementable in managed C# with no extra native interop layer required:

- **Android**: `Android.Net.Nsd.NsdManager` (`.NET for Android` binding of the platform's own `android.net.nsd.NsdManager`) — accessible directly from C# in `Castr.Gui.Android`/`Castr.Core.Discovery`'s Android implementation. Documented at [Microsoft Learn: Android.Net.Nsd](https://learn.microsoft.com/en-us/dotnet/api/android.net.nsd.nsdmanager).
- **iOS/macOS**: `NWBrowser` (Apple's `Network.framework`) via the `dotnet/macios` open-source bindings — same story, directly usable from C# without P/Invoke or a bundled native library. Source: [dotnet/macios NWBrowser.cs](https://github.com/dotnet/macios/blob/main/src/Network/NWBrowser.cs).

This directly confirms the [[castr-project]] design choice: pure-managed mDNS libraries (e.g. `Makaretu.Dns`) would themselves be multicast-socket-based and hit the same iOS restriction that motivated the unicast-swarm mobile tier in the first place — only these OS-mediated native APIs sidestep that restriction, and both are reachable from .NET without extra binding work.

## iOS Info.plist requirements (the easy-to-miss gotcha)

Two entries are required in `Info.plist` for local-network Bonjour/mDNS discovery to work at all on iOS 14+:

- `NSLocalNetworkUsageDescription` — a string, the user-facing permission-prompt explanation for why the app needs local network access.
- `NSBonjourServices` — an array of service-type strings the app browses/advertises, formatted `_servicename._tcp` (or `_udp`). Must exactly match the type string used in both `NWBrowser`/`NWListener` code and Android's `NsdServiceInfo.ServiceType` for cross-platform discovery to actually find each other. Proposed value for Castr: `_castr._tcp`.

**Why this is called out as a spike item rather than left for M4**: Xcode debug/simulator runs can behave more permissively around these entitlement checks than a real signed distribution build, so a missing or mistyped `NSBonjourServices` entry can pass local development testing and only fail at TestFlight/sideload-install time — exactly the kind of failure that's expensive to discover late. M4 must include an explicit test pass on a signed, non-debug build, not just simulator runs.

## Follow-up deferred to M4 (not blocking M0)

- Hands-on validation requires `dotnet workload install android ios` (not installed in the M0 environment — no macOS/iOS toolchain or Android emulator available here) plus a physical or virtual LAN with real devices to confirm cross-platform discovery actually finds peers (Android `NsdManager` finding an iOS `NWListener` advertisement and vice versa) — mDNS record format is standardized (RFC 6762/6763) but real-device interop is worth confirming directly rather than assuming from the API docs alone.
- Exact Android runtime permission requirements (`ACCESS_WIFI_STATE`/`ACCESS_NETWORK_STATE`, and whether `NEARBY_WIFI_DEVICES` is required on Android 13+ for NSD specifically) were not conclusively verified in this research pass — confirm precisely at M4 implementation time against whatever Android API level Castr targets.

## Where this fits

- [[castr-project]]
- [[tech-stack]]
- [[repair-protocol]]
- [[roadmap]]
