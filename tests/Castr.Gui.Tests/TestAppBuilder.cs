using Avalonia;
using Avalonia.Headless;
using Castr.Gui.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Castr.Gui.Tests;

/// <summary>
/// Boots the real <see cref="Castr.Gui.App"/> (Fluent theme + ViewLocator resources) on Avalonia's headless
/// platform, so tests render and drive actual windows/controls with no display attached.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Castr.Gui.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
