using System;
using Avalonia;

namespace Castr.Gui.Desktop;

/// <summary>
/// The desktop executable head. Owns nothing but the platform bootstrap: all views and view-models live in
/// the shared <c>Castr.Gui</c> library so the (M4) mobile heads can reuse them. See PLACEHOLDER history in
/// Castr.Gui for why this split waited until M2.
/// </summary>
internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant
    // code before AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Castr.Gui.App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
