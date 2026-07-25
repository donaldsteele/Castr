using Avalonia;
using Avalonia.iOS;
using Foundation;

namespace Castr.Gui.iOS;

/// <summary>
/// The iOS <c>UIApplicationDelegate</c>. Boots Avalonia with the Castr iOS <see cref="App"/> (a single-view
/// application — mobile has no desktop windows) and the Inter font used across the shared UI.
/// </summary>
[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder)
            .WithInterFont();
}
