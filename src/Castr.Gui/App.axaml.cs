using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Castr.Core.Trust;
using Castr.Gui.Services;
using Castr.Gui.Trust;
using Castr.Gui.ViewModels;
using Castr.Gui.Views;

namespace Castr.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = ComposeMainWindow(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Real desktop composition: a persisted signing identity, a file-backed trust store, real UDP multicast
    /// transports, and the dialog-based trust prompt — all under the user's application-data directory.
    /// </summary>
    private static MainWindow ComposeMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Castr");

        var signingKey = CastrIdentity.LoadOrCreateSigningKey(Path.Combine(dataDir, "identity.key"));
        ITrustStore trustStore = new FileTrustStore(Path.Combine(dataDir, "trusted-senders.json"));
        var transports = new UdpTransportFactory();
        var trustPrompt = new DialogTrustPrompt(() => desktop.MainWindow);

        var send = new SendViewModel(transports, signingKey);
        ISessionRegistry sessionRegistry = new FileSessionRegistry(Path.Combine(dataDir, "seen-sessions.json"));
        var receive = new ReceiveViewModel(transports, trustStore, trustPrompt, sessionRegistry: sessionRegistry);
        var main = new MainViewModel(send, receive);

        var window = new MainWindow { DataContext = main };

        // Storage pickers need a live TopLevel, so wire them from the window we just created.
        send.FilePicker = () => StoragePickers.PickFileAsync(window);
        receive.FolderPicker = () => StoragePickers.PickFolderAsync(window);

        return window;
    }
}
