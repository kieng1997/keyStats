using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using KeyStats.Models;
using KeyStats.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

[TestClass]
public sealed class ScrollDirectionAndRevolutionsTests
{
    [TestMethod]
    public void ParseScrollData_ClassifiesVerticalDirectionsAndRoutesHorizontalToDown()
    {
        var up = InputMonitorService.ParseScrollData(MakeWheelMouseData(120), isVerticalWheel: true);
        Assert.AreEqual(1.0, up.Distance, 1e-9);
        Assert.IsTrue(up.IsScrollUp);

        var down = InputMonitorService.ParseScrollData(MakeWheelMouseData(-240), isVerticalWheel: true);
        Assert.AreEqual(2.0, down.Distance, 1e-9);
        Assert.IsFalse(down.IsScrollUp);

        var horizontalPositive = InputMonitorService.ParseScrollData(MakeWheelMouseData(120), isVerticalWheel: false);
        Assert.AreEqual(1.0, horizontalPositive.Distance, 1e-9);
        Assert.IsFalse(horizontalPositive.IsScrollUp);

        var horizontalNegative = InputMonitorService.ParseScrollData(MakeWheelMouseData(-120), isVerticalWheel: false);
        Assert.AreEqual(1.0, horizontalNegative.Distance, 1e-9);
        Assert.IsFalse(horizontalNegative.IsScrollUp);

        var zero = InputMonitorService.ParseScrollData(MakeWheelMouseData(0), isVerticalWheel: true);
        Assert.AreEqual(0.0, zero.Distance, 1e-9);
        Assert.IsFalse(zero.IsScrollUp);
    }

    [TestMethod]
    public void DailyStats_LegacyScrollDistanceWithoutDirection_AttributesRemainderToDown()
    {
        const string legacyJson = @"{""date"":""2026-05-01T00:00:00"",""scrollDistance"":48}";
        var legacy = JsonSerializer.Deserialize<DailyStats>(legacyJson);

        Assert.IsNotNull(legacy);
        Assert.AreEqual(0.0, legacy!.EffectiveScrollUpDistance, 1e-9);
        Assert.AreEqual(48.0, legacy.EffectiveScrollDownDistance, 1e-9);
        Assert.AreEqual(48.0, legacy.EffectiveTotalScrollDistance, 1e-9);
        Assert.AreEqual("0 px", legacy.FormattedScrollUpDistance);
        Assert.AreEqual("48 px", legacy.FormattedScrollDownDistance);

        legacy.NormalizeScrollDirections();
        Assert.AreEqual(0.0, legacy.ScrollUpDistance, 1e-9);
        Assert.AreEqual(48.0, legacy.ScrollDownDistance, 1e-9);
        Assert.AreEqual(48.0, legacy.ScrollDistance, 1e-9);

        var partial = new DailyStats
        {
            ScrollDistance = 100,
            ScrollUpDistance = 30,
            ScrollDownDistance = 20
        };
        Assert.AreEqual(30.0, partial.EffectiveScrollUpDistance, 1e-9);
        Assert.AreEqual(70.0, partial.EffectiveScrollDownDistance, 1e-9);
        Assert.AreEqual(100.0, partial.EffectiveTotalScrollDistance, 1e-9);
    }

    [TestMethod]
    public void StatsManager_OnMouseScrolled_AccumulatesUpDownAndPreservesLegacyTotalInDown()
    {
        using var directory = new TestDirectory();
        var manager = CreatePersistenceManager(directory.Path, new DailyStats(DateTime.Today)
        {
            ScrollDistance = 10
        });
        try
        {
            InvokePrivate(manager, "OnMouseScrolled", 5.0, true, "browser", "Browser");
            InvokePrivate(manager, "OnMouseScrolled", 3.0, false, "browser", "Browser");

            Assert.AreEqual(5.0, manager.CurrentStats.ScrollUpDistance, 1e-9);
            Assert.AreEqual(13.0, manager.CurrentStats.ScrollDownDistance, 1e-9);
            Assert.AreEqual(18.0, manager.CurrentStats.ScrollDistance, 1e-9);
            Assert.AreEqual(8.0, manager.CurrentStats.AppStats["browser"].ScrollDistance, 1e-9);
        }
        finally
        {
            DisposeTimers(manager);
        }
    }

