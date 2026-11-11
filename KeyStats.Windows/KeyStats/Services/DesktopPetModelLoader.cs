using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KeyStats.Services;

/// <summary>
/// One configured key group from the model package's config.json, paired with its hand overlay image.
/// </summary>
public sealed class DesktopPetHandOverlay
{
    public DesktopPetHandOverlay(IReadOnlyList<int> virtualKeys, string imageRelativePath)
    {
        VirtualKeys = virtualKeys;
        ImageRelativePath = imageRelativePath;
    }

    /// <summary>Virtual-key codes that activate this overlay; any one pressed shows it.</summary>
    public IReadOnlyList<int> VirtualKeys { get; }

    /// <summary>Package-relative path to the overlay PNG.</summary>
    public string ImageRelativePath { get; }
}

/// <summary>
/// A validated, immutable desktop pet model package. All paths are package-relative and use forward
/// slashes so they can be appended to the viewer's model base URL unchanged.
/// </summary>
public sealed class DesktopPetPackage
{
    public DesktopPetPackage(
        string rootPath,
        string model3RelativePath,
        string mocRelativePath,
        IReadOnlyList<string> textureRelativePaths,
        IReadOnlyList<DesktopPetHandOverlay> hands)
    {
        RootPath = rootPath;
        Model3RelativePath = model3RelativePath;
        MocRelativePath = mocRelativePath;
        TextureRelativePaths = textureRelativePaths;
        Hands = hands;
    }

    /// <summary>Absolute directory the viewer is allowed to serve from.</summary>
    public string RootPath { get; }

    public string Model3RelativePath { get; }

    public string MocRelativePath { get; }

    public IReadOnlyList<string> TextureRelativePaths { get; }

    /// <summary>Overlay images in config order; an empty configured slot produces no entry.</summary>
    public IReadOnlyList<DesktopPetHandOverlay> Hands { get; }
}

/// <summary>
/// Loads a desktop pet model directory. Supported layouts are a Mver "standard" package
/// (<c>config.json</c> + <c>img/standard/&lt;model&gt;/*.model3.json</c> + <c>img/standard/hand/&lt;index&gt;.png</c>)
/// and a bare directory that directly contains exactly one <c>*.model3.json</c>.
/// Every referenced file is resolved inside the package root before it is opened; anything that
/// would escape the root (absolute, UNC, traversal, reparse point) is rejected.
/// </summary>
public static class DesktopPetModelLoader
{
    // Both config.json and model3.json are tiny by design; cap them so a wrong folder cannot
    // exhaust the process memory.
    private const long MaximumJsonBytes = 4L * 1024 * 1024;

    // Cubism Core moc3 format enum: MocVersion_30=1 .. MocVersion_53=6. Anything outside the enum
    // cannot be created by any Core version; the actual load is validated by Core consistency.
    private const int MinimumSupportedMocVersion = 1;
    private const int MaximumSupportedMocVersion = 6;

    private const string ConfigFileName = "config.json";
    private const string StandardFolder = "img/standard";
    private const string HandFolder = "img/standard/hand";

    public static bool TryLoad(string? rootPath, out DesktopPetPackage? package, out string? error)
    {
        package = null;
        error = null;

        if (!TryResolveRoot(rootPath, out var root, out error))
        {
            return false;
        }

        var configPath = Path.Combine(root, ConfigFileName);
        if (File.Exists(configPath))
        {
            return TryLoadStandardPackage(root, out package, out error);
        }

        return TryLoadBareModel(root, out package, out error);
    }

    /// <summary>
    /// Validates the chosen folder itself before anything is read: it must be an existing local
    /// directory that is not a reparse point.
    /// </summary>
    private static bool TryResolveRoot(string? rootPath, out string root, out string? error)
    {
        root = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            error = "未选择模型文件夹";
            return false;
        }

