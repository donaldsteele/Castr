using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Castr.Gui.Android;

/// <summary>
/// The Android process-wide <c>Application</c> entry point (distinct from any Activity — this is created
/// once per process, before <see cref="MainActivity"/>). Avalonia.Android 12.1.0 puts the AppBuilder/lifetime
/// bootstrap here via <see cref="AvaloniaAndroidApplication{TApp}"/>, generic over this head's Avalonia
/// <see cref="App"/>; <see cref="MainActivity"/> just attaches to the lifetime this creates. The
/// <c>(IntPtr, JniHandleOwnership)</c> constructor is required by the Android runtime, which activates
/// Application/Activity subclasses via JNI rather than a normal managed `new`.
/// </summary>
[Application]
public class MainApplication : AvaloniaAndroidApplication<App>
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder)
            .WithInterFont();
}
