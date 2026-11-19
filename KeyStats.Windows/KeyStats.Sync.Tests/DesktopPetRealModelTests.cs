using System;
using System.IO;
using System.Linq;
using System.Text;
using KeyStats.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

/// <summary>
/// Desktop pet model checks over real packages: the models bundled in
/// Resources/DesktopPet/Models (ds-standard, nailin-keyboard) must load from the test output, and
/// a user-supplied Mver package loads from KEYSTATS_REAL_PET_MODEL when that environment variable
/// is set (optional; the suite never requires it).
/// </summary>
[TestClass]
public sealed class DesktopPetRealModelTests
{
    private const string ModelPathVariable = "KEYSTATS_REAL_PET_MODEL";
    private const string BundledModelsFolder = "Resources/DesktopPet/Models";

    [DataTestMethod]
    [DataRow("ds-standard", "img/standard/cat_model/deepseek.model3.json", "img/standard/cat_model/deepseek.moc3", 17, null)]
    [DataRow("nailin-keyboard", "cat.model3.json", "cat.moc3", 60, "resources/background.png")]
    public void BundledModel_LoadsFromOutputResources(
        string directory,
        string expectedModel3RelativePath,
        string expectedMocRelativePath,
        int expectedHandCount,
        string? expectedBackgroundRelativePath)
    {
        var root = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            BundledModelsFolder,
            directory);

        var loaded = DesktopPetModelLoader.TryLoad(root, out var package, out var error);

        Assert.IsTrue(loaded, error);
        Assert.IsNotNull(package);
        Assert.AreEqual(expectedModel3RelativePath, package!.Model3RelativePath, "model3 manifest path");
        Assert.AreEqual(expectedMocRelativePath, package.MocRelativePath, "moc path");
        Assert.IsTrue(File.Exists(Path.Combine(package.RootPath, package.MocRelativePath)), "moc must resolve");
        Assert.IsTrue(package.TextureRelativePaths.Count > 0, "textures must be declared");
        foreach (var texture in package.TextureRelativePaths)
        {
            Assert.IsTrue(File.Exists(Path.Combine(package.RootPath, texture)), "texture must resolve: " + texture);
        }

        Assert.AreEqual(expectedHandCount, package.Hands.Count, "hand overlays");

        Assert.AreEqual(expectedBackgroundRelativePath, package.BackgroundRelativePath, "background");
        if (package.BackgroundRelativePath != null)
        {
            Assert.IsTrue(
                DesktopPetModelLoader.TryResolveFile(package.RootPath, package.BackgroundRelativePath, out _, out _),
                "background must resolve safely: " + package.BackgroundRelativePath);
        }
    }

    [TestMethod]
    public void NailinKeyboardModel_MapsLeftAndRightKeyOverlays()
    {
        var root = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            BundledModelsFolder,
            "nailin-keyboard");

        var loaded = DesktopPetModelLoader.TryLoad(root, out var package, out var error);

        Assert.IsTrue(loaded, error);
        Assert.IsNotNull(package);
        Assert.AreEqual(60, package!.Hands.Count, "left-keys + right-keys overlays");
        Assert.AreEqual(28, package.Hands.Count(h => !h.IsRightHand), "left-hand overlays");
        Assert.AreEqual(32, package.Hands.Count(h => h.IsRightHand), "right-hand overlays");

        foreach (var hand in package.Hands)
        {
            Assert.IsTrue(hand.VirtualKeys.Count > 0, "every overlay must cover at least one key");
            Assert.IsTrue(
                DesktopPetModelLoader.TryResolveFile(
                    package.RootPath, hand.ImageRelativePath, out _, out var resolveError),
                resolveError);
        }

        var leftA = package.Hands.Single(h =>
            h.VirtualKeys.Count == 1 && h.VirtualKeys[0] == 65 && !h.IsRightHand);
        Assert.AreEqual("resources/left-keys/KeyA.png", leftA.ImageRelativePath);

        var rightReturn = package.Hands.Single(h =>
            h.VirtualKeys.Count == 1 && h.VirtualKeys[0] == 13 && h.IsRightHand);
        Assert.AreEqual("resources/right-keys/Return.png", rightReturn.ImageRelativePath);

        var metaHands = package.Hands.Where(h => h.VirtualKeys.Contains(91)).ToList();
        Assert.AreEqual(1, metaHands.Count, "Meta maps to one shared left overlay");
        Assert.AreEqual("resources/left-keys/Meta.png", metaHands[0].ImageRelativePath);
        Assert.IsFalse(metaHands[0].IsRightHand);
    }

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
