using System;
using System.Collections.Generic;
using System.IO;
using KeyStats.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

[TestClass]
public sealed class DesktopPetModelLoaderTests
{
    private static readonly int[] StandardKeys =
    {
        65, 68, 69, 81, 82, 83, 87, 16, 32, 49, 50, 51, 52, 53, 54, 55, 17
    };

    /// <summary>
    /// Builds a Mver "standard" package. Optional knobs let a test express a broken package without
    /// deleting anything afterwards.
    /// </summary>
    private static void WriteStandardPackage(
        string root,
        string? configJson = null,
        string? model3Json = null,
        int? omitHandImageIndex = null,
        bool omitTextureFolder = false,
        byte mocVersion = 6,
        bool writeConfig = true)
    {
        var modelDir = Path.Combine(root, "img", "standard", "cat_model");
        Directory.CreateDirectory(modelDir);
        Directory.CreateDirectory(Path.Combine(root, "img", "standard", "hand"));
        if (!omitTextureFolder)
        {
            Directory.CreateDirectory(Path.Combine(modelDir, "cat_model.1024"));
            File.WriteAllBytes(
                Path.Combine(modelDir, "cat_model.1024", "texture_00.png"),
                new byte[] { 0x89, 0x50 });
            File.WriteAllText(Path.Combine(modelDir, "cat.physics3.json"), "{}");
        }

        if (writeConfig)
        {
            File.WriteAllText(
                Path.Combine(root, "config.json"),
                configJson ?? BuildSingleKeyPerHandConfig(StandardKeys));
        }

        File.WriteAllText(
            Path.Combine(modelDir, "cat.model3.json"),
            model3Json ?? @"{
  ""Version"": 3,
  ""FileReferences"": {
    ""Moc"": ""cat.moc3"",
    ""Textures"": [""cat_model.1024/texture_00.png""],
    ""Physics"": ""cat.physics3.json""
  }
}");

        File.WriteAllBytes(Path.Combine(modelDir, "cat.moc3"), BuildMoc3Header(mocVersion));

        for (var i = 0; i < StandardKeys.Length; i++)
        {
            if (i == omitHandImageIndex)
            {
                continue;
            }

            File.WriteAllBytes(
                Path.Combine(root, "img", "standard", "hand", i + ".png"),
                new byte[] { 0x89, 0x50 });
        }
    }

    private static string BuildSingleKeyPerHandConfig(IEnumerable<int> keys)
    {
        var groups = new List<IEnumerable<int>>();
        foreach (var key in keys)
        {
            groups.Add(new[] { key });
        }

        return BuildStandardConfig(groups);
    }

    private static string BuildStandardConfig(IEnumerable<IEnumerable<int>> handGroups)
    {
        var groups = new List<string>();
        foreach (var group in handGroups)
        {
            var keys = new List<string>();
            foreach (var key in group)
            {
                keys.Add(key.ToString());
            }

            groups.Add("[" + string.Join(", ", keys) + "]");
        }

        return "{\n  \"mode\": 1,\n  \"standard\": {\n    \"l2d\": true,\n    \"hand\": [" +
            string.Join(", ", groups) + "]\n  }\n}";
    }

    private static byte[] BuildMoc3Header(int version)
    {
        var bytes = new byte[64];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'O';
        bytes[2] = (byte)'C';
        bytes[3] = (byte)'3';
        bytes[4] = (byte)version;
        return bytes;
    }

    [TestMethod]
    public void ConfigWithUtf8Bom_IsLoaded()
    {
        using var directory = new TestDirectory();
        WriteStandardPackage(directory.Path);

        // The real Mver packages ship config.json with a UTF-8 BOM; that must not be treated as
        // malformed JSON.
        var configPath = Path.Combine(directory.Path, "config.json");
        var json = File.ReadAllText(configPath);
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        var withBom = new byte[bom.Length + body.Length];
        Buffer.BlockCopy(bom, 0, withBom, 0, bom.Length);
        Buffer.BlockCopy(body, 0, withBom, bom.Length, body.Length);
        File.WriteAllBytes(configPath, withBom);

        var loaded = DesktopPetModelLoader.TryLoad(directory.Path, out var package, out var error);

        Assert.IsTrue(loaded, error);
        Assert.AreEqual(17, package!.Hands.Count);
    }

