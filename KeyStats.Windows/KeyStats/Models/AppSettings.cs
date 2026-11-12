using System;
using System.Text.Json.Serialization;

namespace KeyStats.Models;

public class AppSettings
{
    public const double DefaultMouseMetersPerPixel = 0.00005;
    public const string FloatingStatsSingleRowLayoutMode = "singleRow";
    public const string FloatingStatsDoubleRowLayoutMode = "doubleRow";
    public const int FloatingStatsLayoutBaseFontSize = 11;
    public const int DefaultFloatingStatsFontSize = 12;
    public const int MinimumFloatingStatsFontSize = 9;
    public const int MaximumFloatingStatsFontSize = 22;
    public const string HeatmapColorSchemeDefault = "default";
    public const string HeatmapColorSchemeWarm = "warm";
    public const string HeatmapColorSchemeSpectrum = "spectrum";
    public const string HeatmapColorSchemeGreen = "green";
    public const string HeatmapColorSchemeTeal = "teal";
    public const string HeatmapColorSchemePurple = "purple";
    public const string HeatmapColorSchemeRose = "rose";
    public const string HeatmapColorSchemeCustom = "custom";
    private int _floatingStatsFontSize = DefaultFloatingStatsFontSize;

    [JsonPropertyName("notificationsEnabled")]
    public bool NotificationsEnabled { get; set; }

    [JsonPropertyName("keyPressNotifyThreshold")]
    public int KeyPressNotifyThreshold { get; set; } = 1000;

    [JsonPropertyName("clickNotifyThreshold")]
    public int ClickNotifyThreshold { get; set; } = 1000;

    [JsonPropertyName("launchAtStartup")]
    public bool LaunchAtStartup { get; set; }

    [JsonPropertyName("mouseMetersPerPixel")]
    public double MouseMetersPerPixel { get; set; } = DefaultMouseMetersPerPixel;

    [JsonPropertyName("mouseDistanceUnit")]
    public string MouseDistanceUnit { get; set; } = "auto"; // auto | px

    [JsonPropertyName("mouseScrollPpi")]
    public int MouseScrollPpi { get; set; } = 92;

    [JsonPropertyName("keyHistorySelectedRangeIndex")]
    public int KeyHistorySelectedRangeIndex { get; set; } = 1;

    [JsonPropertyName("mainWindowLeft")]
    public double? MainWindowLeft { get; set; }

    [JsonPropertyName("mainWindowTop")]
    public double? MainWindowTop { get; set; }

    [JsonPropertyName("mainWindowWidth")]
    public double? MainWindowWidth { get; set; }

    [JsonPropertyName("mainWindowHeight")]
    public double? MainWindowHeight { get; set; }

    [JsonPropertyName("keyboardHeatmapWindowWidth")]
    public double? KeyboardHeatmapWindowWidth { get; set; }

    [JsonPropertyName("keyboardHeatmapWindowHeight")]
    public double? KeyboardHeatmapWindowHeight { get; set; }

    [JsonPropertyName("keyHistoryWindowWidth")]
    public double? KeyHistoryWindowWidth { get; set; }

    [JsonPropertyName("keyHistoryWindowHeight")]
    public double? KeyHistoryWindowHeight { get; set; }

    [JsonPropertyName("appStatsWindowWidth")]
    public double? AppStatsWindowWidth { get; set; }

    [JsonPropertyName("appStatsWindowHeight")]
    public double? AppStatsWindowHeight { get; set; }

    [JsonPropertyName("floatingStatsEnabled")]
    public bool FloatingStatsEnabled { get; set; }

    [JsonPropertyName("floatingStatsPrimaryMetric")]
    public string FloatingStatsPrimaryMetric { get; set; } = "keyPresses";

    [JsonPropertyName("floatingStatsSecondaryMetric")]
    public string FloatingStatsSecondaryMetric { get; set; } = "totalClicks";

    [JsonPropertyName("floatingStatsLayoutMode")]
    public string FloatingStatsLayoutMode { get; set; } = FloatingStatsDoubleRowLayoutMode;

    [JsonPropertyName("floatingStatsFontSize")]
    public int FloatingStatsFontSize
    {
        get => _floatingStatsFontSize;
        set => _floatingStatsFontSize = Math.Max(
            MinimumFloatingStatsFontSize,
            Math.Min(MaximumFloatingStatsFontSize, value));
    }

    [JsonPropertyName("floatingStatsLeft")]
    public double? FloatingStatsLeft { get; set; }

    [JsonPropertyName("floatingStatsTop")]
    public double? FloatingStatsTop { get; set; }

    [JsonPropertyName("floatingStatsMonitorDeviceName")]
    public string? FloatingStatsMonitorDeviceName { get; set; }

    [JsonPropertyName("floatingStatsTopmost")]
    public bool FloatingStatsTopmost { get; set; } = true;

    [JsonPropertyName("floatingStatsPositionLocked")]
    public bool FloatingStatsPositionLocked { get; set; }

    [JsonPropertyName("heatmapColorScheme")]
    public string HeatmapColorScheme { get; set; } = HeatmapColorSchemeDefault;  // "default" | "warm" | "spectrum" | "green" | "teal" | "purple" | "rose" | "custom"

    [JsonPropertyName("heatmapCustomLowColor")]
    public string HeatmapCustomLowColor { get; set; } = "#FFE082";

    [JsonPropertyName("heatmapCustomHighColor")]
    public string HeatmapCustomHighColor { get; set; } = "#C62828";

    [JsonPropertyName("languagePreference")]
    public string LanguagePreference { get; set; } = "system";  // "system" | "zh-Hans" | "zh-Hant" | "en"

    /// <summary>
    /// Absolute local path of the desktop pet model folder. Null means no model chosen yet.
    /// </summary>
    [JsonPropertyName("desktopPetModelPath")]
    public string? DesktopPetModelPath { get; set; }

    /// <summary>
    /// Id of the bundled desktop pet preset ("ds-standard", "nailin-keyboard"). Null means the
    /// legacy custom path (or nothing) applies; preset ids are only ever matched against fixed
    /// literals, never turned into paths from arbitrary settings strings.
    /// </summary>
    [JsonPropertyName("desktopPetPresetId")]
    public string? DesktopPetPresetId { get; set; }

    /// <summary>
    /// Detached copy used when persisting settings: the fields are value types or immutable
    /// strings, so the clone serializes to exactly the same JSON as the live instance.
    /// </summary>
    internal AppSettings Snapshot() => (AppSettings)MemberwiseClone();
}
