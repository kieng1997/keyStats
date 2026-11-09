using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using KeyStats.Helpers;
using KeyStats.Services;
using KeyStats.ViewModels;
using KeyStats.Views;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace KeyStats;

public partial class App : System.Windows.Application
{
    private TrayIconHost? _trayIcon;
    private TrayIconViewModel? _trayIconViewModel;
    private TrayContextMenuHost? _trayContextMenuHost;
    private DispatcherTimer? _trayRecoveryTimer;
    private int _trayRecoveryAttempts;
    private const int TrayRecoveryMaxAttempts = 10;
    private bool _isExiting;
    private SettingsWindow? _settingsWindow;
    private NotificationSettingsWindow? _notificationSettingsWindow;
    private MouseCalibrationWindow? _mouseCalibrationWindow;
    private AppStatsWindow? _appStatsWindow;
    private KeyboardHeatmapWindow? _keyboardHeatmapWindow;
    private KeyHistoryWindow? _keyHistoryWindow;
    private SyncSettingsWindow? _syncSettingsWindow;
    private FloatingStatsWindow? _floatingStatsWindow;
    private MenuItem? _floatingStatsMenuItem;
    private DispatcherTimer? _floatingStatsVisibilityTimer;
    private System.Threading.Mutex? _singleInstanceMutex;
    private string? _appVersion;
    private SyncCoordinator? _syncCoordinator;
    private long _lastResumeRecoveryTicks;
    private bool _isFloatingStatsHiddenForFullscreen;

    protected override void OnStartup(StartupEventArgs e)
    {
#if DEBUG
        // 在 Debug 模式下分配控制台窗口，方便查看输出
        AllocConsole();
        Console.WriteLine("=== KeyStats Debug Console ===");
#endif
        base.OnStartup(e);

        // Global exception handlers
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            Console.WriteLine($"=== UNHANDLED EXCEPTION ===\n{args.ExceptionObject}");
        };
        DispatcherUnhandledException += (s, args) =>
        {
            Console.WriteLine($"=== DISPATCHER EXCEPTION ===\n{args.Exception}");
            args.Handled = true;
        };

        try
        {
            Console.WriteLine("KeyStats starting...");

            // Apply language preference BEFORE any UI loads.
            // StatsManager.Instance triggers settings.json load on first access.
            var preliminarySettings = StatsManager.Instance.Settings;
            LocalizationManager.ApplyAtStartup(preliminarySettings.LanguagePreference);

            // Ensure single instance — retry up to 3 attempts (500ms apart) so a
            // language-switch relaunch doesn't lose the race against the old
            // process's OnExit cleanup.
            System.Threading.Mutex? mutex = null;
            bool createdNew = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                mutex = new System.Threading.Mutex(true, "KeyStats_SingleInstance", out createdNew);
                if (createdNew) break;
                mutex.Dispose();
                mutex = null;
                System.Threading.Thread.Sleep(500);
            }

