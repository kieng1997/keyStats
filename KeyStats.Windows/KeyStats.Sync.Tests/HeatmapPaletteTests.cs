using System;
using System.Text.Json;
using System.Windows.Media;
using KeyStats.Helpers;
using KeyStats.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

[TestClass]
public sealed class HeatmapPaletteTests
{
    [TestMethod]
    public void CustomPeakColorsRemainReadableInBothThemes()
    {
        foreach (var dark in new[] { false, true })
        {
            foreach (var peak in new[] { Colors.White, Colors.Black })
            {
                var fill = HeatmapPalette.ColorFor(AppSettings.HeatmapColorSchemeCustom,
                    Colors.Gray, peak, 1, dark);
                Assert.AreEqual(peak, fill);
                Assert.AreEqual(peak == Colors.White ? Colors.Black : Colors.White,
                    HeatmapPalette.LegendColor(fill));
            }
        }
    }

    [TestMethod]
    public void PresetAndCustomGradientsMaintainLegibleContrast()
    {
        var schemes = new[]
        {
            AppSettings.HeatmapColorSchemeDefault, AppSettings.HeatmapColorSchemeWarm,
            AppSettings.HeatmapColorSchemeSpectrum, AppSettings.HeatmapColorSchemeGreen,
            AppSettings.HeatmapColorSchemeTeal, AppSettings.HeatmapColorSchemePurple,
            AppSettings.HeatmapColorSchemeRose, AppSettings.HeatmapColorSchemeCustom
        };
        foreach (var dark in new[] { false, true })
        foreach (var scheme in schemes)
        for (var step = 0; step <= 10; step++)
        {
            var fill = HeatmapPalette.ColorFor(scheme, Colors.White, Colors.Black, step / 10.0, dark);
            var foreground = HeatmapPalette.LegendColor(fill);
            var backgroundLuminance = Luminance(fill);
            var foregroundLuminance = Luminance(foreground);
            var contrast = (Math.Max(backgroundLuminance, foregroundLuminance) + 0.05)
                / (Math.Min(backgroundLuminance, foregroundLuminance) + 0.05);
            Assert.IsTrue(contrast >= 4.5, $"{scheme}, dark={dark}, step={step}: {contrast}");
        }
    }

    [TestMethod]
    public void HeatmapColorSettings_DefaultWhenAbsentAndRoundTripWhenSet()
    {
        const string legacyJson = @"{
            ""notificationsEnabled"": true,
            ""keyHistorySelectedRangeIndex"": 0
        }";

        var legacy = JsonSerializer.Deserialize<AppSettings>(legacyJson);

        Assert.IsNotNull(legacy);
        Assert.AreEqual("default", legacy!.HeatmapColorScheme);
        Assert.AreEqual("#FFE082", legacy.HeatmapCustomLowColor);
        Assert.AreEqual("#C62828", legacy.HeatmapCustomHighColor);

        var saved = new AppSettings
        {
            HeatmapColorScheme = AppSettings.HeatmapColorSchemeCustom,
            HeatmapCustomLowColor = "#112233",
            HeatmapCustomHighColor = "#445566"
        };
        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved));

        Assert.IsNotNull(reloaded);
        Assert.AreEqual(AppSettings.HeatmapColorSchemeCustom, reloaded!.HeatmapColorScheme);
        Assert.AreEqual("#112233", reloaded.HeatmapCustomLowColor);
        Assert.AreEqual("#445566", reloaded.HeatmapCustomHighColor);
    }

    private static double Luminance(Color color)
    {
        double Linear(byte component)
        {
            var value = component / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
}
