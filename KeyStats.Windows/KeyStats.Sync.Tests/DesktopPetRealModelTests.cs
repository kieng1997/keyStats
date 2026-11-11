using System;
using System.IO;
using System.Text;
using KeyStats.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

/// <summary>
/// Loads the user's real Mver package from a path supplied by the environment. It is skipped unless
/// KEYSTATS_REAL_PET_MODEL points at a downloaded model folder, so the suite never depends on
/// third-party files (which are not redistributable and must stay out of the repository).
/// </summary>
[TestClass]
public sealed class DesktopPetRealModelTests
{
    private const string ModelPathVariable = "KEYSTATS_REAL_PET_MODEL";

    [TestMethod]
    public void RealStandardPackage_LoadsWithConfiguredHands()
    {
        var root = Environment.GetEnvironmentVariable(ModelPathVariable);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Assert.Inconclusive(
                "Set " + ModelPathVariable + " to a local Mver model folder to run this check.");
            return;
        }

        var loaded = DesktopPetModelLoader.TryLoad(root, out var package, out var error);

        Assert.IsTrue(loaded, error);
        Assert.IsNotNull(package);
        Assert.IsTrue(File.Exists(Path.Combine(package!.RootPath, package.MocRelativePath)), "moc must resolve");
        foreach (var texture in package.TextureRelativePaths)
        {
            Assert.IsTrue(File.Exists(Path.Combine(package.RootPath, texture)), "texture must resolve: " + texture);
        }

        Assert.IsTrue(package.Hands.Count > 0, "the standard package must expose hand overlays");
        foreach (var hand in package.Hands)
        {
            Assert.IsTrue(hand.VirtualKeys.Count > 0);
            Assert.IsTrue(
                File.Exists(Path.Combine(package.RootPath, hand.ImageRelativePath)),
                "hand overlay must resolve: " + hand.ImageRelativePath);
        }

        var report = new StringBuilder();
        report.AppendLine("model3: " + package.Model3RelativePath);
        report.AppendLine("moc: " + package.MocRelativePath);
        report.AppendLine("textures: " + package.TextureRelativePaths.Count);
        report.AppendLine("hands: " + package.Hands.Count);
        for (var i = 0; i < package.Hands.Count; i++)
        {
            report.AppendLine(
                "  hand[" + i + "] keys=" + string.Join(",", package.Hands[i].VirtualKeys) +
                " image=" + package.Hands[i].ImageRelativePath);
        }

        Console.WriteLine(report.ToString());
        TestContext?.WriteLine(report.ToString());
    }

    public TestContext? TestContext { get; set; }
}
