using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KeyStats.Helpers;
using KeyStats.Views.Controls;
// Rendering smoke test for the keyboard heatmap control. Test-KeyboardHeatmapRendering.ps1 compiles the
// production control and ThemeManager into this harness, shows a real WPF window that hosts the control,
// and inspects the rendered pixels. It never starts KeyStats, installs input hooks, reads user statistics
// or touches system settings; the only data it renders is synthetic.
namespace KeyStats.Views
{
    internal static class KeyboardHeatmapRenderingSmokeTest
    {
        // Chassis band above the numeric keypad, as fractions of the deck rect. Derived from the 104-key
        // layout contract: the numpad occupies x=21.73u..26.18u, the key field is 26.18u x 6.75u and the
        // deck is inflated by 0.35u, so the empty band above the numpad is 4.45u x 1.0u.
        private const double BrandBandLeft = 0.8214;
        private const double BrandBandRight = 0.9869;
        private const double BrandBandTop = 0.0470;
        private const double BrandBandBottom = 0.1812;

        // Bare chassis strip between the main key field (right edge 17.18u) and the navigation cluster
        // (left edge 17.73u), in the frozen 26.18u x 6.75u key-field contract. Inset so antialiased key
        // borders cannot be mistaken for grain.
        private const double ChassisStripLeft = 17.31;
        private const double ChassisStripRight = 17.60;
        private const double ChassisStripTop = 1.15;
        private const double ChassisStripBottom = 6.75;

        // Space bar frame in the same contract; its single-line legend sits in the lower-right corner.
        private const double SpaceLeft = 4.40;
        private const double SpaceTop = 5.75;
        private const double SpaceWidth = 7.73;
        private const double SpaceHeight = 1.0;

