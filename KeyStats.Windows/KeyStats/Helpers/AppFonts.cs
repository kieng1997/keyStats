using System.Windows;
using System.Windows.Media;

namespace KeyStats.Helpers;

public static class AppFonts
{
    private static readonly FontFamily Fallback = new("Segoe UI");

    // Defined once in App.xaml (AppFontFamily) so XAML text and custom-drawn text stay in sync.
    public static FontFamily Primary =>
        Application.Current?.TryFindResource("AppFontFamily") as FontFamily ?? Fallback;
}
