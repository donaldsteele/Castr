using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Castr.Gui.Views;

/// <summary>
/// View for <see cref="Castr.Gui.ViewModels.SwarmReceiveViewModel"/> — the mobile unicast-swarm receive surface.
/// A plain <see cref="UserControl"/> (not a <see cref="Window"/>) so it hosts inside a single-view mobile head's
/// <c>MainView</c>. Platform-neutral: the Android and (future) iOS heads both host this same control.
/// </summary>
public partial class SwarmReceiveView : UserControl
{
    public SwarmReceiveView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