        // Every key frame of the ANSI 104-key layout in the 26.18u x 6.75u key-field contract, copied
        // here so the test never reaches into the control's private layout. Main checks it against
        // SupportedKeyIds so a layout change cannot silently invalidate the sampling.
        private static readonly (string Id, double X, double Y, double Width, double Height)[] KeyFrames =
        {
            new(@"Esc", 0.00, 0.00, 1.450, 1.000),
            new(@"F1", 1.60, 0.00, 1.161, 1.000),
            new(@"F2", 2.91, 0.00, 1.161, 1.000),
            new(@"F3", 4.22, 0.00, 1.161, 1.000),
            new(@"F4", 5.53, 0.00, 1.161, 1.000),
            new(@"F5", 6.84, 0.00, 1.161, 1.000),
            new(@"F6", 8.15, 0.00, 1.161, 1.000),
            new(@"F7", 9.46, 0.00, 1.161, 1.000),
            new(@"F8", 10.78, 0.00, 1.161, 1.000),
            new(@"F9", 12.09, 0.00, 1.161, 1.000),
            new(@"F10", 13.40, 0.00, 1.161, 1.000),
            new(@"F11", 14.71, 0.00, 1.161, 1.000),
            new(@"F12", 16.02, 0.00, 1.161, 1.000),
            new(@"`", 0.00, 1.15, 1.000, 1.000),
            new(@"1", 1.15, 1.15, 1.000, 1.000),
            new(@"2", 2.30, 1.15, 1.000, 1.000),
            new(@"3", 3.45, 1.15, 1.000, 1.000),
            new(@"4", 4.60, 1.15, 1.000, 1.000),
            new(@"5", 5.75, 1.15, 1.000, 1.000),
            new(@"6", 6.90, 1.15, 1.000, 1.000),
            new(@"7", 8.05, 1.15, 1.000, 1.000),
            new(@"8", 9.20, 1.15, 1.000, 1.000),
            new(@"9", 10.35, 1.15, 1.000, 1.000),
            new(@"0", 11.50, 1.15, 1.000, 1.000),
            new(@"-", 12.65, 1.15, 1.000, 1.000),
            new(@"=", 13.80, 1.15, 1.000, 1.000),
            new(@"Backspace", 14.95, 1.15, 2.230, 1.000),
            new(@"Tab", 0.00, 2.30, 1.450, 1.000),
            new(@"Q", 1.60, 2.30, 1.000, 1.000),
            new(@"W", 2.75, 2.30, 1.000, 1.000),
            new(@"E", 3.90, 2.30, 1.000, 1.000),
            new(@"R", 5.05, 2.30, 1.000, 1.000),
            new(@"T", 6.20, 2.30, 1.000, 1.000),
            new(@"Y", 7.35, 2.30, 1.000, 1.000),
            new(@"U", 8.50, 2.30, 1.000, 1.000),
            new(@"I", 9.65, 2.30, 1.000, 1.000),
            new(@"O", 10.80, 2.30, 1.000, 1.000),
            new(@"P", 11.95, 2.30, 1.000, 1.000),
            new(@"[", 13.10, 2.30, 1.000, 1.000),
            new(@"]", 14.25, 2.30, 1.000, 1.000),
            new(@"\", 15.40, 2.30, 1.780, 1.000),
            new(@"CapsLock", 0.00, 3.45, 1.800, 1.000),
            new(@"A", 1.95, 3.45, 1.000, 1.000),
            new(@"S", 3.10, 3.45, 1.000, 1.000),
            new(@"D", 4.25, 3.45, 1.000, 1.000),
            new(@"F", 5.40, 3.45, 1.000, 1.000),
            new(@"G", 6.55, 3.45, 1.000, 1.000),
            new(@"H", 7.70, 3.45, 1.000, 1.000),
            new(@"J", 8.85, 3.45, 1.000, 1.000),
            new(@"K", 10.00, 3.45, 1.000, 1.000),
            new(@"L", 11.15, 3.45, 1.000, 1.000),
            new(@";", 12.30, 3.45, 1.000, 1.000),
            new(@"'", 13.45, 3.45, 1.000, 1.000),
            new(@"Return", 14.60, 3.45, 2.580, 1.000),
            new(@"Shift", 0.00, 4.60, 2.450, 1.000),
            new(@"Z", 2.60, 4.60, 1.000, 1.000),
            new(@"X", 3.75, 4.60, 1.000, 1.000),
            new(@"C", 4.90, 4.60, 1.000, 1.000),
            new(@"V", 6.05, 4.60, 1.000, 1.000),
            new(@"B", 7.20, 4.60, 1.000, 1.000),
            new(@"N", 8.35, 4.60, 1.000, 1.000),
            new(@"M", 9.50, 4.60, 1.000, 1.000),
            new(@",", 10.65, 4.60, 1.000, 1.000),
            new(@".", 11.80, 4.60, 1.000, 1.000),
            new(@"/", 12.95, 4.60, 1.000, 1.000),
            new(@"Shift", 14.10, 4.60, 3.080, 1.000),
            new(@"LCtrl", 0.00, 5.75, 1.350, 1.000),
            new(@"LWin", 1.50, 5.75, 1.300, 1.000),
            new(@"LAlt", 2.95, 5.75, 1.300, 1.000),
            new(@"Space", 4.40, 5.75, 7.730, 1.000),
            new(@"RAlt", 12.28, 5.75, 1.300, 1.000),
            new(@"RWin", 13.73, 5.75, 1.300, 1.000),
            new(@"RCtrl", 15.18, 5.75, 2.000, 1.000),
            new(@"PrintScreen", 17.73, 0.00, 1.000, 1.000),
            new(@"ScrollLock", 18.88, 0.00, 1.000, 1.000),
            new(@"Pause", 20.03, 0.00, 1.000, 1.000),
            new(@"Insert", 17.73, 1.15, 1.000, 1.000),
            new(@"Home", 18.88, 1.15, 1.000, 1.000),
            new(@"PageUp", 20.03, 1.15, 1.000, 1.000),
            new(@"Delete", 17.73, 2.30, 1.000, 1.000),
            new(@"End", 18.88, 2.30, 1.000, 1.000),
            new(@"PageDown", 20.03, 2.30, 1.000, 1.000),
            new(@"Up", 18.88, 4.60, 1.000, 1.000),
            new(@"Left", 17.73, 5.75, 1.000, 1.000),
            new(@"Down", 18.88, 5.75, 1.000, 1.000),
            new(@"Right", 20.03, 5.75, 1.000, 1.000),
            new(@"NumLock", 21.73, 1.15, 1.000, 1.000),
            new(@"Num/", 22.88, 1.15, 1.000, 1.000),
            new(@"Num*", 24.03, 1.15, 1.000, 1.000),
            new(@"Num-", 25.18, 1.15, 1.000, 1.000),
            new(@"Num7", 21.73, 2.30, 1.000, 1.000),
            new(@"Num8", 22.88, 2.30, 1.000, 1.000),
            new(@"Num9", 24.03, 2.30, 1.000, 1.000),
            new(@"Num+", 25.18, 2.30, 1.000, 2.150),
            new(@"Num4", 21.73, 3.45, 1.000, 1.000),
            new(@"Num5", 22.88, 3.45, 1.000, 1.000),
            new(@"Num6", 24.03, 3.45, 1.000, 1.000),
            new(@"Num1", 21.73, 4.60, 1.000, 1.000),
            new(@"Num2", 22.88, 4.60, 1.000, 1.000),
            new(@"Num3", 24.03, 4.60, 1.000, 1.000),
            new(@"NumEnter", 25.18, 4.60, 1.000, 2.150),
            new(@"Num0", 21.73, 5.75, 2.150, 1.000),
            new(@"Num.", 24.03, 5.75, 1.000, 1.000),
        };

        private const double KeyFieldWidth = 26.18;
        private const double DeckPaddingUnits = 0.35;

        private static string _outputDir = string.Empty;
        private static readonly List<string> _legendFailures = new();

        [STAThread]
        private static int Main(string[] args)
        {
            _outputDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "ks-heatmap");
            Directory.CreateDirectory(_outputDir);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var exitCode = 0;
            app.Startup += (_, _) =>
            {
                try
                {
                    ThemeManager.Instance.Initialize();
                    Run();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("FAIL: " + ex.Message);
                    exitCode = 1;
                }
                finally
                {
                    ThemeManager.Instance.Dispose();
                    app.Shutdown();
                }
            };
            app.Run();
            return exitCode;
        }

