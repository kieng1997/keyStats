using System.Text.Json;
using KeyStats.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

[TestClass]
public sealed class ThemePreferenceTests
{
    [TestMethod]
    [DataRow("{}", "system")]
    [DataRow("{\"themePreference\":null}", "system")]
    [DataRow("{\"themePreference\":\"unknown\"}", "system")]
    [DataRow("{\"themePreference\":\"light\"}", "light")]
    [DataRow("{\"themePreference\":\"dark\"}", "dark")]
    public void ThemePreference_RoundTripsOrDefaultsToSystem(string json, string expected)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(settings));

        Assert.AreEqual(expected, saved.RootElement.GetProperty("themePreference").GetString());
    }
}