    [TestMethod]
    public void AbsoluteTextureReference_IsRejectedEvenWhenTheFileExistsInPackage()
    {
        using var directory = new TestDirectory();
        // texture_00.png exists inside the package, so a stripped leading slash would silently pass.
        WriteStandardPackage(directory.Path, model3Json: @"{
  ""Version"": 3,
  ""FileReferences"": {
    ""Moc"": ""cat.moc3"",
    ""Textures"": [""/img/standard/cat_model/cat_model.1024/texture_00.png""]
  }
}");

        var loaded = DesktopPetModelLoader.TryLoad(directory.Path, out _, out var error);

        Assert.IsFalse(loaded);
        StringAssert.Contains(error, "不允许的路径");
    }

    [TestMethod]
    public void NetworkTextureReference_IsRejectedEvenWhenTheFileExistsInPackage()
    {
        using var directory = new TestDirectory();
        WriteStandardPackage(directory.Path, model3Json: @"{
  ""Version"": 3,
  ""FileReferences"": {
    ""Moc"": ""cat.moc3"",
    ""Textures"": [""\\\\server\\share\\texture_00.png""]
  }
}");

        var loaded = DesktopPetModelLoader.TryLoad(directory.Path, out _, out var error);

        Assert.IsFalse(loaded);
        StringAssert.Contains(error, "不允许的路径");
    }

    [TestMethod]
    public void StandardPackage_ExposesManifestHandImagesAndKeyMapping()
    {
        using var directory = new TestDirectory();
        WriteStandardPackage(directory.Path);

        var loaded = DesktopPetModelLoader.TryLoad(directory.Path, out var package, out var error);

        Assert.IsTrue(loaded, error);
        Assert.IsNotNull(package);
        Assert.AreEqual("img/standard/cat_model/cat.model3.json", package!.Model3RelativePath);
        Assert.AreEqual("img/standard/cat_model/cat.moc3", package.MocRelativePath);
        CollectionAssert.AreEqual(
            new[] { "img/standard/cat_model/cat_model.1024/texture_00.png" },
            (System.Collections.ICollection)package.TextureRelativePaths);

        Assert.AreEqual(17, package.Hands.Count, "every configured hand group must map to one overlay image");
        CollectionAssert.AreEqual(new[] { 65 }, (System.Collections.ICollection)package.Hands[0].VirtualKeys);
        Assert.AreEqual("img/standard/hand/0.png", package.Hands[0].ImageRelativePath);
        CollectionAssert.AreEqual(new[] { 17 }, (System.Collections.ICollection)package.Hands[16].VirtualKeys);
        Assert.AreEqual("img/standard/hand/16.png", package.Hands[16].ImageRelativePath);
    }

    [TestMethod]
    public void ConfigWithNonObjectJsonRoot_IsRejectedInsteadOfThrowing()
    {
        using var directory = new TestDirectory();
        WriteStandardPackage(directory.Path, configJson: "[]");

        AssertRejectedWithoutThrowing(directory.Path, out var error);
        StringAssert.Contains(error, "config.json");
    }

    [TestMethod]
    public void Model3WithNullJsonRoot_IsRejectedInsteadOfThrowing()
    {
        using var directory = new TestDirectory();
        WriteStandardPackage(directory.Path, model3Json: "null");

        AssertRejectedWithoutThrowing(directory.Path, out var error);
        StringAssert.Contains(error, "model3");
    }

