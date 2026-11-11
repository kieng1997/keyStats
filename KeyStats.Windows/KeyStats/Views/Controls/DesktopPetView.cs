using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KeyStats.Helpers;
using KeyStats.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KeyStats.Views.Controls;

/// <summary>
/// Samples the configured keys and mouse cursor at ~30 Hz while visible and forwards each frame to
/// the offline viewer. Only the current state is read: nothing is persisted, logged or sent anywhere.
/// </summary>
public sealed class DesktopPetInputSampler : IDisposable
{
    private const int SampleIntervalMilliseconds = 33;

    private readonly DispatcherTimer _timer;
    private readonly List<int> _virtualKeys = new List<int>();
    private readonly Func<IReadOnlyList<int>, DesktopPetInputFrame> _readFrame;
    private Action<DesktopPetInputFrame>? _onFrame;
    private bool _disposed;

    /// <param name="readFrame">
    /// Reads one frame for the given configured keys. Defaults to the real Win32 state; tests pass a
    /// scripted reader so no OS-level input is injected or sampled.
    /// </param>
    public DesktopPetInputSampler(Func<IReadOnlyList<int>, DesktopPetInputFrame>? readFrame = null)
    {
        _readFrame = readFrame ?? ReadSystemFrame;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(SampleIntervalMilliseconds)
        };
        _timer.Tick += OnTick;
    }

    private static DesktopPetInputFrame ReadSystemFrame(IReadOnlyList<int> virtualKeys)
    {
        var keys = new List<int>();
        foreach (var virtualKey in virtualKeys)
        {
            if (NativeInterop.IsKeyDown(virtualKey))
            {
                keys.Add(virtualKey);
            }
        }

        var cursor = default(NativeInterop.POINT);
        var hasCursor = NativeInterop.GetCursorPos(out cursor);

        return new DesktopPetInputFrame(
            keys,
            hasCursor ? cursor.x : 0,
            hasCursor ? cursor.y : 0,
            NativeInterop.IsKeyDown(VirtualKeys.LeftButton),
            NativeInterop.IsKeyDown(VirtualKeys.RightButton));
    }

    public bool IsRunning => _timer.IsEnabled;

    /// <summary>
    /// Starts sampling. <paramref name="handVirtualKeys"/> is the union of every key group in the
    /// model package, so a press is forwarded even if the model lacks an overlay for it.
    /// </summary>
    public void Start(IEnumerable<int> handVirtualKeys, Action<DesktopPetInputFrame> onFrame)
    {
        if (_disposed)
        {
            return;
        }

        _onFrame = onFrame;
        _virtualKeys.Clear();
        foreach (var virtualKey in handVirtualKeys)
        {
            if (!_virtualKeys.Contains(virtualKey))
            {
                _virtualKeys.Add(virtualKey);
            }
        }

        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    public void Stop()
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || _onFrame == null)
        {
            return;
        }

        _onFrame(_readFrame(_virtualKeys));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _timer.Tick -= OnTick;
        _onFrame = null;
    }

    private static class VirtualKeys
    {
        public const int LeftButton = 0x01;
        public const int RightButton = 0x02;
    }
}

/// <summary>One sampled input frame: held keys plus cursor position in screen pixels.</summary>
public readonly struct DesktopPetInputFrame
{
    public DesktopPetInputFrame(IReadOnlyList<int> keys, int cursorX, int cursorY, bool leftDown, bool rightDown)
    {
        Keys = keys;
        CursorX = cursorX;
        CursorY = cursorY;
        LeftDown = leftDown;
        RightDown = rightDown;
    }

    public IReadOnlyList<int> Keys { get; }
    public int CursorX { get; }
    public int CursorY { get; }
    public bool LeftDown { get; }
    public bool RightDown { get; }
}

