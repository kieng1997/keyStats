using System;
using System.Collections.Generic;
using System.Windows.Media;
// External-boundary stand-ins for the isolated StatsPopupViewModel test. These replace the pieces that
// cannot run outside the real application: the StatsManager singleton (reads and writes the user's
// %LOCALAPPDATA% data), AppIconHelper (file/registry/process IO), the composition root and the
// localization resource. The view model, its base class, the refresher and the models are compiled from
// the real production sources. Nothing here reimplements view-model behavior.
namespace KeyStats.Services
{
    public sealed class StatsManager
    {
        public enum StatsUpdateKind { Full, MouseDistanceOnly }
        public enum HistoryRange { Today, Yesterday, ThreeDays, Week, Month, All }
        public enum HistoryMetric { KeyPresses, Clicks, MouseDistance, ScrollDistance }
        public enum MouseTotalsRange { Week, Month, Year, All }

        public sealed class HistoryTrendSeries
        {
            public HistoryTrendSeries(
                List<(DateTime Date, double Value)> display,
                List<(DateTime Date, double Value)>? local)
            {
                Display = display;
                Local = local;
            }

            public List<(DateTime Date, double Value)> Display { get; }
            public List<(DateTime Date, double Value)>? Local { get; }
        }

        private static StatsManager? _instance;
        public static StatsManager Instance => _instance ??= new StatsManager();

        /// <summary>Statistics handed to the popup; replaced by the test before each run.</summary>
        public static DailyStatsStatsSource Source { get; set; } = new();

        public event Action<StatsUpdateKind>? StatsChanged;

        public long HistoryRevision => 0;

        public Models.DailyStats GetCurrentStatsCopy() => Source.Today;

        public (double PeakKPS, double PeakCPS) GetAllTimePeakRates() => Source.Peaks;

        public HistoryTrendSeries GetHistoryTrendSeries(HistoryRange range, HistoryMetric metric) =>
            Source.History;

        public (
            int LeftClicks,
            int MiddleClicks,
            int RightClicks,
            int SideBackClicks,
            int SideForwardClicks,
            double MouseDistance,
            double ScrollDistance) GetHistoryInputTotals(MouseTotalsRange range) => Source.MouseTotals;

        public List<(string Key, int Count)> GetKeyPressBreakdownSorted() => Source.KeyBreakdown;

        public List<Models.AppStats> GetAppStatsSorted(int limit = 5)
        {
            var apps = Source.Apps;
            return limit >= apps.Count ? new List<Models.AppStats>(apps) : apps.GetRange(0, limit);
        }

        public string FormatNumber(int number)
        {
            if (number >= 999_950) return $"{number / 1_000_000.0:F1}M";
            if (number >= 1_000) return $"{number / 1_000.0:F1}k";
            return number.ToString("N0");
        }

        public string FormatMouseDistance(double distance) => $"{distance:F0} px";

        public string FormatCalibratedDistance(double distance) => $"{distance:F1} cm";

        public string FormatHistoryValue(HistoryMetric metric, double value) =>
            metric is HistoryMetric.KeyPresses or HistoryMetric.Clicks
                ? FormatNumber((int)value)
                : value.ToString("N0");

        public void FlushPendingSave() { }

        /// <summary>Raises the production notification so the view model's own handler runs.</summary>
        public void RaiseStatsChanged(StatsUpdateKind kind) => StatsChanged?.Invoke(kind);
    }

    public sealed class DailyStatsStatsSource
    {
        public Models.DailyStats Today { get; set; } = new Models.DailyStats();
        public (double PeakKPS, double PeakCPS) Peaks { get; set; }
        public StatsManager.HistoryTrendSeries History { get; set; } =
            new(new List<(DateTime, double)>(), null);
        public (
            int LeftClicks,
            int MiddleClicks,
            int RightClicks,
            int SideBackClicks,
            int SideForwardClicks,
            double MouseDistance,
            double ScrollDistance) MouseTotals { get; set; }
        public List<(string Key, int Count)> KeyBreakdown { get; set; } = new();
        public List<Models.AppStats> Apps { get; set; } = new();
    }

    /// <summary>
    /// Stands in for the icon lookup. The test decides how long a lookup takes and can block it, so the
    /// UI thread's responsiveness is observable without touching the file system or the registry.
    /// </summary>
    public static class AppIconStub
    {
        public static readonly System.Threading.ManualResetEventSlim FirstLookupStarted = new(false);
        public static readonly System.Threading.ManualResetEventSlim Release = new(false);
        public static int LookupCount;

        /// <summary>App names whose lookup returns null, exercising the no-icon path.</summary>
        public static System.Collections.Generic.HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>When false the first lookup returns immediately, so the responsiveness check is moot.</summary>
        public static bool BlockFirstLookup = true;

        public static void Reset()
        {
            FirstLookupStarted.Reset();
            Release.Reset();
            LookupCount = 0;
            Missing.Clear();
            BlockFirstLookup = true;
        }

        public static ImageSource? GetAppIcon(string processName, string? displayName)
        {
            var call = System.Threading.Interlocked.Increment(ref LookupCount);
            if (call == 1 && BlockFirstLookup)
            {
                FirstLookupStarted.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }

            if (Missing.Contains(processName)) return null;

            // A frozen 1x1 image, the same shape AppIconHelper produces.
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
            bitmap.Freeze();
            return bitmap;
        }
    }
}

namespace KeyStats.Helpers
{
    /// <summary>Forwards to the stub so the production view model's call site stays untouched.</summary>
    public static class AppIconHelper
    {
        public static ImageSource? GetAppIcon(string processName, string? displayName) =>
            Services.AppIconStub.GetAppIcon(processName, displayName);
    }
}

namespace KeyStats.Properties
{
    /// <summary>Localization resource stand-in; values match Strings.resx.</summary>
    public static class Strings
    {
        public static string Stats_HistoricalPeakFormat => "{0} (all-time high: {1})";
        public static string History_TotalFormat => "Total: {0}";
    }
}

namespace KeyStats.Services
{
    public sealed class InputMonitorService
    {
        private static InputMonitorService? _instance;
        public static InputMonitorService Instance => _instance ??= new InputMonitorService();
        public void StopMonitoring() { }
    }
}

namespace KeyStats
{
    /// <summary>Composition-root stand-in.</summary>
    internal sealed class App
    {
        public static App? CurrentApp => null;
        public void TrackClick(string elementName) { }
        public void TrackClick(string elementName, Dictionary<string, object?>? extra) { }
        public void TrackPageView(string page) { }
    }
}

namespace KeyStats.Views
{
    // The view model imports this namespace for the window types; no window is used by the test.
    internal sealed class Unused { }
}