    [TestMethod]
    public void MissingHandImage_IsRejectedWithChineseError()
    {
        using var directory = new TestDirectory();
        WriteStandardPackage(directory.Path, omitHandImageIndex: 3);

        var loaded = DesktopPetModelLoader.TryLoad(directory.Path, out var package, out var error);

        Assert.IsFalse(loaded);
        Assert.IsNull(package);
        StringAssert.Contains(error, "缺少文件");
    }

    [TestMethod]
    public void EmptyHandSlot_DoesNotRenumberLaterGroups()
    {
        using var directory = new TestDirectory();
        // Slot 0 has no keys; the group holding A must still resolve to hand/1.png.
        WriteStandardPackage(
            directory.Path,
            configJson: BuildStandardConfig(new List<IEnumerable<int>>
            {
                Array.Empty<int>(),
                new[] { 65 }
            }));

        var loaded = DesktopPetModelLoader.TryLoad(directory.Path, out var package, out var error);

        Assert.IsTrue(loaded, error);
        Assert.AreEqual(1, package!.Hands.Count, "an empty slot is skipped, not turned into an overlay");
        CollectionAssert.AreEqual(new[] { 65 }, (System.Collections.ICollection)package.Hands[0].VirtualKeys);
        Assert.AreEqual("img/standard/hand/1.png", package.Hands[0].ImageRelativePath);
    }

    [TestMethod]
    public void NonIntegerKeyCode_IsRejectedInsteadOfSilentlyDropped()
    {
        using var directory = new TestDirectory();
        WriteStandardPackage(
            directory.Path,
            configJson: "{\"standard\": {\"hand\": [[65, \"A\"]]}}");

        AssertRejectedWithoutThrowing(directory.Path, out var error);
        StringAssert.Contains(error, "hand");
    }

