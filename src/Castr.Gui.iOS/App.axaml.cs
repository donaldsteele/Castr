using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Castr.Core.Chunking;
using Castr.Core.Discovery;
using Castr.Core.Protocol;
using Castr.Core.Swarm;
using Castr.Core.Time;
using Castr.Core.Transport.Tcp;
using Castr.Core.Trust;
using Castr.Gui.ViewModels;
using Castr.Gui.Views;

namespace Castr.Gui.iOS;

/// <summary>
/// The Castr iOS Avalonia application. Unlike the desktop head (which uses a classic desktop window
/// lifetime), mobile uses the <see cref="ISingleViewApplicationLifetime"/> single-view lifetime and hosts the
/// shared <see cref="MobileReceiveView"/> bound to a <see cref="MobileReceiveViewModel"/>.
/// <para>
/// Composition is real and mobile-appropriate: peers are discovered via the native
/// <see cref="NetworkServiceDiscovery"/> (Apple Network.framework NWBrowser/NWListener), files are pulled over
/// unicast TCP by a <see cref="SwarmPullSession"/> writing into the app's sandboxed Documents directory, trust
/// is persisted in a <see cref="FileTrustStore"/> under the same sandbox, and an unknown sender surfaces the
/// in-app Trust-On-First-Use prompt (the view-model is its own <see cref="ITrustPrompt"/>).
/// </para>
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

    private static MobileReceiveView ComposeMainView()
    {
        // The iOS app sandbox: MyDocuments maps to the app's Documents directory, the correct place for both
        // received files and the persisted trust store on iOS.
        var documentsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dataDir = Path.Combine(documentsDir, "Castr");
        Directory.CreateDirectory(dataDir);

        ITrustStore trustStore = new FileTrustStore(Path.Combine(dataDir, "trusted-senders.json"));
        var receiverId = RandomNumberGenerator.GetBytes(16);

        IServiceDiscovery discovery = new NetworkServiceDiscovery();

        var viewModel = new MobileReceiveViewModel(
            discovery,
            trustPrompt => new SwarmPullSession(
                receiverId,
                trustStore,
                new TcpStreamClient(),
                SystemClock.Instance,
                new SwarmPullSessionOptions(
                    documentsDir, UnknownSenderPolicy.Prompt, IsInteractive: true,
                    SessionRegistry: new FileSessionRegistry(Path.Combine(dataDir, "seen-sessions.json"))),
                sinkFactory: (destination, length) => new FileSystemFileSink(destination, length),
                trustPrompt: trustPrompt));

        return new MobileReceiveView { DataContext = viewModel };
    }
}
