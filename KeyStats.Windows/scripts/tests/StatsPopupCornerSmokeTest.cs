using System;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using KeyStats.Helpers;
// Regression smoke test for the tray popup's rounded corners. Test-StatsPopupCorners.ps1 compiles the
// production WindowBackdropHelper/NativeInterop/ThemeManager into this harness and shows a real WPF
// window with the tray-popup chrome over a synthetic magenta backdrop, then samples the corner pixels.
// Never starts KeyStats, reads user stats, or captures anything but its own two windows.
namespace KeyStats.Views
{
    internal static class StatsPopupCornerSmokeTest
    {
        private const int SRCCOPY = 0x00CC0020;
        private static readonly Color BackdropColor = Color.FromRgb(255, 0, 255);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

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
            var workArea = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var backdrop = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(BackdropColor),
                WindowStartupLocation = WindowStartupLocation.Manual,
                Width = 520,
                Height = 420,
                Topmost = true
            };
            backdrop.Left = workArea.Left + Math.Max(0, (workArea.Width - 520) / 2);
            backdrop.Top = workArea.Top + Math.Max(0, (workArea.Height - 420) / 2);
            backdrop.Show();
            Wait(250);

            CheckCorners(backdrop, "#FAFAFA", "light theme popup");
            CheckCorners(backdrop, "#202020", "dark theme popup");

            backdrop.Close();
            Pump();
        }

        // Mirrors StatsPopupWindow tray mode: WindowStyle=None, AllowsTransparency=false,
        // Background=Transparent, rounded RootBorder, EnsureHandle() before Show(), and the shared
        // WindowBackdropHelper applied from SourceInitialized.
        private static void CheckCorners(Window backdrop, string surfaceColor, string description)
        {
            var popup = new Window
            {
                Owner = backdrop,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                AllowsTransparency = false,
                Background = Brushes.Transparent,
                Width = 220,
                Height = 170,
                Topmost = true,
                Content = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = (SolidColorBrush)new BrushConverter().ConvertFromString(surfaceColor)!,
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0, 0, 0))
                }
            };
            popup.SourceInitialized += (_, _) =>
                WindowBackdropHelper.Apply(popup, NativeInterop.DwmSystemBackdropType.TransientWindow);

            new WindowInteropHelper(popup).EnsureHandle();
            popup.Show();
            Wait(400);

            var handle = new WindowInteropHelper(popup).Handle;
            if (!NativeInterop.GetWindowRect(handle, out var rect))
            {
                throw new Exception($"{description}: GetWindowRect failed");
            }

            if (!NativeInterop.GetWindowRect(new WindowInteropHelper(backdrop).Handle, out var backdropRect) ||
                rect.Left < backdropRect.Left || rect.Top < backdropRect.Top ||
                rect.Right > backdropRect.Right || rect.Bottom > backdropRect.Bottom)
            {
                throw new Exception($"{description}: popup is not fully inside the synthetic backdrop");
            }

            // Physical screen pixels, so the crop is correct at any DPI scale and never leaves the backdrop.
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            using (var bitmap = Capture(rect.Left, rect.Top, width, height))
            {
                var artifact = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "ks-popup-corners",
                    description.Replace(' ', '-') + ".png");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(artifact)!);
                bitmap.Save(artifact, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("  artifact: " + artifact);

                var center = bitmap.GetPixel(width / 2, height / 2);
                Check(!IsBackdrop(center),
                    $"{description}: window content rendered (center=#{center.R:X2}{center.G:X2}{center.B:X2})");
                CheckCorner(bitmap, 0, 0, description, "top-left");
                CheckCorner(bitmap, width - 1, 0, description, "top-right");
                CheckCorner(bitmap, 0, height - 1, description, "bottom-left");
                CheckCorner(bitmap, width - 1, height - 1, description, "bottom-right");
            }

            popup.Close();
            Pump();
        }

        private static void CheckCorner(Drawing.Bitmap bitmap, int x, int y, string description, string corner)
        {
            var pixel = bitmap.GetPixel(x, y);
            Check(IsBackdrop(pixel),
                $"{description}: {corner} corner is transparent (px=#{pixel.R:X2}{pixel.G:X2}{pixel.B:X2})");
        }

        private static bool IsBackdrop(Drawing.Color pixel)
            => pixel.R > 0x80 && pixel.G < 0x80 && pixel.B > 0x80;

        private static Drawing.Bitmap Capture(int left, int top, int width, int height)
        {
            var bitmap = new Drawing.Bitmap(width, height, PixelFormat.Format32bppArgb);
            var screenDc = GetDC(IntPtr.Zero);
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            {
                var destDc = graphics.GetHdc();
                try { BitBlt(destDc, 0, 0, width, height, screenDc, left, top, SRCCOPY); }
                finally { graphics.ReleaseHdc(destDc); }
            }

            ReleaseDC(IntPtr.Zero, screenDc);
            return bitmap;
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

        private static void Wait(int milliseconds)
        {
            var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < end)
            {
                Pump();
                System.Threading.Thread.Sleep(15);
            }
        }
    }
}