/// <summary>
/// Hosts the offline Live2D viewer and drives it from sampled input.
/// Everything (viewer page and model assets) is served from one reserved virtual origin so textures
/// stay origin-clean and no CORS is involved. Model files are answered only through
/// <see cref="CoreWebView2.WebResourceRequested"/>, after the same validation the loader uses;
/// the arbitrary model folder is never mapped wholesale.
/// </summary>
public sealed class DesktopPetView : Grid, IDisposable
{
    private const string Origin = "https://keystats-pet.invalid";
    private const string ModelRoutePrefix = "/model/";
    private const int MaximumAssetBytes = 32 * 1024 * 1024;
    private const double ModelViewSize = 340;

    private readonly string _appAssetFolder;
    private readonly string _userDataFolder;
    private readonly DesktopPetInputSampler _sampler;
    private WebView2CompositionControl? _webView;
    private bool _disposed;
    private bool _coreReady;
    private bool _pageReady;
    private bool _visible;
    private DesktopPetPackage? _activePackage;
    private List<int> _activeVirtualKeys = new List<int>();
    private int _loadId;
    // Load id whose model reported ready and is therefore allowed to sample input.
    private int _readyLoadId;

    /// <param name="userDataFolder">
    /// Optional WebView2 profile folder. Tests pass an isolated folder; production leaves it null and
    /// gets the app's own LocalApplicationData folder.
    /// </param>
    /// <param name="readInputFrame">
    /// Optional input reader seam. Defaults to the real keyboard/cursor state; tests script it.
    /// </param>
    public DesktopPetView(
        string? userDataFolder = null,
        Func<IReadOnlyList<int>, DesktopPetInputFrame>? readInputFrame = null)
    {
        _appAssetFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "DesktopPet");
        _userDataFolder = string.IsNullOrWhiteSpace(userDataFolder)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KeyStats",
                "desktop_pet_webview")
            : userDataFolder!;
        _sampler = new DesktopPetInputSampler(readInputFrame);

        _webView = new WebView2CompositionControl
        {
            // WPF's root border owns dragging and the context menu; the page never needs hit testing.
            IsHitTestVisible = false,
            DefaultBackgroundColor = System.Drawing.Color.Transparent
        };

        Children.Add(_webView);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Width = ModelViewSize;
        Height = ModelViewSize;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>Raised with a user-facing Chinese message when the pet cannot be shown.</summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>True once the viewer page and Cubism Core are both ready to accept a model.</summary>
    public bool IsViewerReady => _coreReady && _pageReady;

    public async void LoadModel(DesktopPetPackage? package)
    {
        if (_disposed)
        {
            return;
        }

        var loadId = ++_loadId;
        _readyLoadId = 0;

        if (package == null)
        {
            _activePackage = null;
            _activeVirtualKeys = new List<int>();
            _sampler.Stop();
            await SendAsync(new { type = "unload" }).ConfigureAwait(true);
            return;
        }

        _activePackage = package;
        _activeVirtualKeys = CollectVirtualKeys(package);
        _sampler.Stop();

        if (!IsViewerReady)
        {
            // The page signals readiness; OnViewerReady sends the pending package.
            return;
        }

        await SendModelAsync(package, loadId).ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task SendModelAsync(DesktopPetPackage package, int loadId)
    {
        var hands = new List<object>(package.Hands.Count);
        foreach (var hand in package.Hands)
        {
            hands.Add(new { virtualKeys = hand.VirtualKeys, image = hand.ImageRelativePath });
        }

        var payload = new
        {
            type = "load",
            loadId,
            baseUrl = Origin + ModelRoutePrefix,
            moc = package.MocRelativePath,
            textures = package.TextureRelativePaths,
            hands
        };

        await SendAsync(payload).ConfigureAwait(true);
    }

    private static List<int> CollectVirtualKeys(DesktopPetPackage package)
    {
        var keys = new List<int>();
        foreach (var hand in package.Hands)
        {
            foreach (var virtualKey in hand.VirtualKeys)
            {
                if (!keys.Contains(virtualKey))
                {
                    keys.Add(virtualKey);
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// Converts a global cursor position into 0..1 across the Windows virtual desktop, which is what
    /// the viewer's input state expects. The pet window's own size/DPI is irrelevant here: a desktop
    /// pet reacts to the whole desktop, not only to the area above itself.
    /// </summary>
    private void OnInputFrame(DesktopPetInputFrame frame)
    {
        if (_disposed || _webView?.CoreWebView2 == null)
        {
            return;
        }

        var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (desktop.Width <= 0 || desktop.Height <= 0)
        {
            return;
        }

        SendAsync(new
        {
            type = "input",
            keys = frame.Keys,
            mouseX = (frame.CursorX - desktop.Left) / (double)desktop.Width,
            mouseY = (frame.CursorY - desktop.Top) / (double)desktop.Height,
            leftDown = frame.LeftDown,
            rightDown = frame.RightDown
        });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await EnsureCoreAsync().ConfigureAwait(true);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _sampler.Stop();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _visible = (bool)e.NewValue;

        if (_visible)
        {
            SendAsync(new { type = "resume" });
            // Showing again must resume sampling for a model that is still the ready one; the page's
            // own resume only restarts rendering and never re-sends "ready".
            StartSamplerIfReady();
        }
        else
        {
            _sampler.Stop();
            SendAsync(new { type = "pause" });
        }
    }

    /// <summary>
    /// Starts sampling only when the currently active model reported ready and the window is shown.
    /// </summary>
    private void StartSamplerIfReady()
    {
        if (_disposed || !_visible || _activePackage == null || _readyLoadId != _loadId)
        {
            return;
        }

        _sampler.Start(_activeVirtualKeys, OnInputFrame);
    }

    private async System.Threading.Tasks.Task EnsureCoreAsync()
    {
        if (_disposed || _webView == null || _coreReady)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_userDataFolder);
            var environment = await CoreWebView2Environment
                .CreateAsync(null, _userDataFolder, null)
                .ConfigureAwait(true);
            if (_disposed || _webView == null)
            {
                return;
            }

            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            if (_disposed || _webView?.CoreWebView2 == null)
            {
                return;
            }

            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            // Every request is answered here: the fixed viewer files, the package's declared assets,
            // and a local refusal for anything else (including any external origin).
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

            core.WebResourceRequested += OnWebResourceRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.ProcessFailed += OnProcessFailed;

            // The viewer never downloads anything; cancelling keeps a hostile or broken model
            // package from writing into the user's Downloads folder.
            core.DownloadStarting += OnDownloadStarting;

            _coreReady = true;
            core.Navigate(Origin + "/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ReportError("未安装 WebView2 运行时，请先安装 Microsoft Edge WebView2 Runtime");
        }
        catch (DllNotFoundException)
        {
            ReportError("模型窗口组件缺少运行库文件，请重新安装应用");
        }
        catch (BadImageFormatException)
        {
            ReportError("模型窗口组件与当前系统位数不匹配，请重新安装应用");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException || exception is COMException ||
            exception is IOException || exception is UnauthorizedAccessException)
        {
            ReportError("模型窗口初始化失败，请重启应用后重试");
        }
    }

    /// <summary>
    /// Serves one model file per request. The path is decoded, percent-decoding is undone exactly
    /// once, and the same <see cref="DesktopPetModelLoader"/> resolution used at import time decides
    /// whether the file may be read. Anything else is refused locally; nothing is fetched.
    /// </summary>
    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var core = _webView?.CoreWebView2;
            if (core == null)
            {
                return;
            }

            var requestUri = new Uri(e.Request.Uri);
            if (requestUri.GetLeftPart(UriPartial.Authority) != Origin)
            {
                Reject(core, e);
                return;
            }

            var path = Uri.UnescapeDataString(requestUri.AbsolutePath);
            if (path.StartsWith(ModelRoutePrefix, StringComparison.Ordinal))
            {
                ServeModelAsset(core, e, path.Substring(ModelRoutePrefix.Length));
                return;
            }

            ServeViewerAsset(core, e, path);
        }
        catch (Exception exception) when (
            exception is UriFormatException || exception is ArgumentException ||
            exception is InvalidOperationException || exception is IOException)
        {
            var core = _webView?.CoreWebView2;
            if (core != null)
            {
                Reject(core, e);
            }
        }
    }

    /// <summary>Serves a fixed viewer file from the app's own Resources\DesktopPet folder.</summary>
    private void ServeViewerAsset(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string path)
    {
        var relativePath = path.TrimStart('/');
        if (relativePath.Length == 0)
        {
            relativePath = "index.html";
        }

        if (relativePath.IndexOf("..", StringComparison.Ordinal) >= 0 ||
            relativePath.IndexOf(':') >= 0 ||
            !IsAllowedViewerAsset(relativePath))
        {
            Reject(core, e);
            return;
        }

        var fullPath = Path.GetFullPath(Path.Combine(
            _appAssetFolder,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(_appAssetFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            Reject(core, e);
            return;
        }

        Respond(core, e, fullPath, relativePath);
    }

    /// <summary>
    /// Only files that ship with the viewer are served from the app folder: the page, our own
    /// scripts, and the vendored Cubism runtime/shaders/licenses. Nothing else is reachable.
    /// </summary>
    private static bool IsAllowedViewerAsset(string relativePath)
    {
        if (string.Equals(relativePath, "index.html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(relativePath, "viewer.js", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(relativePath, "input-state.js", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!relativePath.StartsWith("vendor/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = relativePath.Substring("vendor/".Length);
        if (name.IndexOf('/') < 0)
        {
            switch (name.ToLowerInvariant())
            {
                case "live2dcubismcore.min.js":
                case "framework.js":
                case "version.txt":
                case "license-live2d-core.md":
                case "license-live2d-framework.md":
                case "redistributablefiles-core.txt":
                    return true;
            }

            return false;
        }

        return name.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase) &&
               name.IndexOf('/', "shaders/".Length) < 0 &&
               (name.EndsWith(".vert", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".frag", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Serves one model file. The path is decoded once and the same loader resolution used at import
    /// time decides whether the file may be read; only declared assets are allowed.
    /// </summary>
    private void ServeModelAsset(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string relativePath)
    {
        var package = _activePackage;
        if (package == null ||
            !IsAllowedModelAsset(package, relativePath) ||
            !DesktopPetModelLoader.TryResolveFile(package.RootPath, relativePath, out var fullPath, out _))
        {
            Reject(core, e);
            return;
        }

        Respond(core, e, fullPath!, relativePath);
    }

    private void Respond(
        CoreWebView2 core,
        CoreWebView2WebResourceRequestedEventArgs e,
        string fullPath,
        string relativePath)
    {
        byte[] content;
        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > MaximumAssetBytes)
            {
                Reject(core, e);
                return;
            }

            content = File.ReadAllBytes(fullPath);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Reject(core, e);
            return;
        }

        // WebView2 requires the response stream to already hold all content and be agile, so the
        // bytes are buffered in memory rather than handed over as a FileStream.
        e.Response = core.Environment.CreateWebResourceResponse(
            new MemoryStream(content, writable: false),
            200,
            "OK",
            "Content-Type: " + ContentTypeFor(relativePath) + "\r\nCache-Control: no-store");
    }

    /// <summary>
    /// Only the files the package itself declared may be served: the moc, its textures and the
    /// configured hand overlays. A model folder never turns into a general file server.
    /// </summary>
    private static bool IsAllowedModelAsset(DesktopPetPackage package, string relativePath)
    {
        if (string.Equals(relativePath, package.MocRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var texture in package.TextureRelativePaths)
        {
            if (string.Equals(relativePath, texture, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var hand in package.Hands)
        {
            if (string.Equals(relativePath, hand.ImageRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The viewer is served from an in-process handler, so the browser has no other way to learn a
    /// file type. A wrong type here makes the browser download the page instead of rendering it.
    /// </summary>
    private static string ContentTypeFor(string relativePath)
    {
        var extension = Path.GetExtension(relativePath).ToLowerInvariant();
        switch (extension)
        {
            case ".html":
                return "text/html; charset=utf-8";
            case ".js":
                return "application/javascript; charset=utf-8";
            case ".json":
                return "application/json; charset=utf-8";
            case ".png":
                return "image/png";
            case ".vert":
            case ".frag":
            case ".txt":
            case ".md":
                return "text/plain; charset=utf-8";
            case ".moc3":
            default:
                return "application/octet-stream";
        }
    }

    private static void Reject(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e)
    {
        e.Response = core.Environment.CreateWebResourceResponse(
            null,
            403,
            "Forbidden",
            "Content-Type: text/plain");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var allowedPrefix = Origin + "/";
        if (e.Uri != null && e.Uri.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
    }

    /// <summary>Only a real navigation failure is surfaced to the user; success is not an error.</summary>
    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            return;
        }

        ReportError("模型页面加载失败（" + e.WebErrorStatus + "），请重启应用后重试");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        ReportError("模型窗口异常退出，请重新显示悬浮窗");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // Only the viewer page itself may drive the host; any other frame or document is ignored.
        if (!string.Equals(e.Source, Origin + "/index.html", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (Exception)
        {
            return;
        }

        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        string? type = null;
        string? errorMessage = null;
        int reportedLoadId = 0;
        try
        {
            using var document = JsonDocument.Parse(message!);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (document.RootElement.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String)
            {
                type = typeElement.GetString();
            }

            if (document.RootElement.TryGetProperty("message", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String)
            {
                errorMessage = errorElement.GetString();
            }

            if (document.RootElement.TryGetProperty("loadId", out var loadIdElement) &&
                loadIdElement.ValueKind == JsonValueKind.Number)
            {
                loadIdElement.TryGetInt32(out reportedLoadId);
            }
        }
        catch (JsonException)
        {
            return;
        }

        switch (type)
        {
            case "viewer-ready":
                _pageReady = true;
                _ = SendAsync(new { type = _visible ? "resume" : "pause" });
                if (_activePackage != null)
                {
                    var package = _activePackage;
                    var loadId = _loadId;
                    _ = SendModelAsync(package, loadId);
                }

                break;
            case "ready":
                if (reportedLoadId != _loadId)
                {
                    // A model that finished loading after being superseded must not start sampling.
                    return;
                }

                _readyLoadId = reportedLoadId;
                StartSamplerIfReady();
                break;
            case "error":
                if (reportedLoadId != 0 && reportedLoadId != _loadId)
                {
                    return;
                }

                _readyLoadId = 0;
                _sampler.Stop();
                ReportError(string.IsNullOrWhiteSpace(errorMessage) ? "模型加载失败" : errorMessage!);
                break;
        }
    }

    private void ReportError(string message)
    {
        _sampler.Stop();
        ErrorOccurred?.Invoke(message);
    }

    private System.Threading.Tasks.Task SendAsync(object payload)
    {
        var core = _webView?.CoreWebView2;
        if (core == null)
        {
            return System.Threading.Tasks.Task.CompletedTask;
        }

        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch (Exception)
        {
            // The browser process may already be gone during shutdown.
        }

        return System.Threading.Tasks.Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadId++;
        _sampler.Dispose();

        if (_webView != null)
        {
            try
            {
                _webView.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"unload\"}");
            }
            catch (Exception)
            {
                // Ignore teardown races.
            }

            try
            {
                _webView.Dispose();
            }
            catch (Exception)
            {
                // Ignore teardown races.
            }

            Children.Remove(_webView);
            _webView = null;
        }

        _coreReady = false;
        _pageReady = false;
        _activePackage = null;
    }
}
