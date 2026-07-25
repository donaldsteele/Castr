using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Castr.Core.Chunking;
using Castr.Core.Discovery;
using Castr.Core.Swarm;
using Castr.Core.Transport;
using Castr.Core.Transport.Tcp;
using Castr.Core.Trust;
using Castr.Gui.ViewModels;
using Castr.Gui.Views;

namespace Castr.Gui.Android;

/// <summary>
/// The Android application head. Deliberately does NOT reuse <see cref="Castr.Gui.App"/>: that composition is
/// desktop-shaped (an <c>IClassicDesktopStyleApplicationLifetime</c> main window wired onto real UDP multicast),
/// and mobile is architecturally a unicast-swarm CLIENT, not a multicast participant (see ADR-0002 / the
/// "why mobile is architecturally different" design note). This head instead composes the mobile
/// <see cref="SwarmReceiveViewModel"/> onto an <c>ISingleViewApplicationLifetime</c> main view, backed by the
/// native <see cref="NsdServiceDiscovery"/> and real TCP.
/// </summary>
public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            singleView.MainView = ComposeMainView();

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Real Android composition: a file-backed trust store and destination directory under the app's private
    /// storage, native <see cref="NsdServiceDiscovery"/> (NsdManager) for peer discovery, real TCP for the pull,
    /// and the in-app (single-view) Trust-On-First-Use prompt the <see cref="SwarmReceiveViewModel"/> owns.
    /// </summary>
    private static Control ComposeMainView()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Castr");
        Directory.CreateDirectory(dataDir);

        var destination = Path.Combine(dataDir, "incoming");
        Directory.CreateDirectory(destination);

        ITrustStore trustStore = new FileTrustStore(Path.Combine(dataDir, "trusted-senders.json"));
        var receiverId = RandomNumberGenerator.GetBytes(16);

        // The application context outlives any single Activity, so NsdManager is obtained from it.
        IServiceDiscovery discovery = new NsdServiceDiscovery(global::Android.App.Application.Context);
        IStreamClient streamClient = new TcpStreamClient();

        var options = new SwarmPullSessionOptions(destination, UnknownSenderPolicy.Prompt, IsInteractive: true);
        Func<string, long, IFileSink> sinkFactory = (path, length) => new FileSystemFileSink(path, length);

        var vm = new SwarmReceiveViewModel(discovery, streamClient, trustStore, receiverId, options, sinkFactory);
        return new SwarmReceiveView { DataContext = vm };
    }
}