        var trimmed = rootPath!.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            error = "请选择本地模型文件夹，不支持网络路径";
            return false;
        }

        try
        {
            root = Path.GetFullPath(trimmed);
        }
        catch (Exception exception) when (
            exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        {
            error = "模型文件夹路径无效";
            return false;
        }

        if (!Directory.Exists(root))
        {
            error = "模型文件夹不存在";
            return false;
        }

        if (IsReparsePoint(root))
        {
            error = "所选文件夹是链接目录，已拒绝";
            return false;
        }

        return true;
    }

    private static bool TryLoadStandardPackage(string root, out DesktopPetPackage? package, out string? error)
    {
        package = null;
        error = null;

        var standardFolder = Path.Combine(root, "img", "standard");
        if (!TryResolveDirectory(root, standardFolder, out _, out error))
        {
            return false;
        }

        // The Mver standard layout keeps the model in a single sub-folder; no unbounded recursion.
        if (!TryEnumerateDirectories(standardFolder, out var modelFolders, out error))
        {
            return false;
        }

        var manifests = new List<string>();
        foreach (var modelFolder in modelFolders)
        {
            if (IsReparsePoint(modelFolder))
            {
                error = "模型目录包含链接目录，已拒绝";
                return false;
            }

            if (!TryEnumerateFiles(modelFolder, "*.model3.json", out var found, out error))
            {
                return false;
            }

            manifests.AddRange(found);
        }

        if (manifests.Count == 0)
        {
            error = "模型中缺少 model3 文件";
            return false;
        }

        if (manifests.Count > 1)
        {
            error = "模型中有多个 model3 文件，无法确定要显示哪一个";
            return false;
        }

        if (!TryReadStandardHandGroups(root, out var handGroups, out error))
        {
            return false;
        }

        return TryBuildPackage(root, manifests[0], handGroups, out package, out error);
    }

    private static bool TryLoadBareModel(string root, out DesktopPetPackage? package, out string? error)
    {
        package = null;
        error = null;

        if (!TryEnumerateFiles(root, "*.model3.json", out var manifests, out error))
        {
            return false;
        }

        if (manifests.Count == 0)
        {
            error = "所选文件夹中没有 model3 模型文件";
            return false;
        }

        if (manifests.Count > 1)
        {
            error = "所选文件夹中有多个 model3 模型文件";
            return false;
        }

        // A bare directory has no Mver config, so there is no hand mapping to apply.
        return TryBuildPackage(root, manifests[0], Array.Empty<KeyValuePair<int, IReadOnlyList<int>>>(), out package, out error);
    }

    /// <summary>
    /// Reads the configured hand groups, preserving array slot order: slot i maps to hand/i.png.
    /// An empty slot produces no overlay but never renumbers the slots after it.
    /// </summary>
    private static bool TryReadStandardHandGroups(
        string root,
        out IReadOnlyList<KeyValuePair<int, IReadOnlyList<int>>> handGroups,
        out string? error)
    {
        handGroups = Array.Empty<KeyValuePair<int, IReadOnlyList<int>>>();
        error = null;

        var configPath = Path.Combine(root, ConfigFileName);
        if (!TryReadJson(configPath, root, "config.json", out var document, out error))
        {
            return false;
        }

        using (document!)
        {
            var rootElement = document!.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object)
            {
                error = "config.json 格式无效";
                return false;
            }

            if (!rootElement.TryGetProperty("standard", out var standard) ||
                standard.ValueKind != JsonValueKind.Object)
            {
                error = "config.json 缺少 standard 配置";
                return false;
            }

            if (!standard.TryGetProperty("hand", out var hand) || hand.ValueKind != JsonValueKind.Array)
            {
                handGroups = Array.Empty<KeyValuePair<int, IReadOnlyList<int>>>();
                return true;
            }

            var groups = new List<KeyValuePair<int, IReadOnlyList<int>>>();
            var slot = 0;
            foreach (var group in hand.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Array)
                {
                    error = "config.json 的 hand 配置格式无效";
                    return false;
                }

                var keys = new List<int>();
                foreach (var key in group.EnumerateArray())
                {
                    if (key.ValueKind != JsonValueKind.Number || !key.TryGetInt32(out var virtualKey) ||
                        virtualKey <= 0 || virtualKey > 0xFF)
                    {
                        error = "config.json 的 hand 配置包含无效键码";
                        return false;
                    }

                    keys.Add(virtualKey);
                }

                if (keys.Count > 0)
                {
                    groups.Add(new KeyValuePair<int, IReadOnlyList<int>>(slot, keys));
                }

                slot++;
            }

            handGroups = groups;
            return true;
        }
    }

    private static bool TryBuildPackage(
        string root,
        string model3FullPath,
        IReadOnlyList<KeyValuePair<int, IReadOnlyList<int>>> handGroups,
        out DesktopPetPackage? package,
        out string? error)
    {
        package = null;
        error = null;

        if (!TryResolveInsideRoot(root, model3FullPath, out var model3RelativePath, out error))
        {
            return false;
        }

        if (!TryReadJson(model3FullPath, root, "model3", out var document, out error))
        {
            return false;
        }

        using (document!)
        {
            var rootElement = document!.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object ||
                !rootElement.TryGetProperty("FileReferences", out var references) ||
                references.ValueKind != JsonValueKind.Object ||
                !references.TryGetProperty("Moc", out var moc) ||
                moc.ValueKind != JsonValueKind.String)
            {
                error = "model3 文件缺少 Moc 引用";
                return false;
            }

            var model3Folder = Path.GetDirectoryName(model3RelativePath)?.Replace('\\', '/') ?? string.Empty;
            if (!TryCombineReference(model3Folder, moc.GetString(), out var mocRelative, out error))
            {
                return false;
            }

            if (!TryResolveFile(root, mocRelative, out var mocFullPath, out error))
            {
                return false;
            }

            if (!TryCheckMocVersion(mocFullPath!, out error))
            {
                return false;
            }

            if (!references.TryGetProperty("Textures", out var textureArray) ||
                textureArray.ValueKind != JsonValueKind.Array)
            {
                error = "model3 文件缺少纹理引用";
                return false;
            }

            var textures = new List<string>();
            foreach (var texture in textureArray.EnumerateArray())
            {
                if (texture.ValueKind != JsonValueKind.String)
                {
                    error = "model3 文件的纹理引用无效";
                    return false;
                }

                var textureReference = texture.GetString()!;
                if (!TryCombineReference(model3Folder, textureReference, out var textureRelative, out error))
                {
                    return false;
                }

                if (!TryResolveFile(root, textureRelative, out _, out error))
                {
                    return false;
                }

                textures.Add(textureRelative);
            }

            if (textures.Count == 0)
            {
                error = "model3 文件缺少纹理引用";
                return false;
            }

            var hands = new List<DesktopPetHandOverlay>(handGroups.Count);
            foreach (var group in handGroups)
            {
                var imageRelative = HandFolder + "/" + group.Key + ".png";
                if (!TryResolveFile(root, imageRelative, out _, out error))
                {
                    return false;
                }

                hands.Add(new DesktopPetHandOverlay(group.Value, imageRelative));
            }

            package = new DesktopPetPackage(root, model3RelativePath, mocRelative, textures, hands);
            return true;
        }
    }

    private static bool TryReadJson(
        string fullPath,
        string root,
        string label,
        out JsonDocument? document,
        out string? error)
    {
        document = null;
        error = null;

        if (!TryResolveInsideRoot(root, fullPath, out _, out error))
        {
            error = label + " 不在所选模型文件夹内";
            return false;
        }

        if (IsReparsePoint(fullPath))
        {
            error = label + " 是链接文件，已拒绝";
            return false;
        }

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                error = "模型缺少文件：" + label;
                return false;
            }

            if (info.Length > MaximumJsonBytes)
            {
                error = label + " 文件过大";
                return false;
            }

            document = JsonDocument.Parse(File.ReadAllText(fullPath));
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException || exception is IOException || exception is UnauthorizedAccessException)
        {
            error = label + " 无法解析";
            return false;
        }
    }

    private static bool TryCheckMocVersion(string mocFullPath, out string? error)
    {
        error = null;
        byte[] header;
        try
        {
            using var stream = File.OpenRead(mocFullPath);
            header = new byte[8];
            if (stream.Read(header, 0, header.Length) < header.Length)
            {
                error = "moc3 文件不完整";
                return false;
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            error = "moc3 文件无法读取";
            return false;
        }

        if (header[0] != 'M' || header[1] != 'O' || header[2] != 'C' || header[3] != '3')
        {
            error = "moc3 文件格式无效";
            return false;
        }

        var version = header[4];
        if (version < MinimumSupportedMocVersion || version > MaximumSupportedMocVersion)
        {
            error = "moc3 文件版本不受支持（版本 " + version + "）";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves a package-relative path, rejecting rooted paths, UNC, URL schemes, traversal, links
    /// and anything that escapes the package root. Shared with the viewer's asset routing.
    /// </summary>
    public static bool TryResolveFile(string root, string? relativePath, out string? fullPath, out string? error)
    {
        fullPath = null;
        error = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "模型引用了空路径";
            return false;
        }

        var relative = relativePath!.Replace('\\', '/');
        if (relative.IndexOf(':') >= 0 ||
            relative.StartsWith("//", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            error = "模型引用了不允许的路径";
            return false;
        }

        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                error = "模型引用了不允许的路径";
                return false;
            }
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (
            exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        {
            error = "模型引用了不允许的路径";
            return false;
        }

        if (!TryResolveInsideRoot(root, candidate, out _, out error))
        {
            return false;
        }

        if (!File.Exists(candidate))
        {
            error = "模型缺少文件：" + relative;
            return false;
        }

        if (IsReparsePoint(candidate) || HasReparsePointBelow(root, candidate))
        {
            error = "模型引用了链接文件，已拒绝";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    /// <summary>Checks the candidate stays under <paramref name="root"/> without touching the filesystem.</summary>
    private static bool TryResolveInsideRoot(string root, string candidate, out string relativePath, out string? error)
    {
        relativePath = string.Empty;
        error = null;

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (!normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            error = "模型引用了不允许的路径";
            return false;
        }

        relativePath = normalizedCandidate
            .Substring(normalizedRoot.Length)
            .Replace(Path.DirectorySeparatorChar, '/');
        return true;
    }

    /// <summary>Resolves a directory under the root and rejects reparse points along the way.</summary>
    private static bool TryResolveDirectory(string root, string fullPath, out string? resolved, out string? error)
    {
        resolved = null;
        error = null;

        if (!TryResolveInsideRoot(root, fullPath, out var relativePath, out error))
        {
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            error = "模型缺少目录：" + relativePath;
            return false;
        }

        if (IsReparsePoint(fullPath) || HasReparsePointBelow(root, fullPath))
        {
            error = "模型目录是链接目录，已拒绝";
            return false;
        }

        resolved = fullPath;
        return true;
    }

    private static bool HasReparsePointBelow(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetDirectoryName(Path.GetFullPath(candidate));
        var guard = 0;
        while (!string.IsNullOrEmpty(current) &&
               current.Length > normalizedRoot.Length &&
               guard++ < 256)
        {
            if (IsReparsePoint(current))
            {
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // Unreadable is not trusted, so treat it as a link so the package is rejected.
            return true;
        }
    }

    /// <summary>Top-level enumeration only, so a bad package cannot make us walk its subtree.</summary>
    private static bool TryEnumerateFiles(string folder, string pattern, out List<string> files, out string? error)
    {
        files = new List<string>();
        error = null;
        try
        {
            files.AddRange(Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly).Take(2));
            return true;
        }
        catch (Exception exception) when (
            exception is IOException || exception is UnauthorizedAccessException || exception is DirectoryNotFoundException)
        {
            error = "模型文件夹无法访问";
            return false;
        }
    }

    private static bool TryEnumerateDirectories(string folder, out List<string> folders, out string? error)
    {
        folders = new List<string>();
        error = null;
        try
        {
            folders.AddRange(Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly));
            return true;
        }
        catch (Exception exception) when (
            exception is IOException || exception is UnauthorizedAccessException || exception is DirectoryNotFoundException)
        {
            error = "模型文件夹无法访问";
            return false;
        }
    }

    /// <summary>
    /// Joins a manifest-relative reference onto its model folder. Rooted, UNC and URL references are
    /// rejected before joining, so "<c>/texture.png</c>" can never be trimmed back to a package path.
    /// </summary>
    private static bool TryCombineReference(string folder, string? reference, out string relativePath, out string? error)
    {
        relativePath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(reference))
        {
            error = "模型引用了空路径";
            return false;
        }

        var raw = reference!.Trim();
        var normalized = raw.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.StartsWith("//", StringComparison.Ordinal) ||
            raw.StartsWith(@"\", StringComparison.Ordinal) ||
            raw.StartsWith(@"\\", StringComparison.Ordinal) ||
            raw.IndexOf(':') >= 0 ||
            Path.IsPathRooted(raw))
        {
            error = "模型引用了不允许的路径";
            return false;
        }

        relativePath = string.IsNullOrEmpty(folder) ? normalized : folder + "/" + normalized;
        return true;
    }
}