        private static void Run()
        {
            CheckKeyFramesMatchControl();

            foreach (var dark in new[] { false, true })
            {
                ApplyTheme(dark);
                var themeName = dark ? "dark" : "light";
                using var host = new HeatmapHost(1000, 290);
                host.Show();
                host.Control.Apply(EmptyCounts());
                Pump();

                var frame = Frame.Capture(host.Control, host.CardColor);
                SavePng(frame, $"brand-{themeName}.png");
                CheckChassisTexture(frame, themeName);
                CheckBrandMark(frame, themeName);

                host.Close();
                Pump();
            }

            CheckLegibility();
            CheckMinimumSize();
            CheckHighDpi();
            CheckLegendGate();
            CheckDeterministic();
            CheckLiveThemeSwitch();
            CheckSnapshotRender();
            MeasureFrameCost();
        }

        // The control subscribes to ThemeManager.ThemeChanged; flipping the theme in a live window must
        // repaint the chassis in the new material and keep the brand mark.
        private static void CheckLiveThemeSwitch()
        {
            ApplyTheme(false);
            using var host = new HeatmapHost(1000, 290);
            host.Show();
            host.Control.Apply(EmptyCounts());
            Pump();

            var light = Frame.Capture(host.Control, host.CardColor);
            var lightMean = light.MeanGrey();

            ApplyTheme(true);
            // ThemeChanged is dispatched through BeginInvoke; two idle pumps let the queued repaint run.
            Pump();
            Pump();

            var dark = Frame.Capture(host.Control, ResourceColor("CardColor", Color.FromRgb(26, 26, 26)));
            var darkMean = dark.MeanGrey();
            SavePng(dark, "theme-switch-dark.png");

            Console.WriteLine($"  theme switch: light mean={lightMean:F3}, dark mean={darkMean:F3}");
            Check(darkMean < lightMean - 0.2,
                $"live theme switch repaints the chassis darker (light={lightMean:F3}, dark={darkMean:F3})");
            CheckBrandMark(dark, "live switch to dark");
            CheckChassisTexture(dark, "live switch to dark");

            host.Close();
            Pump();
        }

        private static Color ResourceColor(string key, Color fallback)
            => Application.Current.Resources[key] is SolidColorBrush brush ? brush.Color : fallback;

        // 150% scaling is not guaranteed on every machine, so the high-DPI pass renders the control
        // off-screen at 144 DPI instead of moving the real display.
        private static void CheckHighDpi()
        {
            foreach (var dark in new[] { false, true })
            {
                ApplyTheme(dark);
                var themeName = dark ? "dark" : "light";
                using var host = new HeatmapHost(1000, 290);
                host.Show();
                host.Control.Apply(new Dictionary<string, double>(StringComparer.Ordinal) { ["E"] = 48213, ["Space"] = 9123456 });
                Pump();

                var frame = Frame.Capture(host.Control, host.CardColor, dpiScale: 1.5);
                SavePng(frame, $"dpi150-{themeName}.png");
                Check(frame.Width > 1400, $"{themeName} at 150%: rendered at {frame.Width}x{frame.Height} device pixels");
                CheckChassisTexture(frame, $"{themeName} at 150%");
                CheckBrandMark(frame, $"{themeName} at 150%");
                CheckAllLegendsReadable(frame, $"{themeName} at 150%", includeUpperLines: false);

                host.Close();
                Pump();
            }
        }