    [TestMethod]
    public void StatsManager_LoadAndMerge_AttributeLegacyScrollToDownAndSumDirections()
    {
        using var directory = new TestDirectory();
        var historyPath = Path.Combine(directory.Path, "history.json");
        var statsPath = Path.Combine(directory.Path, "daily_stats.json");
        File.WriteAllText(historyPath, @"{""2026-05-01"":{""date"":""2026-05-01T00:00:00"",""scrollDistance"":67.25}}");
        File.WriteAllText(statsPath, @"{""date"":""2026-05-02T00:00:00"",""scrollDistance"":25}");

        var manager = CreatePersistenceManager(directory.Path);
        var loadedHistory = (Dictionary<string, DailyStats>)InvokePrivate(manager, "LoadHistory")!;
        var loadedStats = (DailyStats?)InvokePrivate(manager, "LoadStats");

        Assert.AreEqual(0.0, loadedHistory["2026-05-01"].ScrollUpDistance, 1e-9);
        Assert.AreEqual(67.25, loadedHistory["2026-05-01"].ScrollDownDistance, 1e-9);
        Assert.AreEqual(67.25, loadedHistory["2026-05-01"].ScrollDistance, 1e-9);

        Assert.IsNotNull(loadedStats);
        Assert.AreEqual(0.0, loadedStats!.ScrollUpDistance, 1e-9);
        Assert.AreEqual(25.0, loadedStats.ScrollDownDistance, 1e-9);
        Assert.AreEqual(25.0, loadedStats.ScrollDistance, 1e-9);

        var existingLegacy = new DailyStats(new DateTime(2026, 5, 1)) { ScrollDistance = 20 };
        var incomingDirectional = new DailyStats(new DateTime(2026, 5, 1))
        {
            ScrollUpDistance = 12,
            ScrollDownDistance = 8,
            ScrollDistance = 20
        };

        var merge = typeof(StatsManager).GetMethod("MergeDailyStats", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(merge);
        var merged = (DailyStats)merge!.Invoke(null, new object[] { existingLegacy, incomingDirectional })!;

        Assert.AreEqual(12.0, merged.ScrollUpDistance, 1e-9);
        Assert.AreEqual(28.0, merged.ScrollDownDistance, 1e-9);
        Assert.AreEqual(40.0, merged.ScrollDistance, 1e-9);
    }

    [TestMethod]
    public void ScrollRevolutions_UsesFloorDivisionWithConfigurableSettingAndDefaultFallback()
    {
        var manager = CreateManagerWithToday(new DailyStats(DateTime.Today));

        Assert.AreEqual(24, manager.GetMouseScrollStepsPerRevolution());
        Assert.AreEqual(0L, manager.CalculateScrollRevolutions(0));
        Assert.AreEqual(0L, manager.CalculateScrollRevolutions(23.9));
        Assert.AreEqual(1L, manager.CalculateScrollRevolutions(24));
        Assert.AreEqual(1L, manager.CalculateScrollRevolutions(47.5));
        Assert.AreEqual(2L, manager.CalculateScrollRevolutions(48));

        // 72 fractional steps of 1/3 should sum cleanly to 24 => 1 revolution despite float inaccuracy.
        var fractionalTotal = 0.0;
        for (var i = 0; i < 72; i++)
        {
            fractionalTotal += 1.0 / 3.0;
        }
        Assert.AreEqual(1L, manager.CalculateScrollRevolutions(fractionalTotal));

        manager.Settings.MouseScrollStepsPerRevolution = 12;
        Assert.AreEqual(12, manager.GetMouseScrollStepsPerRevolution());
        Assert.AreEqual(2L, manager.CalculateScrollRevolutions(24));
        Assert.AreEqual(2L, manager.CalculateScrollRevolutions(35.9));
        Assert.AreEqual(3L, manager.CalculateScrollRevolutions(36));

        // Invalid values must safely fall back to 24.
        manager.Settings.MouseScrollStepsPerRevolution = 0;
        Assert.AreEqual(24, manager.GetMouseScrollStepsPerRevolution());
        Assert.AreEqual(2L, manager.CalculateScrollRevolutions(48));

        manager.Settings.MouseScrollStepsPerRevolution = -5;
        Assert.AreEqual(24, manager.GetMouseScrollStepsPerRevolution());
        Assert.AreEqual(2L, manager.CalculateScrollRevolutions(48));
    }

    [TestMethod]
    public void ScrollStepsPerRevolution_PersistsThroughSettingsRoundTripAndFormatsWithDistance()
    {
        const string legacyJson = @"{""mouseScrollPpi"": 92}";
        var legacy = JsonSerializer.Deserialize<AppSettings>(legacyJson);
        Assert.IsNotNull(legacy);
        Assert.AreEqual(24, legacy!.MouseScrollStepsPerRevolution);

        using var directory = new TestDirectory();
        var manager = CreatePersistenceManager(directory.Path);
        try
        {
            manager.UpdateMouseScrollStepsPerRevolution(30);
            Assert.AreEqual(30, manager.Settings.MouseScrollStepsPerRevolution);

            // Non-positive updates are ignored.
            manager.UpdateMouseScrollStepsPerRevolution(0);
            manager.UpdateMouseScrollStepsPerRevolution(-10);
            Assert.AreEqual(30, manager.Settings.MouseScrollStepsPerRevolution);

            InvokePrivate(manager, "FlushSettings");
            var reloaded = (AppSettings)InvokePrivate(manager, "LoadSettings")!;
            Assert.AreEqual(30, reloaded.MouseScrollStepsPerRevolution);

            var prevCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo("zh-Hans");
                Assert.AreEqual("滚轮上滚", KeyStats.Properties.Strings.Stats_ScrollUpDistance);
                Assert.AreEqual("滚轮下滚", KeyStats.Properties.Strings.Stats_ScrollDownDistance);
                Assert.AreEqual("滚轮总滚动", KeyStats.Properties.Strings.Stats_ScrollTotalDistance);
                Assert.AreEqual("约滚圈数", KeyStats.Properties.Strings.Stats_ScrollRevolutions);
                Assert.AreEqual("3 圈", manager.FormatScrollRevolutions(90));
            }
            finally
            {
                CultureInfo.CurrentUICulture = prevCulture;
            }
        }
        finally
        {
            DisposeTimers(manager);
        }
    }

