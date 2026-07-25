using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Castr.Gui.Views;

/// <summary>
/// The mobile receive surface: a single scrollable view (mobile heads use a single-view application
/// lifetime, not desktop windows) bound to <see cref="ViewModels.MobileReceiveViewModel"/>. Lives in the
/// shared <c>Castr.Gui</c> library so both the iOS and Android heads render the identical UI.
/// </summary>
public partial class MobileReceiveView : UserControl
{
    public MobileReceiveView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