        // Rough cost of one full repaint. This is a smoke check for pathological regressions (per-key
        // texture generation, per-frame disk IO), not a frame-rate claim.
        private static void MeasureFrameCost()
        {
            ApplyTheme(false);
            using var host = new HeatmapHost(1000, 290);
            host.Show();
            host.Control.Apply(EmptyCounts());
            Pump();

            var bitmap = new RenderTargetBitmap(
                (int)Math.Round(host.Control.ActualWidth),
                (int)Math.Round(host.Control.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host.Control);
            Pump();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            const int frames = 20;
            for (var i = 0; i < frames; i++)
            {
                bitmap.Render(host.Control);
            }
            stopwatch.Stop();
            var perFrame = stopwatch.Elapsed.TotalMilliseconds / frames;
            Console.WriteLine($"  repaint cost: {perFrame:F1} ms/frame over {frames} frames");
            Check(perFrame < 120, $"one repaint stays cheap ({perFrame:F1} ms/frame)");

            host.Close();
            Pump();
        }

        // The sampling table is an independent copy of the layout contract; if the control's key set ever
        // changes, the copy is stale and the legend sampling would silently skip keys.
        private static void CheckKeyFramesMatchControl()
        {
            var sampled = new HashSet<string>(KeyFrames.Select(frame => frame.Id), StringComparer.Ordinal);
            var missing = KeyboardHeatmapControl.SupportedKeyIds.Where(id => !sampled.Contains(id)).ToList();
            var extra = sampled.Where(id => !KeyboardHeatmapControl.SupportedKeyIds.Contains(id)).ToList();
            Check(missing.Count == 0 && extra.Count == 0,
                $"legend sampling covers exactly the control's keys (missing: {string.Join(",", missing)}; extra: {string.Join(",", extra)})");
        }

        // The heatmap window's drawing area is about 1006x298 at its 1080x520 default and about
        // 786x298 at the 860x520 minimum; the control must stay intact at the smaller one, with names and
        // badges coexisting on mixed data.
        private static void CheckMinimumSize()
        {
            foreach (var dark in new[] { false, true })
            {
                ApplyTheme(dark);
                var themeName = dark ? "dark" : "light";
                using var host = new HeatmapHost(786, 298);
                host.Show();
                host.Control.Apply(MixedCounts(), showDecimals: true);
                Pump();

                var frame = Frame.Capture(host.Control, host.CardColor);
                SavePng(frame, $"minimum-{themeName}.png");

                var deck = frame.DeckBounds();
                Console.WriteLine($"  {themeName}: minimum-size deck={deck}");
                Check(deck.Width > 600 && deck.Height > 150,
                    $"{themeName}: chassis fills the minimum drawing area ({deck.Width}x{deck.Height})");
                CheckBrandMark(frame, $"{themeName} minimum");
                CheckChassisTexture(frame, $"{themeName} minimum");
                CheckNoClipping(frame, $"{themeName} minimum");
                CheckAllLegendsReadable(frame, $"{themeName} minimum", includeUpperLines: true);
                CheckCountBadgeReadable(frame, "Space", MixedCounts()["Space"], $"{themeName} minimum");
                CheckCountBadgeReadable(frame, "E", MixedCounts()["E"], $"{themeName} minimum");
                CheckEmptyKeyHasNoBadge(frame, "Backspace", $"{themeName} minimum");

                host.Close();
                Pump();
            }
        }

        // The cast shadow must fall inside the control: nothing may touch the outer edge, and there must
        // be shadow ink just outside the chassis.
        private static void CheckNoClipping(Frame frame, string themeName)
        {
            var deck = frame.DeckBounds();
            var shadowRight = deck.X + deck.Width;
            var shadowBottom = deck.Y + deck.Height;
            Check(shadowRight + 2 < frame.Width && shadowBottom + 2 < frame.Height,
                $"{themeName}: chassis and its shadow stay inside the drawing area");

            // Sample just past the silhouette at mid-height/mid-width, where the rounded corners cannot
            // hide the shadow.
            var rightOfDeck = frame.AlphaAt(shadowRight + 1, deck.Y + deck.Height / 2);
            var belowDeck = frame.AlphaAt(deck.X + deck.Width / 2, shadowBottom + 1);
            Check((rightOfDeck > 0 && rightOfDeck < 250) || (belowDeck > 0 && belowDeck < 250),
                $"{themeName}: soft shadow is visible beside the chassis (right={rightOfDeck}, bottom={belowDeck})");
        }

        private static void CheckDeterministic()
        {
            ApplyTheme(false);
            using var host = new HeatmapHost(1000, 290);
            host.Show();
            host.Control.Apply(EmptyCounts());
            Pump();

            var first = Frame.Capture(host.Control, host.CardColor);
            var second = Frame.Capture(host.Control, host.CardColor);
            Check(first.ContentEquals(second), "rendering is deterministic across frames (identical pixels)");
            SavePng(first, "deterministic-light.png");

            host.Close();
            Pump();
        }

        // Mirrors KeyboardHeatmapWindow's day-slide snapshot: the layer is captured with a
        // RenderTargetBitmap and must contain the rendered chassis, not a blank frame.
        private static void CheckSnapshotRender()
        {
            ApplyTheme(true);
            using var host = new HeatmapHost(1000, 290);
            host.Show();
            host.Control.Apply(EmptyCounts());
            Pump();

            var layer = (FrameworkElement)host.Control.Parent;
            layer.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(layer);
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Round(layer.ActualWidth * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Round(layer.ActualHeight * dpi.DpiScaleY)),
                96 * dpi.DpiScaleX,
                96 * dpi.DpiScaleY,
                PixelFormats.Pbgra32);
            bitmap.Render(layer);
            bitmap.Freeze();

            var frame = Frame.FromBitmap(bitmap, host.CardColor);
            SavePng(frame, "snapshot-dark.png");
            var deck = frame.DeckBounds();
            Console.WriteLine($"  dark: snapshot deck={deck}");
            Check(deck.Width > 900 && deck.Height > 200,
                $"dark: day-slide snapshot renders the full chassis ({deck.Width}x{deck.Height})");

            host.Close();
            Pump();
        }

