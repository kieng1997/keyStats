using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KeyStats.Models;
using KeyStats.Services;
using KeyStats.Views;
using KeyStats.Views.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Forms = System.Windows.Forms;

namespace DesktopPetSmoke
{
    /// <summary>
    /// Re-runnable acceptance smoke for the desktop pet.
    ///
    ///   DesktopPetSmoke.exe &lt;outputDir&gt; &lt;modelFolder&gt;
    ///
    /// It drives the real <see cref="FloatingStatsWindow"/> with injected settings, an isolated
    /// WebView2 profile and a scripted input reader, so no real statistics, settings or OS input are
    /// touched. Exit code 0 means every check passed; 1 means at least one check failed.
    /// </summary>
    public static class Program
    {
        private const int StepMilliseconds = 250;

        private static readonly List<string> Failures = new List<string>();


        private static string _outDir;
        private static string _modelRoot;
        private static AppSettings _settings;
        private static SettingsFile _settingsFile;
        private static FakeInput _input;
        private static FloatingStatsWindow _window;
        private static Backdrop _backdrop;
        private static DispatcherTimer _driver;
        private static CoreWebView2 _core;
        private static DispatcherTimer _poll;
        private static bool _pollStopped;
        private static JsonElement? _lastStats;
        private static int _step;
        private static bool _windowClosed;
        private static int _statsAfterHideFrame;
        private static long _readerCallsAtHide;

        [STAThread]
        public static int Main(string[] args)
        {
            _outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "keystats-pet-smoke");
            _modelRoot = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
            Directory.CreateDirectory(_outDir);
            Log.Path = Path.Combine(_outDir, "desktop-pet-smoke.log");
            Log.Write("START outDir=" + _outDir + " model=" + _modelRoot);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Fail("unhandled dispatcher exception: " + e.Exception.GetType().Name + ": " + e.Exception.Message);
                e.Handled = true;
            };

