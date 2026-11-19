using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using KeyStats.Models;
using KeyStats.Views;
using KeyStats.Views.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DesktopPetSmoke;

public static class InitializationHideCheck
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        Directory.CreateDirectory(args[0]);
        var log = Path.Combine(args[0], "initialization-hide.log");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 1;
        app.Startup += async (_, _) =>
        {
            FloatingStatsWindow window = null;
            try
            {
                var reads = 0;
                window = new FloatingStatsWindow(
                    new AppSettings { DesktopPetModelPath = Path.GetFullPath(args[1]), FloatingStatsTopmost = false },
                    () => { }, Path.Combine(Path.GetFullPath(args[0]), "profile"),
                    _ => { reads++; return new DesktopPetInputFrame(new int[0], 0, 0, false, false); });
                var webView = FindWebView((DependencyObject)window.FindName("DesktopPetHost"));
                Require(webView != null, "production WebView exists");
                var hidden = new TaskCompletionSource<bool>();
                window.Loaded += (_, _) =>
                {
                    if (hidden.Task.IsCompleted) return;
                    var beforeCore = webView.CoreWebView2 == null;
                    window.Hide();
                    hidden.TrySetResult(beforeCore);
                };
                window.Show();
                Require(await WithTimeout(hidden.Task), "hidden before Core initialization completes");
                var deadline = DateTime.UtcNow.AddSeconds(20);
                var host = (System.Windows.Controls.Grid)window.FindName("DesktopPetHost");
                var pet = (DesktopPetView)host.Children[0];
                while (!pet.IsViewerReady && DateTime.UtcNow < deadline) await Task.Delay(100);
                var core = webView.CoreWebView2;
                Require(core != null && pet.IsViewerReady, "viewer initialized while hidden");
                JsonElement state;
                do
                {
                    state = await Snapshot(core);
                    if (state.GetProperty("modelLoaded").GetBoolean()) break;
                    await Task.Delay(100);
                } while (DateTime.UtcNow < deadline);
                Require(state.GetProperty("modelLoaded").GetBoolean(), "real model loaded while hidden");
                Require(!state.GetProperty("running").GetBoolean(), "hidden model is not rendering");
                Require(reads == 0, "hidden model does not read input");
                var frame = state.GetProperty("frame").GetInt32();
                await Task.Delay(400);
                state = await Snapshot(core);
                Require(state.GetProperty("frame").GetInt32() == frame, "hidden frame count stays stable");
                window.Show();
                await Task.Delay(500);
                state = await Snapshot(core);
                Require(state.GetProperty("running").GetBoolean(), "show resumes rendering");
                Require(reads > 0, "show resumes input sampling");
                window.Close();
                window = null;
                var readsAtClose = reads;
                await Task.Delay(300);
                Require(reads == readsAtClose, "close stops input sampling");

                var closedBeforeCore = false;
                var closingWindow = new FloatingStatsWindow(
                    new AppSettings { DesktopPetModelPath = Path.GetFullPath(args[1]) },
                    () => { }, Path.Combine(Path.GetFullPath(args[0]), "closing-profile"),
                    _ => { throw new InvalidOperationException("closed window sampled input"); });
                window = closingWindow;
                var closingView = FindWebView((DependencyObject)closingWindow.FindName("DesktopPetHost"));
                var closed = new TaskCompletionSource<bool>();
                closingWindow.Loaded += (_, _) => closingWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    closedBeforeCore = closingView.CoreWebView2 == null;
                    closingWindow.Close();
                    closed.TrySetResult(true);
                }));
                closingWindow.Show();
                await WithTimeout(closed.Task);
                window = null;
                Require(closedBeforeCore, "second window closed before Core initialization completes");
                await Task.Delay(1000);
                Require(!closingWindow.IsVisible, "initialization does not reopen a closed window");
                exitCode = 0;
            }
            catch (Exception error)
            {
                File.AppendAllText(log, "FAIL " + error + Environment.NewLine);
            }
            finally
            {
                window?.Close();
                File.AppendAllText(log, "EXIT " + exitCode + Environment.NewLine);
                app.Shutdown(exitCode);
            }

            void Require(bool condition, string label)
            {
                File.AppendAllText(log, (condition ? "PASS " : "FAIL ") + label + Environment.NewLine);
                if (!condition) throw new InvalidOperationException(label);
            }
        };
        app.Run();
        return exitCode;
    }

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(5000)) != task) throw new TimeoutException("viewer response timed out");
        return await task;
    }

    private static async Task<JsonElement> Snapshot(CoreWebView2 core)
    {
        var received = new TaskCompletionSource<JsonElement>();
        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            using var json = JsonDocument.Parse(e.TryGetWebMessageAsString());
            if (json.RootElement.GetProperty("type").GetString() == "stats") received.TrySetResult(json.RootElement.Clone());
        }
        core.WebMessageReceived += OnMessage;
        try
        {
            core.PostWebMessageAsJson("{\"type\":\"stats\"}");
            return await WithTimeout(received.Task);
        }
        finally { core.WebMessageReceived -= OnMessage; }
    }

    private static WebView2CompositionControl FindWebView(DependencyObject root)
    {
        if (root is WebView2CompositionControl view) return view;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var result = FindWebView(VisualTreeHelper.GetChild(root, i));
            if (result != null) return result;
        }
        return null;
    }
}