            if (!createdNew)
            {
                MessageBox.Show(KeyStats.Properties.Strings.Error_AppAlreadyRunning,
                                KeyStats.Properties.Strings.App_Name,
                                MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            _singleInstanceMutex = mutex;

            EnsureStartMenuShortcut();

            Console.WriteLine("Applying theme...");
            ThemeManager.Instance.Initialize();
            RegisterSystemEventHandlers();

            Console.WriteLine("Initializing services...");
            // Initialize services
            var statsManager = StatsManager.Instance;
            StartupManager.Instance.SyncWithSettings();
            _appVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            SyncServiceConfiguration.TryCreateBaseUri(out var syncServiceUri);
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KeyStats");
            _syncCoordinator = new SyncCoordinator(
                statsManager,
                dataFolder,
                syncServiceUri,
                _appVersion);
            _syncCoordinator.Start();
            InputMonitorService.Instance.StartMonitoring();

            Console.WriteLine("Creating tray icon...");
            _trayIconViewModel = new TrayIconViewModel();
            _trayIconViewModel.PropertyChanged += OnTrayIconViewModelPropertyChanged;
            _trayContextMenuHost = new TrayContextMenuHost(CreateContextMenu());
            _trayIcon = new TrayIconHost();
            _trayIcon.MouseClick += OnTrayIconMouseClick;
            _trayIcon.RecoveryRequested += OnTrayRecoveryRequested;
            if (!_trayIcon.TryRefresh(_trayIconViewModel.TrayIcon, _trayIconViewModel.TooltipText))
            {
                ScheduleTrayRecovery("startup");
            }

            if (statsManager.Settings.FloatingStatsEnabled)
            {
                ShowFloatingStatsWindow();
            }

            Console.WriteLine("Tray integration initialized; failed registrations will be retried.");
            Console.WriteLine("App is running. Look for the icon in the system tray.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during startup: {ex}");
            MessageBox.Show(string.Format(KeyStats.Properties.Strings.Error_StartupFailedFormat, ex.Message),
                            KeyStats.Properties.Strings.Error_AppErrorTitle,
                            MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private System.Windows.Controls.ContextMenu CreateContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var openMainWindowItem = new System.Windows.Controls.MenuItem { Header = KeyStats.Properties.Strings.Tray_OpenMainWindow };
        openMainWindowItem.Click += (s, e) =>
        {
            _trayIconViewModel?.ShowMainWindow();
        };
        menu.Items.Add(openMainWindowItem);

        _floatingStatsMenuItem = new System.Windows.Controls.MenuItem
        {
            Header = KeyStats.Properties.Strings.Tray_ShowFloatingStats,
            IsCheckable = true,
            IsChecked = StatsManager.Instance.Settings.FloatingStatsEnabled
        };
        _floatingStatsMenuItem.Click += (s, e) =>
        {
            var menuItem = (System.Windows.Controls.MenuItem)s!;
            SetFloatingStatsVisible(menuItem.IsChecked);
        };
        menu.Items.Add(_floatingStatsMenuItem);

        var settingsItem = new System.Windows.Controls.MenuItem { Header = KeyStats.Properties.Strings.Tray_Settings };
        settingsItem.Click += (s, e) =>
        {
            ShowSettingsWindow();
        };
        menu.Items.Add(settingsItem);

        var startupItem = new System.Windows.Controls.MenuItem
        {
            Header = KeyStats.Properties.Strings.Tray_StartAtLogin,
            IsCheckable = true,
            IsChecked = StartupManager.Instance.IsEnabled
        };
        startupItem.Click += (s, e) =>
        {
            var menuItem = (System.Windows.Controls.MenuItem)s!;
            try
            {
                StartupManager.Instance.SetEnabled(menuItem.IsChecked);
            }
            catch (Exception ex)
            {
                // 失败时按实际状态回填，而不是简单反转（可能只有部分入口被清理）。
                menuItem.IsChecked = StartupManager.Instance.IsEnabled;
                Console.WriteLine($"Error setting startup: {ex.Message}");
                MessageBox.Show(KeyStats.Properties.Strings.Error_StartupTaskFailed,
                                KeyStats.Properties.Strings.Error_AppErrorTitle,
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        menu.Items.Add(startupItem);

        var keyHistoryItem = new System.Windows.Controls.MenuItem { Header = KeyStats.Properties.Strings.Tray_KeyHistory };
        keyHistoryItem.Click += (s, e) =>
        {
            ShowKeyHistoryWindow();
        };
        menu.Items.Add(keyHistoryItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var quitItem = new System.Windows.Controls.MenuItem { Header = KeyStats.Properties.Strings.Tray_Quit };
        quitItem.Click += (s, e) =>
        {
            _trayIconViewModel?.QuitCommand.Execute(null);
        };
        menu.Items.Add(quitItem);

        return menu;
    }

    public void ShowNotificationSettings()
    {
        if (_notificationSettingsWindow != null && _notificationSettingsWindow.IsVisible)
        {
            _notificationSettingsWindow.Activate();
            return;
        }

        _notificationSettingsWindow = new NotificationSettingsWindow();
        _notificationSettingsWindow.Closed += (_, _) => _notificationSettingsWindow = null;
        _notificationSettingsWindow.Show();
    }

    public void ShowMouseCalibration()
    {
        if (_mouseCalibrationWindow != null && _mouseCalibrationWindow.IsVisible)
        {
            _mouseCalibrationWindow.Activate();
            return;
        }

        _mouseCalibrationWindow = new MouseCalibrationWindow();
        _mouseCalibrationWindow.Closed += (_, _) => _mouseCalibrationWindow = null;
        _mouseCalibrationWindow.Show();
        _mouseCalibrationWindow.Activate();
    }

    public void ShowSettingsWindow()
    {
        if (_settingsWindow != null && _settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void ShowSyncSettingsWindow()
    {
        if (_syncSettingsWindow != null && _syncSettingsWindow.IsVisible)
        {
            _syncSettingsWindow.Activate();
            return;
        }

        _syncSettingsWindow = new SyncSettingsWindow();
        _syncSettingsWindow.Closed += (_, _) => _syncSettingsWindow = null;
        _syncSettingsWindow.Show();
        _syncSettingsWindow.Activate();
    }

    public void ShowStatsPanel()
    {
        _trayIconViewModel?.ShowStatsCommand.Execute(null);
    }

    public void ShowFloatingStatsWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ShowFloatingStatsWindow));
            return;
        }

        StartFloatingStatsVisibilityMonitor();
        if (FullscreenWindowDetector.IsForegroundWindowFullscreen())
        {
            _isFloatingStatsHiddenForFullscreen = true;
            _floatingStatsWindow?.Hide();
            return;
        }

        _isFloatingStatsHiddenForFullscreen = false;

        if (_floatingStatsWindow != null)
        {
            _floatingStatsWindow.ShowWindow();
            return;
        }

        _floatingStatsWindow = new FloatingStatsWindow();
        _floatingStatsWindow.Closed += (_, _) => _floatingStatsWindow = null;
        _floatingStatsWindow.ShowWindow();
    }

    public void SetFloatingStatsVisible(bool isVisible)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetFloatingStatsVisible(isVisible)));
            return;
        }

        var settings = StatsManager.Instance.Settings;
        if (settings.FloatingStatsEnabled != isVisible)
        {
            settings.FloatingStatsEnabled = isVisible;
            StatsManager.Instance.SaveSettings();
        }

        if (_floatingStatsMenuItem != null)
        {
            _floatingStatsMenuItem.IsChecked = isVisible;
        }

        if (isVisible)
        {
            ShowFloatingStatsWindow();
            return;
        }

        StopFloatingStatsVisibilityMonitor();
        _isFloatingStatsHiddenForFullscreen = false;
        _floatingStatsWindow?.Close();
        _floatingStatsWindow = null;
    }

