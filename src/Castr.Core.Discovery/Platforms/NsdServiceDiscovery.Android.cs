using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Android.Content;
using Android.Net.Nsd;
using Castr.Core.Transport;
using Java.Lang;

namespace Castr.Core.Discovery;

// =====================================================================================================
// Android NSD (Network Service Discovery) implementation of IServiceDiscovery.
//
// Uses android.net.nsd.NsdManager directly via its .NET for Android binding (Android.Net.Nsd), exactly
// as validated on paper in ADR-0002 — no extra native interop layer. Advertise -> RegisterService,
// browse -> DiscoverServices + per-service ResolveService (a found service carries only a name; you
// must resolve it to get the host+port that becomes the Endpoint).
//
// -----------------------------------------------------------------------------------------------------
// PERMISSIONS (resolves ADR-0002's open question). Researched July 2026 against current Android docs.
//
// Required in Castr.Gui.Android's AndroidManifest.xml (all INSTALL-TIME / "normal" permissions — no
// runtime prompt needed):
//     <uses-permission android:name="android.permission.INTERNET" />
//     <uses-permission android:name="android.permission.ACCESS_WIFI_STATE" />
//     <uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
//
// NEARBY_WIFI_DEVICES (Android 13 / API 33+): NOT required for NsdManager service discovery.
//   The official "Request permission to access nearby Wi-Fi devices" list of APIs gated behind
//   NEARBY_WIFI_DEVICES is limited to WifiManager.startLocalOnlyHotspot, WifiAwareManager/Session,
//   WifiP2pManager (Wi-Fi Direct: discoverPeers/discoverServices/connect/createGroup/...), and
//   WifiRttManager.startRanging. NsdManager (discoverServices/registerService/resolveService) does
//   NOT appear on that list, so no NEARBY_WIFI_DEVICES and no runtime permission is needed for NSD on
//   Android 13/14/15. (Pre-Android-13 NSD likewise did not require ACCESS_FINE_LOCATION.)
//   Source: https://developer.android.com/develop/connectivity/wifi/wifi-permissions#check-for-apis-that-require-permission
//
//   Caveat for a FUTURE Android version, flagged so it is not a surprise later: Android's new
//   "Local network permission" regime makes LAN access (including mDNS/.local resolution) gated.
//   On Android 16 (API 36) it is OPT-IN and explicitly does NOT affect out-of-process APIs like
//   NsdManager. On Android 17+ (API 37+) it becomes mandatory as ACCESS_LOCAL_NETWORK; NsdManager
//   .local resolution then needs it unless the new system-picker DiscoveryRequest API is used. Castr
//   targets today's stable Android, so this is a forward-looking note, not a current requirement.
//   Source: https://developer.android.com/privacy-and-security/local-network-permission
//
// -----------------------------------------------------------------------------------------------------
// VERIFICATION HONESTY: This file is written against the documented NsdManager binding API and is
// verified only to COMPILE for net10.0-android on this Windows dev machine. It has NEVER been run
// against a real device or emulator (no Android hardware/emulator available here), so runtime behavior
// — actual discovery, resolve success, cross-platform interop with the iOS NWListener side — is
// UNVERIFIED. Hands-on device validation is the M4 follow-up from ADR-0002.
// =====================================================================================================

/// <summary>
/// Android <see cref="NsdManager"/>-backed <see cref="IServiceDiscovery"/>. Compiled only into the
/// <c>net10.0-android</c> target. See the file header for required manifest permissions and the
/// (negative) NEARBY_WIFI_DEVICES finding.
/// </summary>
public sealed class NsdServiceDiscovery : IServiceDiscovery
{
    private readonly NsdManager _nsdManager;
    private RegistrationListener? _registrationListener;
    private DiscoveryListener? _discoveryListener;
    private bool _disposed;

    /// <param name="context">
    /// An Android <see cref="Context"/> (e.g. the application context) used to obtain the system
    /// <see cref="NsdManager"/>. The application context is preferred so the manager outlives any
    /// single Activity.
    /// </param>
    public NsdServiceDiscovery(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _nsdManager = (NsdManager?)context.GetSystemService(Context.NsdService)
            ?? throw new InvalidOperationException("NSD service (Context.NsdService) is unavailable on this device.");
    }

