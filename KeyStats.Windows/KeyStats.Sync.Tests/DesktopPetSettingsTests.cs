using System.Text.Json;
using KeyStats.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

[TestClass]
public sealed class DesktopPetSettingsTests
{
    [TestMethod]
    public void LegacySettingsWithoutDesktopPetPath_LoadWithNullPath()
    {
        const string legacyJson = @"{
            ""notificationsEnabled"": true,
            ""floatingStatsEnabled"": true,
            ""floatingStatsFontSize"": 12
        }";

        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson);

        Assert.IsNotNull(settings);
        Assert.IsTrue(settings!.FloatingStatsEnabled);
        Assert.IsNull(settings.DesktopPetModelPath);
    }

    [TestMethod]
    public void DesktopPetPath_SurvivesJsonRoundTrip()
    {
        var original = new AppSettings
        {
            DesktopPetModelPath = @"D:\models\cat",
            FloatingStatsPositionLocked = true
        };

        var json = JsonSerializer.Serialize(original);
        StringAssert.Contains(json, "\"desktopPetModelPath\":\"D:\\\\models\\\\cat\"");

        var reloaded = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.IsNotNull(reloaded);
        Assert.AreEqual(@"D:\models\cat", reloaded!.DesktopPetModelPath);
        Assert.IsTrue(reloaded.FloatingStatsPositionLocked);
    }
}