        private static void CheckLegibility()
        {
            var counts = MixedCounts();

            foreach (var dark in new[] { false, true })
            {
                ApplyTheme(dark);
                var themeName = dark ? "dark" : "light";
                using var host = new HeatmapHost(1000, 290);
                host.Show();
                host.Control.Apply(counts, showDecimals: true);
                Pump();

                var frame = Frame.Capture(host.Control, host.CardColor);
                SavePng(frame, $"legibility-{themeName}.png");

                CheckAllLegendsReadable(frame, themeName, includeUpperLines: false);
                CheckCountBadgeReadable(frame, "E", counts["E"], themeName);
                CheckCountBadgeReadable(frame, "Space", counts["Space"], themeName);
                CheckEmptyKeyHasNoBadge(frame, "Backspace", themeName);

                host.Close();
                Pump();
            }
        }

        // Mixed heat levels: one key at the maximum (hottest cap, which is where the legend ink has the
        // least headroom), one mid, one low, one empty. Space is the maximum so the hottest cap is the one
        // whose long single-line legend is measured.
        private static Dictionary<string, double> MixedCounts()
            => new(StringComparer.Ordinal)
            {
                ["E"] = 48213,
                ["Space"] = 9123456,
                ["Q"] = 12,
                ["A"] = 733,
                ["Backspace"] = 0
            };

        // The second legend line of a two-line key. The number row is excluded: those caps print only the
        // digit, by design, so there is no upper glyph to measure.
        private static readonly HashSet<string> DoubleLineKeyIds = new(StringComparer.Ordinal)
        {
            "`", "-", "=", "[", "]", "\\", ";", "'", ",", ".", "/", "PrintScreen", "ScrollLock", "NumLock"
        };

        // Every key name must clear 4.5:1 against the cap face it is printed on, across the whole heat
        // ramp. Results from every pass (both themes, minimum width, 150%) are collected and gated once at
        // the end, so one run reports the full picture instead of only the first failing theme. Sampling
        // is confined to the glyph band so a cap edge, the dished shading or the count badge can never be
        // mistaken for text ink.
        private static void CheckAllLegendsReadable(Frame frame, string themeName, bool includeUpperLines)
        {
            var worst = (Contrast: double.MaxValue, Key: string.Empty);
            foreach (var key in KeyFrames)
            {
                var contrast = frame.LegendContrast(key.Id, upperLine: false);
                if (contrast < worst.Contrast) worst = (contrast, key.Id);
                if (contrast < 4.5) _legendFailures.Add($"{themeName} '{key.Id}'={contrast:F2}");

                // The second legend line of a two-line key must hold the same bar.
                if (includeUpperLines && DoubleLineKeyIds.Contains(key.Id))
                {
                    var upper = frame.LegendContrast(key.Id, upperLine: true);
                    if (upper < worst.Contrast) worst = (upper, key.Id + " upper");
                    if (upper < 4.5) _legendFailures.Add($"{themeName} '{key.Id}' upper={upper:F2}");
                }
            }

            Console.WriteLine($"  {themeName}: worst key-name contrast {worst.Contrast:F2} on '{worst.Key}'");
        }

        private static void CheckLegendGate()
        {
            Check(_legendFailures.Count == 0,
                $"every key name clears 4.5:1 on its heat colour (failing: {string.Join(", ", _legendFailures)})");
        }

        private static void CheckCountBadgeReadable(Frame frame, string keyId, double count, string themeName)
        {
            var cap = frame.KeyRect(keyId);
            // The badge is drawn at a small fixed inset from the cap's top-left corner, so the sample
            // region is anchored there rather than scaled with the cap.
            const int inset = 2;
            var region = frame.Crop(new Int32Rect(
                cap.X + inset,
                cap.Y + inset,
                Math.Max(4, cap.Width / 2),
                Math.Max(4, cap.Height / 2)));
            SavePng(region, $"badge-{keyId}-{themeName}.png");
            var contrast = region.MaxLocalContrast();
            Console.WriteLine($"  {themeName}: key '{keyId}' badge contrast={contrast:F2} (count={count:N0})");
            Check(contrast >= 3.0, $"{themeName}: count badge on '{keyId}' stays readable (contrast={contrast:F2})");
        }

