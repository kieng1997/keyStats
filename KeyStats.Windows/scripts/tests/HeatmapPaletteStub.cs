using System;
using System.Windows;
using System.Windows.Media;
// Isolated-test stand-in for KeyStats.Helpers.HeatmapPalette. The production palette reads the user's
// settings through StatsManager.Instance, which would drag the whole StatsManager (and its
// %LOCALAPPDATA% persistence) into the isolated rendering harness. This stub keeps the same public
// surface the heatmap control uses and a neutral-grey → accent ramp; the smoke test only judges the
// rendered structure and legend readability, not the user's chosen colour scheme.
namespace KeyStats.Helpers
{
    public static class HeatmapPalette
    {
        public static event Action? SchemeChanged;

        public static void NotifySchemeChanged()
        {
            SchemeChanged?.Invoke();
        }

        public static Color NeutralColor(bool isDarkMode)
        {
            var surface = ResolveColor("SurfaceBrush", isDarkMode ? Color.FromRgb(32, 32, 32) : Color.FromRgb(250, 250, 250));
            var divider = ResolveColor("DividerBrush", isDarkMode ? Color.FromRgb(61, 61, 61) : Color.FromRgb(229, 229, 229));
            return Blend(surface, divider, isDarkMode ? 0.22 : 0.10);
        }

        public static Color ColorFor(double intensity, bool isDarkMode)
        {
            var neutral = NeutralColor(isDarkMode);
            var accent = ResolveColor("AccentBrush", isDarkMode ? Color.FromRgb(0, 120, 212) : Color.FromRgb(0, 103, 192));
            var low = Blend(neutral, accent, isDarkMode ? 0.34 : 0.24);
            var high = Blend(accent, isDarkMode ? Colors.White : Colors.Black, isDarkMode ? 0.18 : 0.10);
            return Blend(low, high, intensity);
        }

        private static Color ResolveColor(string key, Color fallback)
        {
            if (Application.Current?.Resources[key] is SolidColorBrush brush)
            {
                return brush.Color;
            }
            return Application.Current?.Resources[key] is Color color ? color : fallback;
        }

        private static Color Blend(Color from, Color to, double t)
        {
            var progress = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (byte)Math.Round(from.A + (to.A - from.A) * progress),
                (byte)Math.Round(from.R + (to.R - from.R) * progress),
                (byte)Math.Round(from.G + (to.G - from.G) * progress),
                (byte)Math.Round(from.B + (to.B - from.B) * progress));
        }
    }
}
