using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace Castr.Gui.Android;

/// <summary>
/// The single Android launcher activity. Hosts Avalonia's single-view lifetime via
/// <see cref="AvaloniaMainActivity{TApp}"/>, pointed at this head's <see cref="App"/>. Everything above the
/// platform bootstrap (the swarm receive view/view-model) lives in the shared Castr.Gui library.
/// </summary>
[Activity(
    Label = "Castr",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder)
            .WithInterFont();
}