    private void StartFloatingStatsVisibilityMonitor()
    {
        if (_floatingStatsVisibilityTimer == null)
        {
            _floatingStatsVisibilityTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _floatingStatsVisibilityTimer.Tick += OnFloatingStatsVisibilityTimerTick;
        }

        _floatingStatsVisibilityTimer.Start();
    }

    private void StopFloatingStatsVisibilityMonitor()
    {
        if (_floatingStatsVisibilityTimer == null)
        {
            return;
        }

        _floatingStatsVisibilityTimer.Stop();
        _floatingStatsVisibilityTimer.Tick -= OnFloatingStatsVisibilityTimerTick;
        _floatingStatsVisibilityTimer = null;
    }

    private void OnFloatingStatsVisibilityTimerTick(object? sender, EventArgs e)
    {
        if (!StatsManager.Instance.Settings.FloatingStatsEnabled)
        {
            StopFloatingStatsVisibilityMonitor();
            return;
        }

        var shouldHideForFullscreen = FullscreenWindowDetector.IsForegroundWindowFullscreen();
        if (shouldHideForFullscreen)
        {
            if (_isFloatingStatsHiddenForFullscreen)
            {
                return;
            }

            _isFloatingStatsHiddenForFullscreen = true;
            _floatingStatsWindow?.Hide();
            return;
        }

        if (!_isFloatingStatsHiddenForFullscreen)
        {
            return;
        }

        _isFloatingStatsHiddenForFullscreen = false;
        ShowFloatingStatsWindow();
    }