    private static uint MakeWheelMouseData(short delta) => unchecked((uint)(((int)delta) << 16));

    private static StatsManager CreateManagerWithToday(DailyStats today)
    {
        var manager = (StatsManager)FormatterServices.GetUninitializedObject(typeof(StatsManager));
        SetField(manager, "_lock", new object());
        SetField(manager, "_midnightTimerLock", new object());
        SetField(manager, "_recentKeyTimestamps", new Queue<DateTime>());
        SetField(manager, "_recentClickTimestamps", new Queue<DateTime>());
        SetField(manager, "_saveInterval", 60_000.0);
        SetField(manager, "_statsUpdateDebounceInterval", 60_000.0);
        SetField(manager, "_settingsSaveInterval", 60_000.0);
        SetField(manager, "<Settings>k__BackingField", new AppSettings());
        SetField(manager, "<History>k__BackingField", new Dictionary<string, DailyStats>());
        SetField(manager, "<CurrentStats>k__BackingField", today);
        return manager;
    }

    private static StatsManager CreatePersistenceManager(string directoryPath, DailyStats? today = null)
    {
        var manager = CreateManagerWithToday(today ?? new DailyStats(DateTime.Today));
        SetField(manager, "_statsFilePath", Path.Combine(directoryPath, "daily_stats.json"));
        SetField(manager, "_historyFilePath", Path.Combine(directoryPath, "history.json"));
        SetField(manager, "_settingsFilePath", Path.Combine(directoryPath, "settings.json"));
        return manager;
    }

    private static void DisposeTimers(StatsManager manager)
    {
        SetField(manager, "_isDisposed", true);
        foreach (var name in new[] { "_saveTimer", "_statsUpdateTimer", "_mouseMoveUpdateTimer", "_settingsSaveTimer", "_midnightTimer" })
        {
            var field = typeof(StatsManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(manager) is System.Timers.Timer timer)
            {
                timer.Dispose();
            }
        }
    }

    private static object? InvokePrivate(object target, string name, params object[] args)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "Missing test method: " + name);
        return method!.Invoke(target, args);
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, "Missing test field: " + name);
        field!.SetValue(target, value);
    }
}