        private static void CheckEmptyKeyHasNoBadge(Frame frame, string keyId, string themeName)
        {
            var cap = frame.KeyRect(keyId);
            // Sample the cap interior away from its rim highlight, where a badge would sit.
            const int inset = 6;
            var region = frame.Crop(new Int32Rect(
                cap.X + inset,
                cap.Y + inset,
                Math.Max(4, cap.Width / 2),
                Math.Max(4, cap.Height / 2 - inset)));
            var contrast = region.MaxLocalContrast();
            Console.WriteLine($"  {themeName}: key '{keyId}' with zero count badge contrast={contrast:F2}");
            Check(contrast < 3.0, $"{themeName}: key '{keyId}' with no presses shows no count badge (contrast={contrast:F2})");
        }

        private static void CheckBrandMark(Frame frame, string themeName)
        {
            var deck = frame.DeckBounds();
            Check(deck.Width > 200 && deck.Height > 100, $"{themeName}: deck rendered ({deck.Width}x{deck.Height})");

            // The band above the numpad is bare chassis; drop its lower fifth so a slightly different
            // deck/key-field anchor cannot pick up the numpad's top edge.
            var bandRect = new Int32Rect(
                deck.X + (int)Math.Round((deck.Width - 1) * BrandBandLeft) + 2,
                deck.Y + (int)Math.Round((deck.Height - 1) * BrandBandTop) + 1,
                (int)Math.Round((deck.Width - 1) * (BrandBandRight - BrandBandLeft)) - 4,
                (int)Math.Round((deck.Height - 1) * (BrandBandBottom - BrandBandTop) * 0.8));
            var band = frame.Crop(bandRect);
            SavePng(band, $"brand-band-{themeName}.png");

            var ink = band.InkFraction(0.045);
            Console.WriteLine($"  {themeName}: chassis band ink fraction={ink:F3}");
            Check(ink >= 0.02,
                $"{themeName}: brand mark is printed on the chassis above the numeric keypad (ink={ink:F3})");
        }

        private static void CheckChassisTexture(Frame frame, string themeName)
        {
            var deck = frame.DeckBounds();
            var strip = frame.UnitRect(deck, ChassisStripLeft, ChassisStripTop, ChassisStripRight - ChassisStripLeft, ChassisStripBottom - ChassisStripTop);
            var crop = frame.Crop(strip);
            SavePng(crop, $"chassis-grain-{themeName}.png");

            var grain = crop.MicroContrast();
            Console.WriteLine($"  {themeName}: chassis grain micro-contrast={grain:F4}");
            Check(grain >= 0.004,
                $"{themeName}: chassis has a fine grain texture, not a flat fill or a single outline (micro-contrast={grain:F4})");
        }

        private static Dictionary<string, double> EmptyCounts() => new(StringComparer.Ordinal);

        private static void ApplyTheme(bool dark)
        {
            var manager = ThemeManager.Instance;
            var property = typeof(ThemeManager).GetProperty("IsDarkTheme", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception("ThemeManager.IsDarkTheme was not found");
            property.GetSetMethod(true)!.Invoke(manager, new object[] { dark });
            typeof(ThemeManager).GetMethod("ApplyTheme", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(manager, null);

            var handler = (Action?)typeof(ThemeManager)
                .GetField("ThemeChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
                .GetValue(manager);
            handler?.Invoke();
            Pump();
        }

        private static void Check(bool condition, string description)
        {
            if (!condition) throw new Exception(description);
            Console.WriteLine("PASS: " + description);
        }

        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        private static void SavePng(Frame frame, string fileName)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame.Source));
            var path = Path.Combine(_outputDir, fileName);
            using var stream = File.Create(path);
            encoder.Save(stream);
            Console.WriteLine("  artifact: " + path);
        }

        private sealed class HeatmapHost : IDisposable
        {
            private readonly Window _window;
            public KeyboardHeatmapControl Control { get; }
            public Color CardColor { get; }

            public HeatmapHost(double width, double height)
            {
                Control = new KeyboardHeatmapControl();
                CardColor = ResourceColor("CardColor", Color.FromRgb(242, 242, 242));
                _window = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Topmost = true,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Width = width,
                    Height = height,
                    Background = ResourceBrush("CardBrush"),
                    Content = Control
                };
            }

            public void Show()
            {
                _window.Show();
                PumpUntil(() => Control.IsLoaded && Control.ActualWidth > 0, "heatmap control to load");
            }

            public void Close() => _window.Close();

            public void Dispose() => _window.Close();

            private static Brush ResourceBrush(string key)
                => Application.Current.Resources[key] as Brush ?? Brushes.Transparent;
        }

        private sealed class Frame
        {
            private const int BytesPerPixel = 4;