    public Task AdvertiseAsync(string serviceName, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var serviceInfo = new NsdServiceInfo
        {
            ServiceName = serviceName,
            ServiceType = IServiceDiscovery.ServiceType, // "_castr._tcp" — must match the browse/iOS side verbatim
            Port = port,
        };

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new RegistrationListener(tcs);
        _registrationListener = listener;
        _nsdManager.RegisterService(serviceInfo, NsdProtocol.DnsSd, listener);

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }

    public async IAsyncEnumerable<DiscoveredPeer> BrowseAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var channel = Channel.CreateUnbounded<DiscoveredPeer>();
        var listener = new DiscoveryListener(_nsdManager, channel.Writer);
        _discoveryListener = listener;
        _nsdManager.DiscoverServices(IServiceDiscovery.ServiceType, NsdProtocol.DnsSd, listener);

        try
        {
            await foreach (var peer in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return peer;
        }
        finally
        {
            StopDiscovery(listener);
        }
    }

    private void StopDiscovery(DiscoveryListener listener)
    {
        try { _nsdManager.StopServiceDiscovery(listener); }
        catch (System.Exception) { /* already stopped / never started — safe to ignore */ }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        if (_registrationListener is { } reg)
        {
            try { _nsdManager.UnregisterService(reg); }
            catch (System.Exception) { /* best effort */ }
        }
        if (_discoveryListener is { } disc)
            StopDiscovery(disc);

        return ValueTask.CompletedTask;
    }

    // --- Listeners: bridge NsdManager's Java callbacks to Task / Channel. ---

    private sealed class RegistrationListener(TaskCompletionSource completion)
        : Java.Lang.Object, NsdManager.IRegistrationListener
    {
        public void OnServiceRegistered(NsdServiceInfo? serviceInfo) => completion.TrySetResult();

        public void OnRegistrationFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode) =>
            completion.TrySetException(new InvalidOperationException($"NSD RegisterService failed: {errorCode}."));

        public void OnServiceUnregistered(NsdServiceInfo? serviceInfo) { }

        public void OnUnregistrationFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode) { }
    }

    private sealed class DiscoveryListener(NsdManager nsdManager, ChannelWriter<DiscoveredPeer> writer)
        : Java.Lang.Object, NsdManager.IDiscoveryListener
    {
        public void OnServiceFound(NsdServiceInfo? serviceInfo)
        {
            if (serviceInfo is null)
                return;
            // A found service is name-only; resolve it to obtain host + port.
            nsdManager.ResolveService(serviceInfo, new ResolveListener(writer));
        }

        public void OnServiceLost(NsdServiceInfo? serviceInfo) { }

        public void OnDiscoveryStarted(string? serviceType) { }

        public void OnDiscoveryStopped(string? serviceType) => writer.TryComplete();

        public void OnStartDiscoveryFailed(string? serviceType, NsdFailure errorCode) =>
            writer.TryComplete(new InvalidOperationException($"NSD DiscoverServices failed to start: {errorCode}."));

        public void OnStopDiscoveryFailed(string? serviceType, NsdFailure errorCode) { }
    }

    private sealed class ResolveListener(ChannelWriter<DiscoveredPeer> writer)
        : Java.Lang.Object, NsdManager.IResolveListener
    {
        public void OnServiceResolved(NsdServiceInfo? serviceInfo)
        {
            if (serviceInfo is null)
                return;

            var host = serviceInfo.Host?.HostAddress;
            if (string.IsNullOrEmpty(host))
                return;

            var name = serviceInfo.ServiceName ?? host;
            writer.TryWrite(new DiscoveredPeer(name, new Endpoint(host, serviceInfo.Port)));
        }

        public void OnResolveFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode)
        {
            // NsdManager historically serializes resolves and fails concurrent ones with FailureAlreadyActive.
            // A failed resolve just means this peer is skipped; discovery of others continues.
        }
    }
}