            try
            {
                if (string.IsNullOrWhiteSpace(_modelRoot) || !Directory.Exists(_modelRoot))
                {
                    Fail("model folder argument is required: " + (_modelRoot ?? "<null>"));
                    return Finish(app, 1);
                }

                _settingsFile = new SettingsFile(Path.Combine(_outDir, "settings.json"));
                _settings = _settingsFile.Load();
                _settingsFile.Current = _settings;
                // The production window restores its saved position; place it inside the capture
                // region the smoke composites behind it.
                _settings.FloatingStatsLeft = 70;
                _settings.FloatingStatsTop = 70;
                _settings.FloatingStatsPositionLocked = true;
                _input = new FakeInput();

                _backdrop = new Backdrop("#F2F4F8");
                _backdrop.Show();

                _window = new FloatingStatsWindow(
                    _settings,
                    _settingsFile.Save,
                    Path.Combine(_outDir, "webview-profile"),
                    _input.ReadFrame,
                    () => _modelRoot);
                _window.Owner = _backdrop;
                _window.Show();

                _driver = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StepMilliseconds) };
                _driver.Tick += (s, e) => Drive();
                _driver.Start();

                app.Run();
            }
            catch (Exception exception)
            {
                Fail("fatal: " + exception);
            }
            finally
            {
                _driver?.Stop();
            }

            return Finish(app, Failures.Count == 0 ? 0 : 1);
        }

        private static int Finish(Application app, int exitCode)
        {
            try { _driver?.Stop(); } catch { }
            try { _poll?.Stop(); } catch { }
            _pollStopped = true;
            try { _core = null; } catch { }
            try { _window?.Close(); } catch (Exception exception) { Log.Write("close: " + exception.Message); }
            try { _backdrop?.Close(); } catch { }

            // Each Check already logged its own line; print only the totals here.
            Log.Write("CHECKS=" + _checkCount + " FAILURES=" + Failures.Count);
            Log.Write("EXIT code=" + exitCode + " failures=" + Failures.Count);
            foreach (var failure in Failures)
            {
                Log.Write("FAIL: " + failure);
            }

            try { app.Shutdown(); } catch { }

            // The WebView2 browser process can outlive the WPF shutdown; the smoke must always
            // return a definite exit code rather than hang.
            Environment.Exit(exitCode);
            return exitCode;
        }

        private static void Drive()
        {
            try
            {
                _step++;
                switch (_step)
                {
                    case 2:
                        HookPageObservers();
                        break;
                    case 6:
                        // Empty state must offer a chooser, not a broken pet.
                        Check("empty state shows chooser button",
                            _window.FindName("ChooseModelButton") is System.Windows.Controls.Button button &&
                            button.Visibility == Visibility.Visible);
                        ClickChooseModel();
                        break;
                    case 12:
                        Check("settings persisted the chosen package root",
                            string.Equals(_settings.DesktopPetModelPath, _modelRoot, StringComparison.OrdinalIgnoreCase));
                        Check("settings file on disk has the model path",
                            _settingsFile.ContainsText(_modelRoot));
                        break;
                    case 16:
                        WaitForModelReady();
                        break;
                    case 20:
                        ExpectStats("initial");
                        Capture("shot-light.png");
                        break;
                    case 23:
                        _backdrop.SetColor("#1B1F27");
                        break;
                    case 25:
                        Capture("shot-dark.png");
                        _backdrop.SetColor("#F2F4F8");
                        break;
                    case 27:
                        // Global desktop mapping: centre of the virtual desktop -> parameter origin.
                        var desktop = Forms.SystemInformation.VirtualScreen;
                        _input.Set(
                            new int[0],
                            desktop.Left + desktop.Width / 2,
                            desktop.Top + desktop.Height / 2);
                        break;
                    case 30:
                        CheckMouseMapping("centre", 0.0, 0.0, 0.02);
                        Capture("shot-mouse-centre.png");
                        break;
                    case 32:
                        var d = Forms.SystemInformation.VirtualScreen;
                        _input.Set(new int[0], d.Left, d.Top);
                        break;
                    case 35:
                        // Top-left of the desktop: X at its minimum, Y at its maximum (Live2D is Y-up).
                        CheckMouseMapping("top-left", -30.0, 30.0, 0.1);
                        break;
                    case 37:
                        var corner = Forms.SystemInformation.VirtualScreen;
                        _input.Set(new int[0], corner.Right - 1, corner.Bottom - 1);
                        break;
                    case 40:
                        CheckMouseMapping("bottom-right", 30.0, -30.0, 0.1);
                        break;
                    case 42:
                        _input.Set(new[] { 65 }, ScreenCenterX(), ScreenCenterY());
                        break;
                    case 45:
                        ExpectHand("A held", "img/standard/hand/0.png", 1);
                        Capture("shot-key-a.png");
                        break;
                    case 47:
                        // A is still held; D is pressed on top of it and must win.
                        _input.Set(new[] { 65, 68 }, ScreenCenterX(), ScreenCenterY());
                        break;
                    case 50:
                        ExpectHand("A+D held", "img/standard/hand/1.png", 1);
                        Capture("shot-key-ad.png");
                        break;
                    case 52:
                        _input.Set(new[] { 65 }, ScreenCenterX(), ScreenCenterY());
                        break;
                    case 55:
                        ExpectHand("back to A", "img/standard/hand/0.png", 1);
                        break;
                    case 57:
                        _input.Set(new int[0], ScreenCenterX(), ScreenCenterY());
                        break;
                    case 60:
                        ExpectHand("all released", null, 0);
                        break;
                    case 62:
                        _input.SetMouseButtons(left: true, right: true);
                        break;
                    case 65:
                        ExpectMouseButtons("both buttons", 1, 1);
                        break;
                    case 67:
                        _input.SetMouseButtons(left: false, right: false);
                        break;
                    case 70:
                        ExpectMouseButtons("buttons released", 0, 0);
                        _window.Hide();
                        Log.Write("HIDE");
                        break;
                    case 74:
                        // Hide has settled; take the baseline after the transition, then change the
                        // scripted input to something clearly different while hidden.
                        _readerCallsAtHide = _input.ReadCount;
                        _statsAfterHideFrame = CurrentFrame();
                        _input.Set(new[] { 69 }, (int)(ScreenWidth() * 0.25), (int)(ScreenHeight() * 0.25));
                        Log.Write("HIDDEN input changed to E, baseline frame=" + _statsAfterHideFrame);
                        break;
                    case 78:
                        Check("hidden window does not read the input source",
                            _input.ReadCount == _readerCallsAtHide,
                            "reads went " + _readerCallsAtHide + " -> " + _input.ReadCount);
                        Check("hidden window reports it is not rendering",
                            _lastStats.HasValue && !_lastStats.Value.GetProperty("running").GetBoolean(),
                            _lastStats.HasValue ? "running=" + _lastStats.Value.GetProperty("running") : "no stats");
                        Check("hidden window frame count stays stable",
                            CurrentFrame() == _statsAfterHideFrame,
                            "frame " + _statsAfterHideFrame + " -> " + CurrentFrame());
                        break;
                    case 80:
                        _window.Show();
                        Log.Write("SHOW");
                        break;
                    case 84:
                        Check("showing again resumes reading the input source",
                            _input.ReadCount > _readerCallsAtHide);
                        Check("showing again resumes rendering",
                            CurrentFrame() > _statsAfterHideFrame,
                            "frame " + _statsAfterHideFrame + " -> " + CurrentFrame());
                        break;
                    case 88:
                        // The model loaded before hide is still the ready one, and E was never sent
                        // while hidden; the next sampled frame must carry the new key.
                        ExpectHand("after show picks up new key", "img/standard/hand/2.png", 1);
                        Capture("shot-after-show.png");
                        break;
                    case 90:
                        _input.Set(new int[0], ScreenCenterX(), ScreenCenterY());
                        break;
                    case 94:
                        // Probe an external origin and wait for the verdict before closing; the
                        // production handler must answer it locally with 403.
                        ProbeExternalOriginAsync();
                        break;
                    case 96:
                        if (_externalProbeDone && _capturesPending == 0)
                        {
                            StopObservers();
                            _window.Close();
                            _windowClosed = true;
                            _backdrop.Close();
                        }
                        else
                        {
                            // Stay on this step until the probe finished (bounded by ProbeTimeout).
                            _step--;
                        }
                        break;
                    case 98:
                        Check("window closed cleanly", _windowClosed);
                        Check("no late input reads after close", _input.ReadCount == _input.ReadCountAtClose);
                        _driver.Stop();
                        // Explicit, unconditional shutdown: the run must never hang on a leftover
                        // window or timer.
                        Application.Current.Shutdown();
                        break;
                }
            }
            catch (Exception exception)
            {
                Fail("driver step " + _step + ": " + exception);
                _driver.Stop();
                Application.Current.Shutdown();
            }
        }

        private static int ScreenWidth()
        {
            return Forms.SystemInformation.VirtualScreen.Width;
        }

        private static int ScreenCenterX() { var d = Forms.SystemInformation.VirtualScreen; return d.Left + d.Width / 2; }
        private static int ScreenCenterY() { var d = Forms.SystemInformation.VirtualScreen; return d.Top + d.Height / 2; }
        private static int ScreenHeight()
        {
            return Forms.SystemInformation.VirtualScreen.Height;
        }

        private static void HookPageObservers()
        {
            var webView = FindWebView(_window);
            if (webView == null)
            {
                Fail("webview not found in the production window");
                return;
            }

            void Hook()
            {
                _core = webView.CoreWebView2;
                if (_core == null) return;
                _core.WebMessageReceived += (s, e) =>
                {
                    var raw = e.TryGetWebMessageAsString();
                    if (raw == null || !raw.Contains("\"stats\"")) return;
                    try
                    {
                        using var document = JsonDocument.Parse(raw);
                        _lastStats = document.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                    }
                };
                // Ask the page for a stats snapshot so the smoke can read real parameter values.
                _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _poll.Tick += (s, e) =>
                {
                    if (_pollStopped || _core == null) return;
                    try
                    {
                        _core.PostWebMessageAsJson("{\"type\":\"stats\"}");
                    }
                    catch (Exception)
                    {
                        // The control may already be disposed during teardown.
                    }
                };
                _poll.Start();
            }

            if (webView.CoreWebView2 != null) Hook();
            else webView.CoreWebView2InitializationCompleted += (s, e) => Hook();
        }

        private static void ClickChooseModel()
        {
            var button = _window.FindName("ChooseModelButton") as System.Windows.Controls.Button;
            if (button == null)
            {
                Fail("choose model button missing");
                return;
            }

            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Log.Write("clicked choose model");
        }

        private static void WaitForModelReady()
        {
            var stats = _lastStats;
            Check("viewer reported a loaded model",
                stats.HasValue && stats.Value.TryGetProperty("modelLoaded", out var loaded) && loaded.GetBoolean(),
                stats.HasValue ? stats.Value.ToString() : "no stats");
            var hands = stats.HasValue && stats.Value.TryGetProperty("handsLoaded", out var h) ? h.GetInt32() : -1;
            Check("all 17 configured hand overlays preloaded", hands == 17, "handsLoaded=" + hands);
            var modelCanvas = stats.HasValue && stats.Value.TryGetProperty("params", out var p) &&
                              p.ValueKind == JsonValueKind.Object;
            Check("viewer exposes real Core parameters", modelCanvas);
        }

        private static void ExpectStats(string label)
        {
            var stats = _lastStats;
            if (!stats.HasValue)
            {
                Fail(label + ": no stats received");
                return;
            }

            Check(label + ": model loaded", stats.Value.GetProperty("modelLoaded").GetBoolean());
            Check(label + ": rendering", stats.Value.GetProperty("running").GetBoolean());
        }

        private static void ExpectHand(string label, string expectedImage, int expectedLeftHandDown)
        {
            var stats = _lastStats;
            if (!stats.HasValue)
            {
                Fail(label + ": no stats received");
                return;
            }

            var hand = stats.Value.GetProperty("handImage");
            var actualHand = hand.ValueKind == JsonValueKind.Null ? null : hand.GetString();
            var leftHand = stats.Value.GetProperty("params").GetProperty("CatParamLeftHandDown").GetDouble();
            Check(label + ": visible hand overlay",
                string.Equals(actualHand, expectedImage, StringComparison.Ordinal),
                "expected " + (expectedImage ?? "<base>") + " got " + (actualHand ?? "<base>"));
            Check(label + ": CatParamLeftHandDown",
                Math.Abs(leftHand - expectedLeftHandDown) < 0.001,
                "expected " + expectedLeftHandDown + " got " + leftHand);
        }

        private static void ExpectMouseButtons(string label, int left, int right)
        {
            var stats = _lastStats;
            if (!stats.HasValue)
            {
                Fail(label + ": no stats received");
                return;
            }

            var parameters = stats.Value.GetProperty("params");
            Check(label + ": ParamMouseLeftDown",
                Math.Abs(parameters.GetProperty("ParamMouseLeftDown").GetDouble() - left) < 0.001);
            Check(label + ": ParamMouseRightDown",
                Math.Abs(parameters.GetProperty("ParamMouseRightDown").GetDouble() - right) < 0.001);
        }

        private static void CheckMouseMapping(string label, double expectedX, double expectedY, double tolerance)
        {
            var stats = _lastStats;
            if (!stats.HasValue)
            {
                Fail(label + ": no stats received");
                return;
            }

            var parameters = stats.Value.GetProperty("params");
            var x = parameters.GetProperty("ParamMouseX").GetDouble();
            var y = parameters.GetProperty("ParamMouseY").GetDouble();
            Check(label + ": ParamMouseX follows the desktop", Math.Abs(x - expectedX) <= tolerance,
                "expected " + expectedX + " got " + x);
            Check(label + ": ParamMouseY follows the desktop", Math.Abs(y - expectedY) <= tolerance,
                "expected " + expectedY + " got " + y);
        }

        private static bool _externalProbeDone;
        private static int _externalProbeHits;
        private static int _checkCount;
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

        /// <summary>
        /// Verifies the production WebResourceRequested handler refuses an external origin locally.
        /// A production observer (registered by <see cref="HookPageObservers"/>) records every
        /// request the handler answers; the page probe confirms the fetch promise rejects. The
        /// probe target is a reserved .invalid host, so nothing leaves the machine either way.
        /// </summary>
        private static async void ProbeExternalOriginAsync()
        {
            if (_core == null)
            {
                Fail("external probe: no core");
                _externalProbeDone = true;
                return;
            }

            var core = _core;
            core.WebResourceRequested += OnProbeResourceRequested;
            try
            {
                await core.ExecuteScriptAsync(
                    "window.__ext='pending';" +
                    "fetch('https://keystats-pet-check.invalid/never.txt')" +
                    ".then(function(r){window.__ext='REACHED '+r.status;})" +
                    ".catch(function(){window.__ext='REFUSED';});");
                var deadline = DateTime.UtcNow + ProbeTimeout;
                string value;
                do
                {
                    await Task.Delay(100);
                    value = (await core.ExecuteScriptAsync("window.__ext")).Trim('"');
                } while (value == "pending" && DateTime.UtcNow < deadline);
                Check("external origin fetch is refused by the page", value == "REFUSED", "state=" + value);
                Check("external origin request hit the production handler and was refused locally",
                    _externalProbeHits > 0, "handler saw " + _externalProbeHits + " request(s) with 403");
            }
            catch (Exception error)
            {
                Fail("external probe: " + error.Message);
            }
            finally
            {
                core.WebResourceRequested -= OnProbeResourceRequested;
                _externalProbeDone = true;
            }
        }

        private static void OnProbeResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                if (new Uri(e.Request.Uri).Host != "keystats-pet-check.invalid")
                {
                    return;
                }

                // The production handler answers first; this observer only records what it decided.
                var status = e.Response?.StatusCode ?? 0;
                Log.Write("EXTERNAL handler response status=" + status + " uri=" + e.Request.Uri);
                if (status == 403)
                {
                    _externalProbeHits++;
                }
            }
            catch (Exception exception)
            {
                Log.Write("EXTERNAL observer error: " + exception.Message);
            }
        }

        private static void StopObservers()
        {
            // Tear down the smoke's own polling before the production window is closed, so no
            // late callback of ours can touch a disposed control.
            _pollStopped = true;
            if (_poll != null)
            {
                _poll.Stop();
                _poll = null;
            }

            _input.ReadCountAtClose = _input.ReadCount;
        }

        private static int CurrentFrame()
        {
            var stats = _lastStats;
            if (!stats.HasValue) return -1;
            return stats.Value.TryGetProperty("frame", out var frame) ? frame.GetInt32() : -1;
        }

        private static int _capturesPending;

        private static async void Capture(string name)
        {
            _capturesPending++;
            try
            {
                var color = ((SolidColorBrush)_backdrop.Background).Color;
                using var stream = new MemoryStream();
                await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                stream.Position = 0;
                using var pet = new Bitmap(stream);
                using var bitmap = new Bitmap(420, 440);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(System.Drawing.Color.FromArgb(color.R, color.G, color.B));
                    graphics.DrawImage(pet, new Rectangle(34, 34, 340, 340));
                }
                bitmap.Save(Path.Combine(_outDir, name), ImageFormat.Png);
                Log.Write("SHOT " + name + " (off-screen WebView2 capture with synthetic background)");
            }
            catch (Exception exception)
            {
                Fail("capture " + name + ": " + exception.Message);
            }
            finally { _capturesPending--; }
        }

        private static void Check(string label, bool condition, string detail = null)
        {
            _checkCount++;
            var line = (condition ? "PASS " : "FAIL ") + label + (detail != null ? " (" + detail + ")" : string.Empty);
            Log.Write(line);
            if (!condition)
            {
                Failures.Add(label);
            }
        }

        private static void Fail(string message)
        {
            Failures.Add(message);

            Log.Write("FAIL " + message);
        }

        private static WebView2CompositionControl FindWebView(DependencyObject root)
        {
            if (root == null) return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is WebView2CompositionControl control) return control;
                var found = FindWebView(child);
                if (found != null) return found;
            }

            return null;
        }
    }

    internal static class Log
    {
        public static string Path = "desktop-pet-smoke.log";

        public static void Write(string message)
        {
            try
            {
                File.AppendAllText(Path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }
    }

    /// <summary>Scripted input source; the sampler reads it instead of the real keyboard/cursor.</summary>
    internal sealed class FakeInput
    {
        private int[] _keys = new int[0];
        private int _cursorX;
        private int _cursorY;
        private bool _left;
        private bool _right;

        public int ReadCount { get; private set; }
        public int ReadCountAtClose { get; set; }

        public void Set(IEnumerable<int> keys, int cursorX, int cursorY)
        {
            _keys = new List<int>(keys).ToArray();
            _cursorX = cursorX;
            _cursorY = cursorY;
        }

        public void SetMouseButtons(bool left, bool right)
        {
            _left = left;
            _right = right;
        }

        public DesktopPetInputFrame ReadFrame(IReadOnlyList<int> configuredKeys)
        {
            ReadCount++;

            // Only report keys the window actually asked about, like the real sampler does.
            var held = new List<int>();
            for (var i = 0; i < _keys.Length; i++)
            {
                for (var j = 0; j < configuredKeys.Count; j++)
                {
                    if (configuredKeys[j] == _keys[i])
                    {
                        held.Add(_keys[i]);
                        break;
                    }
                }
            }

            return new DesktopPetInputFrame(held, _cursorX, _cursorY, _left, _right);
        }
    }

    /// <summary>Isolated settings file so the smoke never touches the real LOCALAPPDATA data.</summary>
    internal sealed class SettingsFile
    {
        private readonly string _path;

        public SettingsFile(string path)
        {
            _path = path;
        }

        public AppSettings Load()
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }

        /// <summary>The window mutates the settings instance it was given; persist that instance.</summary>
        public AppSettings Current { get; set; }

        public void Save()
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(Current));
        }

        public bool ContainsText(string text)
        {
            if (!File.Exists(_path)) return false;
            // Compare the deserialized value: the raw JSON escapes Windows path separators.
            var reloaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path));
            return reloaded != null &&
                   string.Equals(reloaded.DesktopPetModelPath, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class Backdrop : Window
    {
        public Backdrop(string color)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            Background = (SolidColorBrush)new BrushConverter().ConvertFromString(color);
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Width = 420;
            Height = 440;
            Left = 40;
            Top = 40;
        }

        public void SetColor(string color)
        {
            Background = (SolidColorBrush)new BrushConverter().ConvertFromString(color);
        }
    }
}