            public BitmapSource Source { get; }
            public int Width { get; }
            public int Height { get; }
            private byte[] Pixels { get; }
            private Color Backdrop { get; }

            private Frame(BitmapSource source, byte[] pixels, Color backdrop)
            {
                Source = source;
                Width = source.PixelWidth;
                Height = source.PixelHeight;
                Pixels = pixels;
                Backdrop = backdrop;
            }

            public static Frame Capture(KeyboardHeatmapControl control, Color backdrop, double dpiScale = 1)
            {
                control.UpdateLayout();
                var width = Math.Max(1, (int)Math.Round(control.ActualWidth * dpiScale));
                var height = Math.Max(1, (int)Math.Round(control.ActualHeight * dpiScale));
                var bitmap = new RenderTargetBitmap(width, height, 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
                bitmap.Render(control);
                bitmap.Freeze();
                return FromBitmap(bitmap, backdrop);
            }

            public static Frame FromBitmap(BitmapSource bitmap, Color backdrop)
            {
                var width = bitmap.PixelWidth;
                var height = bitmap.PixelHeight;
                var straight = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                straight.Freeze();
                var pixels = new byte[width * height * BytesPerPixel];
                straight.CopyPixels(pixels, width * BytesPerPixel, 0);
                return new Frame(straight, pixels, backdrop);
            }

            public bool ContentEquals(Frame other)
            {
                if (Width != other.Width || Height != other.Height) return false;
                for (var i = 0; i < Pixels.Length; i++)
                {
                    if (Pixels[i] != other.Pixels[i]) return false;
                }
                return true;
            }

            // Composite of the rendered pixel over the card the control normally sits on, so translucent
            // chassis revisions are judged the way they look in the window.
            public Color At(int x, int y)
            {
                var offset = y * Width * BytesPerPixel + x * BytesPerPixel;
                var alpha = Pixels[offset + 3] / 255.0;
                byte Mix(byte over, byte under) => (byte)Math.Round(over * alpha + under * (1 - alpha));
                return Color.FromArgb(255,
                    Mix(Pixels[offset + 2], Backdrop.R),
                    Mix(Pixels[offset + 1], Backdrop.G),
                    Mix(Pixels[offset], Backdrop.B));
            }

            public int AlphaAt(int x, int y) => Pixels[y * Width * BytesPerPixel + x * BytesPerPixel + 3];

            public double Luminance(int x, int y) => RelativeLuminance(At(x, y));

            public Int32Rect DeckBounds()
            {
                // The opaque chassis, ignoring the translucent cast shadow around it. Revisions that draw
                // a translucent deck fall back to the looser threshold.
                var bounds = BoundsAtLeast(250);
                return bounds.Width > 0 ? bounds : BoundsAtLeast(40);
            }

            private Int32Rect BoundsAtLeast(int alpha)
            {
                var left = Width;
                var top = Height;
                var right = -1;
                var bottom = -1;
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        if (AlphaAt(x, y) < alpha) continue;
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                    }
                }

                if (right < 0) return new Int32Rect(0, 0, 0, 0);
                return new Int32Rect(left, top, right - left + 1, bottom - top + 1);
            }

