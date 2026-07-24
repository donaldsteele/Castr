using CommunityToolkit.Mvvm.ComponentModel;
using Castr.Core.Manifest;
using Castr.Core.Trust;
using Castr.Gui.Services;
using Castr.Gui.Trust;

namespace Castr.Gui.ViewModels;

/// <summary>
/// Root view-model: hosts the Send and Receive flows. The primary constructor takes fully-composed flow
/// view-models (the desktop head wires real UDP + a persisted identity + a dialog trust prompt). The
/// parameterless constructor builds a self-contained in-memory composition for the XAML designer and for the
/// "window launches" headless smoke test — so nothing here ever touches a real socket unless the desktop
/// head asked for it.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    public MainViewModel(SendViewModel send, ReceiveViewModel receive)
    {
        Send = send;
        Receive = receive;
    }

    public MainViewModel()
        : this(BuildDesignSend(), BuildDesignReceive())
    {
    }

    public SendViewModel Send { get; }

    public ReceiveViewModel Receive { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    public string Title => "Castr — LAN multicast file transfer";

    private static SendViewModel BuildDesignSend()
    {
        var signingKey = ManifestSigner.CreateSigningKey();
        return new SendViewModel(new InMemoryTransportFactory(), signingKey);
    }

    private static ReceiveViewModel BuildDesignReceive() =>
        new(new InMemoryTransportFactory(), new InMemoryTrustStore(), new AutoTrustPrompt(false));
}
