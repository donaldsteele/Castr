using UIKit;

namespace Castr.Gui.iOS;

/// <summary>Native iOS entry point; hands control to <see cref="AppDelegate"/>.</summary>
public static class Application
{
    private static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
