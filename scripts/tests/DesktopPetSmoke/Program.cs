using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KeyStats.Helpers;
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

        // Preset ids must stay in lockstep with the production window's fixed options.
        private const string DsPresetId = "ds-standard";
        private const string NailinPresetId = "nailin-keyboard";

        [STAThread]
        public static int Main(string[] args)
        {
            _outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "keystats-pet-smoke");
            _modelRoot = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
            Directory.CreateDirectory(_outDir);
            Log.Path = Path.Combine(_outDir, "desktop-pet-smoke.log");
            Log.Write("START outDir=" + _outDir + " model=" + _modelRoot);

            Application app = null;
            try
            {
                // Plain Application: no production KeyStats.App instance may ever be constructed
                // or Run in the smoke. The trusted repository App.xaml's Application.Resources
                // dictionary is parsed (XDocument -> XamlReader) into this app's Resources, so
                // the REAL production styles/theme brushes are tested while OnStartup/OnExit and
                // every real service stay out of the process entirely.
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(LoadProductionAppResources());
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Fail("unhandled dispatcher exception: " + e.Exception.GetType().Name + ": " + e.Exception.Message);
                    e.Handled = true;
                };

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

        /// <summary>
        /// Loads the production App.xaml's Application.Resources into the plain smoke Application:
        /// a trusted-repo XDocument extraction (namespace attributes copied, clr-namespace values
        /// pinned to assembly=KeyStats, x:Class dropped) parsed by XamlReader with the production
        /// component BaseUri. Styles/Converters/Brushes only — no events, no x:Shared, no external
        /// Source — and no KeyStats.App instance is ever constructed.
        /// </summary>
        private static ResourceDictionary LoadProductionAppResources()
        {
            // bin/Debug/net48 -> up 6 levels reaches the repo root.
            var repoRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", ".."));
            var appXamlPath = Path.Combine(repoRoot, "KeyStats.Windows", "KeyStats", "App.xaml");
            Check("production App.xaml found at the harness-relative path", File.Exists(appXamlPath),
                appXamlPath);

            const string WpfNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var document = System.Xml.Linq.XDocument.Load(appXamlPath);
            var resources = document.Root.Element(XName.Get("Application.Resources", WpfNs))
                ?? throw new Exception("App.xaml has no Application.Resources element");
            var dictionary = resources.Element(XName.Get("ResourceDictionary", WpfNs))
                ?? throw new Exception("App.xaml Application.Resources has no ResourceDictionary");

            foreach (var attribute in document.Root.Attributes())
            {
                if (attribute.Name.LocalName == "Class" && attribute.Name.NamespaceName ==
                    "http://schemas.microsoft.com/winfx/2006/xaml") continue;
                if (!attribute.IsNamespaceDeclaration)
                {
                    continue;
                }

                // Loose clr-namespace values only resolve in loose XamlReader when the assembly
                // is pinned (the production compile injects it; the parser cannot guess it).
                var value = attribute.Value;
                if (value.StartsWith("clr-namespace:", StringComparison.Ordinal) &&
                    !value.Contains(";assembly="))
                {
                    value += ";assembly=KeyStats";
                }

                dictionary.SetAttributeValue(attribute.Name, value);
            }

            // Namespace declarations alone do not re-bind descendant element names; those still
            // carry the original loose clr-namespace and would serialize (and parse) un-pinned.
            foreach (var element in dictionary.DescendantsAndSelf())
            {
                var namespaceName = element.Name.NamespaceName;
                if (namespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal) &&
                    !namespaceName.Contains(";assembly="))
                {
                    element.Name = XName.Get(element.Name.LocalName, namespaceName + ";assembly=KeyStats");
                }
            }

            // XDocument resolves character references into raw characters, and Parse(string)
            // rejects raw non-ASCII text ("invalid character in given encoding"), so re-escape
            // everything outside ASCII back into character references.
            var xaml = new StringBuilder();
            foreach (var character in dictionary.ToString())
            {
                if (character > 0x7E)
                    xaml.Append("&#x").Append(((int)character).ToString("X4")).Append(';');
                else
                    xaml.Append(character);
            }

            var parsed = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(
                xaml.ToString(),
                new System.Windows.Markup.ParserContext
                {
                    BaseUri = new Uri("pack://application:,,,/KeyStats;component/App.xaml")
                });
            Check("production App.xaml resource dictionary parsed",
                parsed != null && parsed.Count > 0, "entries=" + (parsed?.Count ?? 0));
            return parsed;
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

            try { app?.Shutdown(); } catch { }

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
                        CheckProductionMenuEntryVisible();
                        break;
                    case 6:
                        // First launch defaults to the bundled DS preset; no chooser, no picker.
                        Check("first launch hides the chooser (bundled DS loads by default)",
                            _window.FindName("ChooseModelButton") is System.Windows.Controls.Button button &&
                            button.Visibility != Visibility.Visible);
                        break;
                    case 12:
                        // The smoke does not pick a folder any more; nothing may auto-persist a path.
                        Check("no model path auto-persisted on first launch",
                            string.IsNullOrEmpty(_settings.DesktopPetModelPath),
                            "path=" + (_settings.DesktopPetModelPath ?? "<null>"));
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
                        // Preset switch through the real context-menu items, not private calls.
                        SelectPresetMenuItem("乃琳-键盘模型");
                        break;
                    case 98:
                        Check("nailin preset saved as an id, not a path",
                            string.Equals(_settings.DesktopPetPresetId, NailinPresetId, StringComparison.Ordinal) &&
                            string.IsNullOrEmpty(_settings.DesktopPetModelPath),
                            "preset=" + (_settings.DesktopPetPresetId ?? "<null>") +
                            " path=" + (_settings.DesktopPetModelPath ?? "<null>"));
                        Check("settings file on disk stores the preset id",
                            _settingsFile.ContainsText("nailin-keyboard") &&
                            !_settingsFile.ContainsText("Models\\\\"));
                        break;
                    case 102:
                        WaitForNailinReady();
                        break;
                    case 106:
                        Capture("shot-nailin-light.png");
                        break;
                    case 108:
                        _backdrop.SetColor("#1B1F27");
                        break;
                    case 110:
                        Capture("shot-nailin-dark.png", ExpectKeyboardPixel);
                        _backdrop.SetColor("#F2F4F8");
                        break;
                    case 112:
                        _input.Set(new[] { 65, 13 }, ScreenCenterX(), ScreenCenterY());
                        break;
                    case 115:
                        ExpectBothHands(
                            "A+Return both hands",
                            "resources/left-keys/KeyA.png",
                            "resources/right-keys/Return.png",
                            1, 1);
                        Capture("shot-nailin-hands.png");
                        break;
                    case 117:
                        _input.Set(new[] { 65 }, ScreenCenterX(), ScreenCenterY());
                        break;
                    case 120:
                        ExpectBothHands(
                            "Return released recovers right only",
                            "resources/left-keys/KeyA.png",
                            null,
                            1, 0);
                        break;
                    case 122:
                        _input.Set(new int[0], ScreenCenterX(), ScreenCenterY());
                        break;
                    case 125:
                        ExpectBothHands("all keys released recovers base", null, null, 0, 0);
                        break;
                    case 127:
                        Check("menu checks follow the active preset",
                            GetPresetMenuItemChecked("DS-标准模式") == false &&
                            GetPresetMenuItemChecked("乃琳-键盘模型") == true,
                            "ds=" + GetPresetMenuItemChecked("DS-标准模式") +
                            " nailin=" + GetPresetMenuItemChecked("乃琳-键盘模型"));
                        SelectPresetMenuItem("DS-标准模式");
                        break;
                    case 131:
                        Check("switch back to DS persisted",
                            string.Equals(_settings.DesktopPetPresetId, DsPresetId, StringComparison.Ordinal) &&
                            string.IsNullOrEmpty(_settings.DesktopPetModelPath),
                            "preset=" + (_settings.DesktopPetPresetId ?? "<null>"));
                        break;
                    case 135:
                        // Nothing is held, so the base hand shows; the hands count proves which
                        // model is serving stats (17 = DS, 60 = nailin).
                        ExpectHand("DS model back after switch", null, 0);
                        Check("DS hands loaded after switch back",
                            LastStatsHands() == 17, "handsLoaded=" + LastStatsHands());
                        // Restart the window from the persisted settings file to prove recovery.
                        RestartWindowFromSettings();
                        break;
                    case 139:
                        WaitForHandsLoaded(17, "recreated window restored DS from settings");
                        Check("recreated window kept the preset id",
                            string.Equals(_settings.DesktopPetPresetId, DsPresetId, StringComparison.Ordinal),
                            "preset=" + (_settings.DesktopPetPresetId ?? "<null>"));
                        SelectPresetMenuItem("乃琳-键盘模型");
                        break;
                    case 143:
                        WaitForNailinReady();
                        break;
                    case 147:
                        // Restart again: the recreated window must restore nailin from settings.
                        RestartWindowFromSettings();
                        break;
                    case 151:
                        WaitForHandsLoaded(60, "recreated window restored nailin from settings file");
                        Check("recreated window preset id is nailin",
                            string.Equals(_settings.DesktopPetPresetId, NailinPresetId, StringComparison.Ordinal),
                            "preset=" + (_settings.DesktopPetPresetId ?? "<null>"));
                        _input.Set(new int[0], ScreenCenterX(), ScreenCenterY());
                        break;
                    case 155:
                        // Probe an external origin and wait for the verdict before closing; the
                        // production handler must answer it locally with 403.
                        ProbeExternalOriginAsync();
                        break;
                    case 157:
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
                    case 159:
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

        /// <summary>
        /// Verifies the two fixed preset entries are REAL user-visible entries of the production
        /// context menu: opened, template-applied, laid out, enabled and on screen in the current
        /// light theme. Raising Click events alone proves nothing about what a user can see.
        /// </summary>
        private static void CheckProductionMenuEntryVisible()
        {
            var menu = _window.FindName("RootBorder") is System.Windows.Controls.Border border
                ? border.ContextMenu
                : null;
            Check("production context menu exists on RootBorder", menu != null);
            if (menu == null) return;

            menu.PlacementTarget = _window;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
            DoEvents();

            Check("production context menu opens", menu.IsOpen);
            foreach (var header in new[] { "DS-标准模式", "乃琳-键盘模型" })
            {
                var item = FindPresetMenuItem(header);
                Check("preset entry in the ROOT menu: " + header, item != null);
                if (item == null) continue;
                item.ApplyTemplate();
                _window.UpdateLayout();
                DoEvents();
                Check("preset entry visible in production template: " + header,
                    item.IsVisible && item.IsEnabled && item.ActualWidth > 0 && item.ActualHeight > 0,
                    "visible=" + item.IsVisible + " enabled=" + item.IsEnabled +
                    " size=" + item.ActualWidth + "x" + item.ActualHeight);
            }

            CaptureMenu("shot-menu-light.png");
            ApplySmokeTheme(dark: true);
            _window.UpdateLayout();
            DoEvents();
            Check("preset entries still visible in dark theme",
                AllPresetEntriesVisible(out var detail), detail);
            CaptureMenu("shot-menu-dark.png");
            ApplySmokeTheme(dark: false);
            _window.UpdateLayout();
            menu.IsOpen = false;
            DoEvents();
        }

        private static bool AllPresetEntriesVisible(out string detail)
        {
            var ok = true;
            var parts = new List<string>();
            foreach (var header in new[] { "DS-标准模式", "乃琳-键盘模型" })
            {
                var item = FindPresetMenuItem(header);
                var visible = item != null && item.IsVisible && item.IsEnabled &&
                              item.ActualWidth > 0 && item.ActualHeight > 0;
                ok &= visible;
                parts.Add(header + " visible=" + (visible ? "true" : "false"));
            }

            detail = string.Join(", ", parts);
            return ok;
        }

        /// <summary>
        /// Switches theme through the same private ThemeManager seams the heatmap rendering smoke
        /// uses: test-process resources only, never the OS setting or user preferences.
        /// </summary>
        private static void ApplySmokeTheme(bool dark)
        {
            var manager = ThemeManager.Instance;
            typeof(ThemeManager)
                .GetProperty("IsDarkTheme", System.Reflection.BindingFlags.Instance |
                                              System.Reflection.BindingFlags.Public |
                                              System.Reflection.BindingFlags.NonPublic)!
                .GetSetMethod(true)!.Invoke(manager, new object[] { dark });
            typeof(ThemeManager)
                .GetMethod("ApplyTheme", System.Reflection.BindingFlags.Instance |
                                         System.Reflection.BindingFlags.NonPublic)!
                .Invoke(manager, null);
            _window.UpdateLayout();
            DoEvents();
        }

        /// <summary>Renders the open context menu itself to a PNG via WPF's own RTB.</summary>
        private static void CaptureMenu(string name)
        {
            try
            {
                var menu = (_window.FindName("RootBorder") as System.Windows.Controls.Border)?.ContextMenu;
                if (menu == null || !menu.IsOpen)
                {
                    Fail("menu capture " + name + ": menu not open");
                    return;
                }

                _window.UpdateLayout();
                DoEvents();
                double width = menu.ActualWidth;
                double height = menu.ActualHeight;
                if (width < 1 || height < 1)
                {
                    Fail("menu capture " + name + ": menu has no rendered size " + width + "x" + height);
                    return;
                }

                var rtb = new RenderTargetBitmap(
                    (int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
                rtb.Render(menu);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using var stream = File.Create(Path.Combine(_outDir, name));
                encoder.Save(stream);
                Log.Write("MENU SHOT " + name + " (" + (int)width + "x" + (int)height + ")");
            }
            catch (Exception exception)
            {
                Fail("menu capture " + name + ": " + exception.Message);
            }
        }

        private static int ScreenWidth()
        {
            return Forms.SystemInformation.VirtualScreen.Width;
        }

        /// <summary>Clicks a preset entry in the production root context menu by its Chinese header.</summary>
        private static void SelectPresetMenuItem(string header)
        {
            var item = FindPresetMenuItem(header);
            Check("preset menu item exists: " + header, item != null);
            if (item == null) return;
            // MenuItem has its own ClickEvent (it does not inherit ButtonBase); raising the button
            // event would silently do nothing.
            item.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            Log.Write("clicked preset " + header);
        }

        private static bool GetPresetMenuItemChecked(string header)
        {
            var item = FindPresetMenuItem(header);
            return item != null && item.IsChecked;
        }

        private static System.Windows.Controls.MenuItem? FindPresetMenuItem(string header)
        {
            var menu = _window.FindName("RootBorder") is System.Windows.Controls.Border border
                ? border.ContextMenu
                : null;
            if (menu == null) return null;
            // The fixed presets are flattened root-level leaf items; a nested "模型" submenu
            // would not render under the production MenuItem template (no ItemsPresenter).
            foreach (var top in menu.Items)
            {
                if (top is System.Windows.Controls.MenuItem item &&
                    string.Equals(item.Header as string, header, StringComparison.Ordinal))
                {
                    return item;
                }
            }

            return null;
        }

        private static int LastStatsHands()
        {
            return _lastStats.HasValue && _lastStats.Value.TryGetProperty("handsLoaded", out var hands)
                ? hands.GetInt32() : -1;
        }

        private static void WaitForNailinReady()
        {
            Check("nailin viewer reported a loaded model", LastStatsModelLoaded());
            Check("all 60 nailin hand overlays preloaded", LastStatsHands() == 60,
                "handsLoaded=" + LastStatsHands());
        }

        /// <summary>
        /// Waits (bounded) for the recreated window's viewer to reach the expected overlay count;
        /// a restart needs a fresh WebView2 boot before stats flow again.
        /// </summary>
        private static void WaitForHandsLoaded(int expected, string label)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (LastStatsHands() != expected && DateTime.UtcNow < deadline)
            {
                // Drive() runs on the dispatcher; blocking here would freeze it, so poll coarsely.
                DoEvents();
            }

            Check(label + " (handsLoaded=" + expected + ")", LastStatsHands() == expected,
                "handsLoaded=" + LastStatsHands() + " modelLoaded=" + LastStatsModelLoaded());
        }

        private static void DoEvents()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(100);
        }

        private static bool LastStatsModelLoaded()
        {
            return _lastStats.HasValue && _lastStats.Value.TryGetProperty("modelLoaded", out var loaded) &&
                   loaded.GetBoolean();
        }

        /// <summary>
        /// Closes the window and rebuilds it from the persisted settings file, proving a restart
        /// restores the chosen preset without any picker.
        /// </summary>
        private static void RestartWindowFromSettings()
        {
            StopObservers();
            _window.Close();

            // Reload exactly what a fresh process would read from disk.
            _settings = _settingsFile.Reload();
            _settingsFile.Current = _settings;
            _settings.FloatingStatsLeft = 70;
            _settings.FloatingStatsTop = 70;
            _settings.FloatingStatsPositionLocked = true;
            _input = new FakeInput();
            _window = new FloatingStatsWindow(
                _settings,
                _settingsFile.Save,
                Path.Combine(_outDir, "webview-profile"),
                _input.ReadFrame,
                () => _modelRoot);
            _window.Owner = _backdrop;
            _window.Show();
            HookPageObservers();
            Log.Write("RESTART window from settings file");
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
                _pollStopped = false; // a restart re-hooks after StopObservers; re-arm the poll
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

        private static void ExpectBothHands(
            string label, string expectedLeftImage, string expectedRightImage,
            int expectedLeftDown, int expectedRightDown)
        {
            var stats = _lastStats;
            if (!stats.HasValue)
            {
                Fail(label + ": no stats received");
                return;
            }

            var leftImage = stats.Value.GetProperty("handImage");
            var actualLeft = leftImage.ValueKind == JsonValueKind.Null ? null : leftImage.GetString();
            var rightImage = stats.Value.GetProperty("rightHandImage");
            var actualRight = rightImage.ValueKind == JsonValueKind.Null ? null : rightImage.GetString();
            var parameters = stats.Value.GetProperty("params");
            var leftDown = parameters.GetProperty("CatParamLeftHandDown").GetDouble();
            var rightDown = parameters.GetProperty("CatParamRightHandDown").GetDouble();

            Check(label + ": left overlay",
                string.Equals(actualLeft, expectedLeftImage, StringComparison.Ordinal),
                "expected " + (expectedLeftImage ?? "<base>") + " got " + (actualLeft ?? "<base>"));
            Check(label + ": right overlay",
                string.Equals(actualRight, expectedRightImage, StringComparison.Ordinal),
                "expected " + (expectedRightImage ?? "<base>") + " got " + (actualRight ?? "<base>"));
            Check(label + ": CatParamLeftHandDown",
                Math.Abs(leftDown - expectedLeftDown) < 0.001,
                "expected " + expectedLeftDown + " got " + leftDown);
            Check(label + ": CatParamRightHandDown",
                Math.Abs(rightDown - expectedRightDown) < 0.001,
                "expected " + expectedRightDown + " got " + rightDown);
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

        private static async void Capture(string name, Action<System.Drawing.Bitmap>? verify = null)
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
                verify?.Invoke(bitmap);
            }
            catch (Exception exception)
            {
                Fail("capture " + name + ": " + exception.Message);
            }
            finally { _capturesPending--; }
        }

        /// <summary>
        /// The nailin keyboard background must actually paint its keycaps. The sample point was
        /// located by mapping the source PNG's keycap geometry through the rendered screenshots:
        /// it sits deep inside a large keycap (source rgb 255,255,255), clear of the cat and the
        /// hand overlays, so only a painted background can make it bright.
        /// </summary>
        private static void ExpectKeyboardPixel(System.Drawing.Bitmap bitmap)
        {
            var pixel = bitmap.GetPixel(310, 250);
            Check("nailin keyboard background paints the keycap at (310,250)",
                pixel.R > 220 && pixel.G > 220 && pixel.B > 220,
                "rgb=" + pixel.R + "," + pixel.G + "," + pixel.B + " (need all >220)");
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
            return reloaded != null && (
                string.Equals(reloaded.DesktopPetModelPath, text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reloaded.DesktopPetPresetId, text, StringComparison.Ordinal));
        }

        /// <summary>Reads the file from disk again, like a fresh process would.</summary>
        public AppSettings Reload()
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings()
                : new AppSettings();
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
