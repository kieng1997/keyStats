using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
// Regression smoke test for tray-popup anchoring. Test-StatsPopupPositioning.ps1 extracts the
// production PositionNearTray/OnLoaded/OnWindowBoundsChanged methods into this partial class and
// shows a real WPF window. Never starts KeyStats, reads user stats, or touches local state.
namespace KeyStats.Views
{
    internal sealed partial class StatsPopupWindowHarness : Window
    {
        private readonly Border _root = new Border();
        private readonly StackPanel _content = new StackPanel();
        private readonly DispatcherTimer _windowStateSaveTimer = new DispatcherTimer();
        private bool _isWindowMode;
        private bool _isHiding;
        private bool _allowClose;
        private bool _isFullyLoaded;
        private bool _suppressStatePersistence;
        private System.Drawing.Point? _anchorPoint;

        private Border RootBorder => _root;

        public StatsPopupWindowHarness()
        {
            WindowStyle = WindowStyle.None;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            Width = 360;
            _root.Child = _content;
            Content = _root;
            Loaded += OnLoaded;
            SizeChanged += OnWindowBoundsChanged;
        }

        public void AnchorAt(System.Drawing.Point point) => _anchorPoint = point;

        public void SetContentHeight(double height)
        {
            _content.Children.Clear();
            _content.Children.Add(new Border { Height = height });
            UpdateLayout();
        }

        // Mirrors the tray branch of StatsPopupWindow.ShowWindow.
        public void ShowTrayPopup()
        {
            new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            PositionNearTray();
            Show();
        }

        private void RestoreWindowModeBounds() { }
    }

    internal static class StatsPopupPositioningSmokeTest
    {
        private static double _dpi = 1;

        [STAThread]
        private static int Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var exitCode = 0;
            app.Startup += (_, _) =>
            {
                try { Run(); }
                catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message); exitCode = 1; }
                finally { app.Shutdown(); }
            };
            app.Run();
            return exitCode;
        }

        private static void Run()
        {
            var workingArea = Forms.Screen.PrimaryScreen!.WorkingArea;
            var window = new StatsPopupWindowHarness();
            // Anchor inside the system tray, exactly like a tray-icon click.
            window.AnchorAt(new System.Drawing.Point(workingArea.Right - 60, workingArea.Bottom - 20));
            window.SetContentHeight(120);
            window.ShowTrayPopup();
            Pump();
            _dpi = PresentationSource.FromVisual(window)!.CompositionTarget.TransformToDevice.M22;
            CheckBottom(window, workingArea, "popup near the tray stays above the taskbar");

            // Stats arrive after the first layout and SizeToContent grows the window.
            var initialHeight = window.ActualHeight;
            window.SetContentHeight(workingArea.Height / _dpi * 0.55);
            Pump();
            Check(window.ActualHeight > initialHeight + 100, $"content growth resizes the popup ({initialHeight:F0} -> {window.ActualHeight:F0})");
            CheckBottom(window, workingArea, "popup stays above the taskbar after content grows");

            window.Hide();
            Pump();
            var hiddenTop = window.Top;
            window.SetContentHeight(80);
            Pump();
            Check(Math.Abs(window.Top - hiddenTop) < 0.5, "hidden popup is not repositioned");

            window.ShowTrayPopup();
            Pump();
            CheckBottom(window, workingArea, "reopened popup stays above the taskbar");
            window.Close();
        }

        private static void CheckBottom(Window window, System.Drawing.Rectangle workingArea, string description)
        {
            var bottom = (window.Top + window.ActualHeight) * _dpi;
            var limit = workingArea.Bottom - 10;
            Check(bottom <= limit + 1, $"{description} (bottom={bottom:F1}, limit={limit})");
        }

        private static void Check(bool condition, string description)
        {
            if (!condition) throw new Exception(description);
            Console.WriteLine("PASS: " + description);
        }

        private static void Pump()
        {
            for (var i = 0; i < 3; i++)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
        }
    }
}

namespace KeyStats
{
    // Compile-time stand-in for App.CurrentApp; only referenced by the windowed-mode branch.
    internal sealed class App
    {
        public static App? CurrentApp => null;
        public void TrackPageView(string page) { }
    }
}
