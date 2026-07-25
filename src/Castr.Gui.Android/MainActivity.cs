using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace Castr.Gui.Android;

/// <summary>
/// The single Android launcher activity. Avalonia.Android 12.1.0's <see cref="AvaloniaMainActivity"/> is
/// non-generic; the AppBuilder/lifetime bootstrap (pointed at this head's <see cref="App"/>) now lives on
/// <see cref="MainApplication"/> instead (an <c>Android.App.Application</c> subclass, built and attached
/// before any Activity runs). Everything above the platform bootstrap (the swarm receive view/view-model)
/// lives in the shared Castr.Gui library.
/// </summary>
[Activity(
    Label = "Castr",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}
