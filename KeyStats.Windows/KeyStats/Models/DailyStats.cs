using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KeyStats.Models;

public class DailyStats
{
    private const double MetersPerPixel = AppSettings.DefaultMouseMetersPerPixel;

    [JsonPropertyName("date")]
    public DateTime Date { get; set; }

    [JsonPropertyName("keyPresses")]
    public int KeyPresses { get; set; }

    [JsonPropertyName("keyPressCounts")]
    public Dictionary<string, int> KeyPressCounts { get; set; } = new();

    [JsonPropertyName("leftClicks")]
    public int LeftClicks { get; set; }

    [JsonPropertyName("rightClicks")]
    public int RightClicks { get; set; }

    [JsonPropertyName("middleClicks")]
    public int MiddleClicks { get; set; }

    [JsonPropertyName("sideBackClicks")]
    public int SideBackClicks { get; set; }

    [JsonPropertyName("sideForwardClicks")]
    public int SideForwardClicks { get; set; }

    [JsonPropertyName("mouseDistance")]
    public double MouseDistance { get; set; }

    [JsonPropertyName("scrollDistance")]
    public double ScrollDistance { get; set; }

    [JsonPropertyName("scrollUpDistance")]
    public double ScrollUpDistance { get; set; }

    [JsonPropertyName("scrollDownDistance")]
    public double ScrollDownDistance { get; set; }

    [JsonPropertyName("peakKPS")]
    public double PeakKPS { get; set; }

    [JsonPropertyName("peakCPS")]
    public double PeakCPS { get; set; }

    [JsonPropertyName("appStats")]
    public Dictionary<string, AppStats> AppStats { get; set; } = new();

    [JsonIgnore]
    public int TotalClicks => LeftClicks + RightClicks + MiddleClicks + SideBackClicks + SideForwardClicks;

    public DailyStats()
    {
        Date = DateTime.Today;
    }

    public DailyStats(DateTime date)
    {
        Date = date.Date;
    }

    public string FormattedMouseDistance
    {
        get
        {
            var meters = MouseDistance * MetersPerPixel;
            if (meters >= 1000)
            {
                return $"{meters / 1000:F2} km";
            }
            else if (MouseDistance >= 1000)
            {
                return $"{meters:F1} m";
            }
            return $"{MouseDistance:F0} px";
        }
    }

    [JsonIgnore]
    public double EffectiveScrollUpDistance => SanitizeNonNegative(ScrollUpDistance);

    [JsonIgnore]
    public double EffectiveScrollDownDistance
    {
        get
        {
            var up = SanitizeNonNegative(ScrollUpDistance);
            var down = SanitizeNonNegative(ScrollDownDistance);
            var total = SanitizeNonNegative(ScrollDistance);
            var unaccounted = total - (up + down);
            return unaccounted > 0 ? down + unaccounted : down;
        }
    }

    [JsonIgnore]
    public double EffectiveTotalScrollDistance => EffectiveScrollUpDistance + EffectiveScrollDownDistance;

    public void NormalizeScrollDirections()
    {
        var up = EffectiveScrollUpDistance;
        var down = EffectiveScrollDownDistance;
        ScrollUpDistance = up;
        ScrollDownDistance = down;
        ScrollDistance = up + down;
    }

    public string FormattedScrollDistance => FormatScrollPixels(EffectiveTotalScrollDistance);

    public string FormattedScrollUpDistance => FormatScrollPixels(EffectiveScrollUpDistance);

    public string FormattedScrollDownDistance => FormatScrollPixels(EffectiveScrollDownDistance);

    public static string FormatScrollPixels(double distance)
    {
        var sanitized = SanitizeNonNegative(distance);
        if (sanitized >= 10000)
        {
            return $"{sanitized / 1000:F1} k";
        }
        return $"{sanitized:F0} px";
    }

    private static double SanitizeNonNegative(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            return 0;
        }
        return value;
    }
}