            public Frame Crop(Int32Rect rect)
            {
                var x0 = Math.Max(0, rect.X);
                var y0 = Math.Max(0, rect.Y);
                var width = Math.Max(1, Math.Min(rect.Width, Width - x0));
                var height = Math.Max(1, Math.Min(rect.Height, Height - y0));
                var pixels = new byte[width * height * BytesPerPixel];
                for (var y = 0; y < height; y++)
                {
                    Buffer.BlockCopy(Pixels, (y0 + y) * Width * BytesPerPixel + x0 * BytesPerPixel,
                        pixels, y * width * BytesPerPixel, width * BytesPerPixel);
                }

                var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                    pixels, width * BytesPerPixel);
                source.Freeze();
                return new Frame(source, pixels, Backdrop);
            }

            // Maps a rect in the frozen layout's unit space (key field origin, 26.18u x 6.75u) onto the
            // rendered deck, which is inflated by 0.35u on every side.
            public Int32Rect UnitRect(Int32Rect deck, double x, double y, double width, double height)
            {
                var unitsToPixels = (deck.Width - 1) / (KeyFieldWidth + DeckPaddingUnits * 2);
                return new Int32Rect(
                    deck.X + (int)Math.Round((x + DeckPaddingUnits) * unitsToPixels),
                    deck.Y + (int)Math.Round((y + DeckPaddingUnits) * unitsToPixels),
                    Math.Max(1, (int)Math.Round(width * unitsToPixels)),
                    Math.Max(1, (int)Math.Round(height * unitsToPixels)));
            }

            public Int32Rect KeyRect(string keyId)
            {
                var frame = FrameOf(keyId);
                return UnitRect(DeckBounds(), frame.X, frame.Y, frame.Width, frame.Height);
            }

            private static Rect FrameOf(string keyId)
            {
                foreach (var frame in KeyFrames)
                {
                    if (string.Equals(frame.Id, keyId, StringComparison.Ordinal))
                    {
                        return new Rect(frame.X, frame.Y, frame.Width, frame.Height);
                    }
                }

                throw new ArgumentOutOfRangeException(nameof(keyId), keyId, "No frame declared for this key");
            }

            // Contrast between the key-name ink and the cap face it is printed on, measured only inside
            // the glyph band. The face is the band's median luminance and the ink is the extreme that
            // sits farthest from it, so neither the cap edge, the dished shading, the rim highlight nor
            // the count badge can be mistaken for text. The material grain is far too weak to reach the
            // ink extremes, so the extreme is the glyph core, not noise.
            public double LegendContrast(string keyId, bool upperLine)
            {
                var cap = KeyRect(keyId);
                // Lower line: right of the badge and below it. Upper line: the narrower band to the right
                // of the badge, in the upper half of the cap.
                var x0 = cap.X + (int)Math.Round(cap.Width * (upperLine ? 0.60 : 0.42));
                var x1 = cap.X + cap.Width - 2;
                var y0 = upperLine
                    ? cap.Y + (int)Math.Round(cap.Height * 0.12)
                    : cap.Y + (int)Math.Round(cap.Height * 0.52);
                var y1 = upperLine
                    ? cap.Y + (int)Math.Round(cap.Height * 0.50)
                    : cap.Y + cap.Height - 3;
                if (x1 - x0 < 3 || y1 - y0 < 3)
                {
                    throw new Exception($"Legend band too small for '{keyId}'");
                }

                var luminances = new List<double>((x1 - x0) * (y1 - y0));
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        luminances.Add(Luminance(x, y));
                    }
                }

                luminances.Sort();
                var median = luminances[luminances.Count / 2];
                var low = luminances[0];
                var high = luminances[luminances.Count - 1];
                var ink = median - low > high - median ? low : high;
                var lighter = Math.Max(ink, median);
                var darker = Math.Min(ink, median);
                return (lighter + 0.05) / (darker + 0.05);
            }

            // Largest luminance ratio between any two pixels of the region: text or a badge against the
            // cap face must clear 3:1 to stay legible.
            public double MaxLocalContrast()
            {
                var min = double.MaxValue;
                var max = double.MinValue;
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        var value = Luminance(x, y);
                        if (value < min) min = value;
                        if (value > max) max = value;
                    }
                }

                return (max + 0.05) / (min + 0.05);
            }

            // Mean absolute change in the gamma-encoded grey value between neighbouring pixels, ignoring
            // the outer 2px so an edge highlight or border does not count as texture. Channel space is
            // used because linear luminance compresses near black and would under-report the dark theme.
            public double MicroContrast()
            {
                var sum = 0.0;
                var count = 0;
                for (var y = 2; y < Height - 2; y++)
                {
                    for (var x = 2; x < Width - 2; x++)
                    {
                        var value = Grey(x, y);
                        sum += Math.Abs(value - Grey(x + 1, y)) + Math.Abs(value - Grey(x, y + 1));
                        count += 2;
                    }
                }
                return count == 0 ? 0 : sum / count;
            }

            public double MeanGrey()
            {
                var sum = 0.0;
                var count = 0;
                for (var y = 0; y < Height; y += 2)
                {
                    for (var x = 0; x < Width; x += 2)
                    {
                        sum += Grey(x, y);
                        count++;
                    }
                }
                return count == 0 ? 0 : sum / count;
            }

            private double Grey(int x, int y)
            {
                var color = At(x, y);
                return (color.R + color.G + color.B) / (3.0 * 255.0);
            }

            // Fraction of pixels whose luminance differs from the band median by more than the threshold:
            // a printed mark adds ink, a plain chassis surface does not.
            public double InkFraction(double threshold)
            {
                var luminances = new List<double>(Width * Height);
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        luminances.Add(Luminance(x, y));
                    }
                }

                var sorted = luminances.OrderBy(value => value).ToList();
                var median = sorted[sorted.Count / 2];
                var ink = luminances.Count(value => Math.Abs(value - median) > threshold);
                return ink / (double)luminances.Count;
            }
        }

        private static double RelativeLuminance(Color color)
        {
            static double Channel(byte value)
            {
                var channel = value / 255.0;
                return channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }

        private static void PumpUntil(Func<bool> condition, string description)
        {
            for (var attempt = 0; attempt < 400; attempt++)
            {
                if (condition()) return;
                Pump();
                System.Threading.Thread.Sleep(5);
            }

            throw new Exception("Timed out waiting for " + description);
        }
    }
}