    public void ApplyFloatingStatsBehaviorSettings()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ApplyFloatingStatsBehaviorSettings));
            return;
        }

        _floatingStatsWindow?.ApplyBehaviorSettings();
    }

    public void ShowMainWindow()
    {
        _trayIconViewModel?.ShowMainWindow();
    }

    public void ShowAppStatsWindow()
    {
        if (_appStatsWindow != null && _appStatsWindow.IsVisible)
        {
            _appStatsWindow.Activate();
            return;
        }

        _appStatsWindow = new AppStatsWindow();
        _appStatsWindow.Closed += (_, _) => _appStatsWindow = null;
        _appStatsWindow.Show();
        _appStatsWindow.Activate();
    }

    public void ShowKeyboardHeatmapWindow()
    {
        if (_keyboardHeatmapWindow != null && _keyboardHeatmapWindow.IsVisible)
        {
            _keyboardHeatmapWindow.Activate();
            return;
        }

        _keyboardHeatmapWindow = new KeyboardHeatmapWindow();
        _keyboardHeatmapWindow.Closed += (_, _) => _keyboardHeatmapWindow = null;
        _keyboardHeatmapWindow.Show();
        _keyboardHeatmapWindow.Activate();
    }

    public void ShowKeyHistoryWindow()
    {
        if (_keyHistoryWindow != null && _keyHistoryWindow.IsVisible)
        {
            _keyHistoryWindow.Activate();
            return;
        }

        _keyHistoryWindow = new KeyHistoryWindow();
        _keyHistoryWindow.Closed += (_, _) => _keyHistoryWindow = null;
        _keyHistoryWindow.Show();
        _keyHistoryWindow.Activate();
    }

    public void ExportData()
    {
        // 确保在 UI 线程上执行
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ExportData);
            return;
        }

        try
        {
            var dialog = new SaveFileDialog
            {
                Title = KeyStats.Properties.Strings.Dialog_ExportTitle,
                Filter = KeyStats.Properties.Strings.Dialog_JsonFilter,
                DefaultExt = ".json",
                AddExtension = true,
                FileName = MakeExportFileName()
            };

            // 创建一个隐藏窗口作为对话框的 owner，避免在无窗口应用中崩溃
            var hiddenWindow = new Window
            {
                Width = 0,
                Height = 0,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Visibility = Visibility.Hidden
            };
            hiddenWindow.Show();

            try
            {
                if (dialog.ShowDialog(hiddenWindow) != true || string.IsNullOrWhiteSpace(dialog.FileName))
                {
                    return;
                }

                var data = StatsManager.Instance.ExportStatsData();
                File.WriteAllBytes(dialog.FileName, data);

                // 使用 Toast 通知显示导出成功
                new ToastContentBuilder()
                    .AddText(KeyStats.Properties.Strings.Toast_ExportSuccess_Title)
                    .AddText(string.Format(KeyStats.Properties.Strings.Toast_ExportSuccess_BodyFormat, Path.GetFileName(dialog.FileName)))
                    .Show();
            }
            finally
            {
                hiddenWindow.Close();
            }
        }
        catch (Exception ex)
        {
            new ToastContentBuilder()
                .AddText(KeyStats.Properties.Strings.Toast_ExportFailed_Title)
                .AddText(string.Format(KeyStats.Properties.Strings.Toast_ExportFailed_BodyFormat, ex.Message))
                .Show();
        }
    }

    public void ImportData()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ImportData);
            return;
        }

        try
        {
            if (_syncCoordinator?.BlocksImport == true)
            {
                throw new InvalidOperationException(KeyStats.Properties.Strings.Error_ImportDisabledWhileSyncing);
            }

            var dialog = new OpenFileDialog
            {
                Title = KeyStats.Properties.Strings.Dialog_ImportTitle,
                Filter = KeyStats.Properties.Strings.Dialog_JsonFilter,
                DefaultExt = ".json",
                CheckFileExists = true,
                Multiselect = false
            };

            var hiddenWindow = new Window
            {
                Width = 0,
                Height = 0,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Visibility = Visibility.Hidden
            };
            hiddenWindow.Show();

            try
            {
                if (dialog.ShowDialog(hiddenWindow) != true || string.IsNullOrWhiteSpace(dialog.FileName))
                {
                    return;
                }

                var selectedFile = dialog.FileName;
                hiddenWindow.Close();

                // Show Win11-style import mode dialog
                var mode = ImportModeDialog.Show();
                if (mode == null)
                {
                    return;
                }

                var data = File.ReadAllBytes(selectedFile);
                StatsManager.Instance.ImportStatsData(data, mode.Value);

                var modeLabel = mode == StatsManager.ImportMode.Overwrite
                    ? KeyStats.Properties.Strings.ImportMode_Overwrite
                    : KeyStats.Properties.Strings.ImportMode_Merge;
                new ToastContentBuilder()
                    .AddText(KeyStats.Properties.Strings.Toast_ImportSuccess_Title)
                    .AddText(string.Format(KeyStats.Properties.Strings.Toast_ImportSuccess_BodyFormat, modeLabel, Path.GetFileName(selectedFile)))
                    .Show();

                return;
            }
            finally
            {
                if (hiddenWindow.IsVisible)
                {
                    hiddenWindow.Close();
                }
            }
        }
        catch (Exception ex)
        {
            new ToastContentBuilder()
                .AddText(KeyStats.Properties.Strings.Toast_ImportFailed_Title)
                .AddText(string.Format(KeyStats.Properties.Strings.Toast_ImportFailed_BodyFormat, ex.Message))
                .Show();
        }
    }

    private static string MakeExportFileName()
    {
        var dateString = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"KeyStats-Export-{dateString}.json";
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;
        StopTrayRecovery();
        StopFloatingStatsVisibilityMonitor();
        UnregisterSystemEventHandlers();
        if (_trayIconViewModel != null)
        {
            _trayIconViewModel.PropertyChanged -= OnTrayIconViewModelPropertyChanged;
        }
        _trayContextMenuHost?.Dispose();
        _trayContextMenuHost = null;
        if (_trayIcon != null)
        {
            _trayIcon.MouseClick -= OnTrayIconMouseClick;
            _trayIcon.RecoveryRequested -= OnTrayRecoveryRequested;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayIconViewModel?.Cleanup();
        _floatingStatsWindow?.Close();
        _floatingStatsWindow = null;
        InputMonitorService.Instance.StopMonitoring();
        _syncCoordinator?.Dispose();
        _syncCoordinator = null;
        StatsManager.Instance.Dispose();
        ThemeManager.Instance.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void EnsureStartMenuShortcut()
    {
        try
        {
            var exePath = GetCurrentExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return;
            }

            var programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (string.IsNullOrWhiteSpace(programsDir))
            {
                return;
            }

            var shortcutPath = Path.Combine(programsDir, "KeyStats.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            var existingTargetPath = GetShortcutTargetPath(shortcut);
            if (PathsEqual(existingTargetPath, exePath))
            {
                return;
            }

            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
            shortcut.WindowStyle = 1;
            shortcut.Description = KeyStats.Properties.Strings.Shortcut_Description;
            shortcut.IconLocation = exePath;
            shortcut.Save();
        }
        catch
        {
            // Ignore failures; app should still run.
        }
    }

    private static string? GetCurrentExecutablePath()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                return Path.GetFullPath(exePath);
            }
        }
        catch
        {
        }

        try
        {
            var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                return Path.GetFullPath(exePath);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? GetShortcutTargetPath(dynamic shortcut)
    {
        try
        {
            var targetPath = shortcut.TargetPath as string;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return null;
            }

            return Path.GetFullPath(targetPath);
        }
        catch
        {
            return null;
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

#if DEBUG
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
#endif

    /// <summary>
    /// 获取当前 App 实例
    /// </summary>
    public static App? CurrentApp => Current as App;
    public SyncCoordinator? SyncCoordinator => _syncCoordinator;

    private void RegisterSystemEventHandlers()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void UnregisterSystemEventHandlers()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Console.WriteLine("System suspend detected, flushing pending stats.");
                StatsManager.Instance.FlushPendingSave();
                InputMonitorService.Instance.ResetLastMousePosition();
                break;
            case PowerModes.Resume:
                ScheduleResumeRecovery("power_resume");
                break;
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                Console.WriteLine("Session lock detected, flushing pending stats.");
                StatsManager.Instance.FlushPendingSave();
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                ScheduleResumeRecovery($"session_{e.Reason}");
                break;
        }
    }

    private void ScheduleResumeRecovery(string trigger)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        // Tray recovery has its own coalescing timer, so a later unlock can retry even
        // when the heavier stats/hook recovery below is still inside its debounce window.
        Dispatcher.BeginInvoke(new Action(() => ScheduleTrayRecovery(trigger)));
        var nowTicks = DateTime.UtcNow.Ticks;
        var debounceWindowTicks = TimeSpan.FromSeconds(5).Ticks;

        while (true)
        {
            var lastTicks = Interlocked.Read(ref _lastResumeRecoveryTicks);
            if (nowTicks - lastTicks < debounceWindowTicks)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastResumeRecoveryTicks, nowTicks, lastTicks) == lastTicks)
            {
                break;
            }
        }

        Dispatcher.BeginInvoke(new Action(() => RecoverAfterResume(trigger)));
    }

    private void RecoverAfterResume(string trigger)
    {
        if (_isExiting)
        {
            return;
        }

        Console.WriteLine($"Running resume recovery triggered by {trigger}.");

        Task.Run(() =>
        {
            try
            {
                StatsManager.Instance.HandleSystemResume();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Stats resume recovery failed: {ex}");
            }

            try
            {
                InputMonitorService.Instance.HandleSystemResume();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Input monitor resume recovery failed: {ex}");
            }
        });

        _syncCoordinator?.HandleAppResume();
    }

    private void OnTrayRecoveryRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => ScheduleTrayRecovery("taskbar_created")));
    }

    private void ScheduleTrayRecovery(string trigger)
    {
        if (_isExiting || _trayIcon == null || _trayIconViewModel == null)
        {
            return;
        }

        Console.WriteLine($"Scheduling tray recovery: {trigger}.");
        // Close any stale popup/owner left open across a desktop or session change.
        _trayContextMenuHost?.Dispose();
        _trayContextMenuHost = new TrayContextMenuHost(CreateContextMenu());

        if (_trayRecoveryTimer == null)
        {
            _trayRecoveryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _trayRecoveryTimer.Tick += OnTrayRecoveryTick;
        }

        _trayRecoveryAttempts = 0;
        _trayRecoveryTimer.Stop();
        _trayRecoveryTimer.Interval = TimeSpan.FromSeconds(1);
        _trayRecoveryTimer.Start();
    }

    private void OnTrayRecoveryTick(object? sender, EventArgs e)
    {
        if (_isExiting || _trayIcon == null || _trayIconViewModel == null)
        {
            StopTrayRecovery();
            return;
        }

        _trayRecoveryAttempts++;
        try
        {
            if (_trayIcon.TryRefresh(_trayIconViewModel.TrayIcon, _trayIconViewModel.TooltipText))
            {
                Console.WriteLine($"Tray recovery succeeded on attempt {_trayRecoveryAttempts}.");
                StopTrayRecovery();
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Tray recovery attempt {_trayRecoveryAttempts} failed: {ex}");
        }

        if (_trayRecoveryAttempts >= TrayRecoveryMaxAttempts)
        {
            Console.WriteLine("Tray recovery exhausted retries; waiting for the next taskbar/session event.");
            StopTrayRecovery();
            return;
        }

        if (_trayRecoveryTimer != null)
        {
            _trayRecoveryTimer.Interval = TimeSpan.FromSeconds(2);
        }
    }

    private void StopTrayRecovery()
    {
        if (_trayRecoveryTimer == null)
        {
            return;
        }

        _trayRecoveryTimer.Stop();
        _trayRecoveryTimer.Tick -= OnTrayRecoveryTick;
        _trayRecoveryTimer = null;
    }

    private void OnTrayIconMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        if (e.Button == Forms.MouseButtons.Right)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isExiting)
                {
                    _trayContextMenuHost?.ShowAtCursor();
                }
            }));
            return;
        }

        if (e.Button != Forms.MouseButtons.Left)
        {
            return;
        }

        Console.WriteLine("Tray icon left click fired - showing stats");
        var anchorPoint = Forms.Control.MousePosition;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isExiting)
            {
                _trayIconViewModel?.ShowStats(anchorPoint);
            }
        }));
    }

    private void OnTrayIconViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_trayIcon == null || _trayIconViewModel == null)
        {
            return;
        }

        if (e.PropertyName == nameof(TrayIconViewModel.TrayIcon) ||
            e.PropertyName == nameof(TrayIconViewModel.TooltipText))
        {
            Dispatcher.BeginInvoke(new Action(() => ScheduleTrayRecovery("view_model_changed")));
        }
    }

    private sealed class TrayContextMenuHost : IDisposable
    {
        private readonly ContextMenu _menu;
        private HostWindow? _hostWindow;
        private bool _isClosingHostWindow;

        public TrayContextMenuHost(ContextMenu menu)
        {
            _menu = menu;
            _menu.Closed += OnMenuClosed;
        }

        public void ShowAtCursor()
        {
            EnsureHostWindow();

            if (_hostWindow == null)
            {
                return;
            }

            _isClosingHostWindow = false;
            var cursor = Forms.Control.MousePosition;

            // Show the host window first (at its off-screen default position)
            // so that PresentationSource becomes available for DPI queries.
            if (!_hostWindow.IsVisible)
            {
                _hostWindow.Show();
            }

            // Convert physical pixels to WPF device-independent pixels (DIPs).
            // Forms.Control.MousePosition returns physical pixels, but WPF
            // Window.Left/Top expect DIPs. Without this conversion the menu
            // lands at the wrong position on high-DPI displays.
            var source = PresentationSource.FromVisual(_hostWindow);
            var dpiScaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var dpiScaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            _hostWindow.Left = cursor.X / dpiScaleX;
            _hostWindow.Top = cursor.Y / dpiScaleY;

            _hostWindow.Activate();
            _menu.PlacementTarget = _hostWindow.Anchor;
            _menu.Placement = PlacementMode.Bottom;
            _menu.HorizontalOffset = 0;
            _menu.VerticalOffset = 0;
            _menu.IsOpen = true;
        }

        public void Dispose()
        {
            _menu.Closed -= OnMenuClosed;
            _menu.IsOpen = false;
            _menu.PlacementTarget = null;
            CloseHostWindow();
        }

        private void EnsureHostWindow()
        {
            if (_hostWindow != null)
            {
                return;
            }

            _hostWindow = new HostWindow();
            _hostWindow.Deactivated += OnHostWindowDeactivated;
            _hostWindow.Closed += OnHostWindowClosed;
        }

        private void OnHostWindowDeactivated(object? sender, EventArgs e)
        {
            if (_menu.IsOpen)
            {
                _menu.IsOpen = false;
            }
            else
            {
                CloseHostWindow();
            }
        }

        private void OnMenuClosed(object? sender, RoutedEventArgs e)
        {
            CloseHostWindow();
        }

        private void OnHostWindowClosed(object? sender, EventArgs e)
        {
            if (_hostWindow == null)
            {
                return;
            }

            _hostWindow.Deactivated -= OnHostWindowDeactivated;
            _hostWindow.Closed -= OnHostWindowClosed;
            _hostWindow = null;
            _isClosingHostWindow = false;
        }

        private void CloseHostWindow()
        {
            if (_hostWindow == null || _isClosingHostWindow)
            {
                return;
            }

            _isClosingHostWindow = true;
            _hostWindow.Close();
        }

        private sealed class HostWindow : Window
        {
            public Border Anchor { get; }

            public HostWindow()
            {
                Width = 1;
                Height = 1;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false;
                ShowActivated = true;
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                Opacity = 0.01;
                Topmost = true;
                Left = -10_000;
                Top = -10_000;

                Anchor = new Border
                {
                    Width = 1,
                    Height = 1,
                    Background = Brushes.Transparent,
                    Focusable = false
                };

                Content = Anchor;
            }
        }
    }
}
