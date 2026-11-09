using System;
using System.Text.Json;
using KeyStats.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

[TestClass]
public sealed class AppSettingsLegacyAnalyticsTests
{
    [TestMethod]
    public void LegacyAnalyticsFields_AreIgnoredOnLoadAndDroppedOnSave()
    {
        const string legacyJson = @"{
            ""notificationsEnabled"": true,
            ""analyticsEnabled"": false,
            ""analyticsApiKey"": ""phc_legacy"",
            ""analyticsHost"": ""https://app.posthog.com"",
            ""analyticsDistinctId"": ""abc123"",
            ""analyticsFirstOpenUtc"": ""2026-05-01T02:03:04Z"",
            ""analyticsInstallTracked"": true,
            ""languagePreference"": ""zh-Hans"",
            ""floatingStatsFontSize"": 18
        }";

        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson);

        Assert.IsNotNull(settings);
        Assert.IsTrue(settings!.NotificationsEnabled);
        Assert.AreEqual("zh-Hans", settings.LanguagePreference);
        Assert.AreEqual(18, settings.FloatingStatsFontSize);

        var saved = JsonSerializer.Serialize(settings);
        Assert.IsTrue(saved.IndexOf("analytics", StringComparison.Ordinal) < 0,
            "Settings must no longer persist analytics state.");
        StringAssert.Contains(saved, "\"languagePreference\":\"zh-Hans\"");
        StringAssert.Contains(saved, "\"floatingStatsFontSize\":18");
    }
}