    [TestMethod]
    public void RemoteTextureReference_IsRejected()
    {
        using var directory = new TestDirectory();
        WriteStandardPackage(directory.Path, model3Json: @"{
  ""Version"": 3,
  ""FileReferences"": {
    ""Moc"": ""cat.moc3"",
    ""Textures"": [""https://example.com/texture_00.png""]
  }
}");

        var loaded = DesktopPetModelLoader.TryLoad(directory.Path, out _, out var error);

        Assert.IsFalse(loaded);
        StringAssert.Contains(error, "不允许的路径");
    }

    [TestMethod]
    public void ParentTraversalReference_IsRejected()
    {
        using var directory = new TestDirectory();
        WriteStandardPackage(directory.Path, model3Json: @"{
  ""Version"": 3,
  ""FileReferences"": {
    ""Moc"": ""../../../config.json"",
    ""Textures"": [""cat_model.1024/texture_00.png""]
  }
}");

        var loaded = DesktopPetModelLoader.TryLoad(directory.Path, out _, out var error);

        Assert.IsFalse(loaded);
        StringAssert.Contains(error, "不允许的路径");
    }

    [TestMethod]
    public void UnsupportedMocVersion_IsRejected()
    {
        using var directory = new TestDirectory();
        // 6 is the newest format this Core supports; 7 is beyond it. 2 is a valid Cubism 3.3 moc
        // and must not be rejected on version alone.
        WriteStandardPackage(directory.Path, mocVersion: 7);

        var loaded = DesktopPetModelLoader.TryLoad(directory.Path, out _, out var error);

        Assert.IsFalse(loaded);
        StringAssert.Contains(error, "版本不受支持");
    }

    [TestMethod]
    public void OlderMocVersionWithinCoreEnum_IsAccepted()
    {
        using var directory = new TestDirectory();
        WriteStandardPackage(directory.Path, mocVersion: 2);

        var loaded = DesktopPetModelLoader.TryLoad(directory.Path, out var package, out var error);

        Assert.IsTrue(loaded, error);
        Assert.IsNotNull(package);
    }

    [TestMethod]
    public void BareModelDirectory_LoadsWithoutHandOverlays()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "tex"));
        File.WriteAllText(Path.Combine(directory.Path, "solo.model3.json"), @"{
  ""Version"": 3,
  ""FileReferences"": {
    ""Moc"": ""solo.moc3"",
    ""Textures"": [""tex/texture_00.png""]
  }
}");
        File.WriteAllBytes(Path.Combine(directory.Path, "solo.moc3"), BuildMoc3Header(6));
        File.WriteAllBytes(Path.Combine(directory.Path, "tex", "texture_00.png"), new byte[] { 0x89, 0x50 });

        var loaded = DesktopPetModelLoader.TryLoad(directory.Path, out var package, out var error);

        Assert.IsTrue(loaded, error);
        Assert.AreEqual("solo.model3.json", package!.Model3RelativePath);
        Assert.AreEqual("solo.moc3", package.MocRelativePath);
        Assert.AreEqual(0, package.Hands.Count);
    }

    [TestMethod]
    public void EmptyDirectory_IsRejectedWithActionableError()
    {
        using var directory = new TestDirectory();

        var loaded = DesktopPetModelLoader.TryLoad(directory.Path, out var package, out var error);

        Assert.IsFalse(loaded);
        Assert.IsNull(package);
        StringAssert.Contains(error, "model3");
    }

    [TestMethod]
    public void NetworkRoot_IsRejectedWithoutTouchingTheNetwork()
    {
        AssertRejectedWithoutThrowing(@"\\server\share\model", out var error);
        StringAssert.Contains(error, "本地");
    }

    [TestMethod]
    public void PackageRootThatIsAJunction_IsRejected()
    {
        using var directory = new TestDirectory();
        using var outside = new TestDirectory();
        WriteStandardPackage(outside.Path);

        var linkPath = Path.Combine(directory.Path, "linked");
        if (!TryCreateJunction(linkPath, outside.Path))
        {
            Assert.Inconclusive("Creating a directory junction is not permitted in this environment.");
            return;
        }

        var loaded = DesktopPetModelLoader.TryLoad(linkPath, out _, out var error);

        Assert.IsFalse(loaded);
        StringAssert.Contains(error, "链接");
    }

    [TestMethod]
    public void JunctionInsidePackage_IsRejectedWithoutWalkingIt()
    {
        using var directory = new TestDirectory();
        using var outside = new TestDirectory();
        WriteStandardPackage(directory.Path, omitTextureFolder: true);
        Directory.CreateDirectory(Path.Combine(outside.Path, "cat_model.1024"));
        File.WriteAllBytes(
            Path.Combine(outside.Path, "cat_model.1024", "texture_00.png"),
            new byte[] { 0x89, 0x50 });

        var textureFolder = Path.Combine(directory.Path, "img", "standard", "cat_model", "cat_model.1024");
        if (!TryCreateJunction(textureFolder, Path.Combine(outside.Path, "cat_model.1024")))
        {
            Assert.Inconclusive("Creating a directory junction is not permitted in this environment.");
            return;
        }

        var loaded = DesktopPetModelLoader.TryLoad(directory.Path, out _, out var error);

        Assert.IsFalse(loaded);
        StringAssert.Contains(error, "链接");
    }

    private static void AssertRejectedWithoutThrowing(string rootPath, out string? error)
    {
        DesktopPetPackage? package = null;
        error = null;
        var loaded = false;
        try
        {
            loaded = DesktopPetModelLoader.TryLoad(rootPath, out package, out error);
        }
        catch (Exception exception)
        {
            Assert.Fail("TryLoad must not throw, but threw: " + exception.GetType().Name + ": " + exception.Message);
        }

        Assert.IsFalse(loaded, "invalid package must be rejected");
        Assert.IsNull(package);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error), "a user-facing error message is required");
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c mklink /J \"" + linkPath + "\" \"" + targetPath + "\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (process == null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception || exception is InvalidOperationException)
        {
            return false;
        }
    }
}
