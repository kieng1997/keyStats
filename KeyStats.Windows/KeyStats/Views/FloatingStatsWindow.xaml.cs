using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KeyStats.Helpers;
using KeyStats.Models;
using KeyStats.Services;
using KeyStats.Views.Controls;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace KeyStats.Views;

public partial class FloatingStatsWindow : Window
{
    private const double EdgeMargin = 16;
    private readonly DispatcherTimer _positionSaveTimer;
    private readonly DesktopPetView _desktopPet;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly Func<string?>? _chooseModelFolder;
    private bool _isLoaded;
    private bool _isRestoringPosition;

    /// <summary>The preset actually loaded right now; drives the context-menu check marks.</summary>
    private string? _activePresetId;

    /// <summary>Bundled presets: fixed ids matched against settings, never built from setting text.</summary>
    private static readonly (string Id, string Title)[] Presets =
    {
        ("ds-standard", "DS-标准模式"),
        ("nailin-keyboard", "乃琳-键盘模型")
    };

    /// <summary>Production entry point: reads and persists the live application settings.</summary>
    public FloatingStatsWindow()
        : this(null, null, null, null)
    {
    }

    /// <summary>
    /// Isolation seam for tests and smoke hosts: the window never touches the real settings or
    /// statistics singleton when an injected <paramref name="settings"/> is supplied.
    /// </summary>
    public FloatingStatsWindow(
        AppSettings? settings,
        Action? saveSettings,
        string? webViewProfileFolder,
        Func<IReadOnlyList<int>, DesktopPetInputFrame>? readInputFrame,
        Func<string?>? chooseModelFolder = null)
    {
        InitializeComponent();
        _chooseModelFolder = chooseModelFolder;
        _positionSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _positionSaveTimer.Tick += PositionSaveTimer_Tick;

        if (settings == null)
        {
            _settings = StatsManager.Instance.Settings;
            _saveSettings = StatsManager.Instance.SaveSettings;
        }
        else
        {
            _settings = settings;
            _saveSettings = saveSettings ?? (() => { });
        }

        _desktopPet = new DesktopPetView(webViewProfileFolder, readInputFrame);
        _desktopPet.ErrorOccurred += ShowDesktopPetError;
        DesktopPetHost.Children.Add(_desktopPet);

        Topmost = _settings.FloatingStatsTopmost;
        UpdateDragCursor();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        LocationChanged += OnLocationChanged;
        SizeChanged += OnSizeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void ShowWindow()
    {
        if (!IsVisible)
        {
            Show();
        }
    }

    public void ApplyBehaviorSettings()
    {
        Topmost = _settings.FloatingStatsTopmost;
        UpdateDragCursor();
        EnsureVisiblePosition();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        NativeInterop.HideWindowFromSwitcher(new WindowInteropHelper(this).Handle);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestorePosition();
        RootBorder.ContextMenu = BuildContextMenu();
        _isLoaded = true;
        LoadDesktopPetModel();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _positionSaveTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _desktopPet.ErrorOccurred -= ShowDesktopPetError;
        _desktopPet.Dispose();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(EnsureVisiblePosition));
    }

    private void ChooseModelButton_Click(object sender, RoutedEventArgs e)
    {
        ChooseModel();
    }

    /// <summary>
    /// Asks for the extracted model folder. The Mver package root is the folder the user picks, so
    /// choosing a model file inside <c>img/standard/&lt;model&gt;</c> is never necessary and the
    /// configured key overlays are found. An imported model clears the preset selection.
    /// </summary>
    private void ChooseModel()
    {
        var selectedFolder = _chooseModelFolder != null
            ? _chooseModelFolder()
            : ShowFolderPicker();

        if (string.IsNullOrWhiteSpace(selectedFolder))
        {
            return;
        }

        if (!TryApplyDesktopPetModel(selectedFolder))
        {
            return;
        }

        _activePresetId = null;
        _settings.DesktopPetPresetId = null;
        _settings.DesktopPetModelPath = selectedFolder;
        _saveSettings();
    }

    private string? ShowFolderPicker()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择解压后的模型文件夹（包含 config.json 或 .model3.json）",
            ShowNewFolderButton = false
        };

        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private void LoadDesktopPetModel()
    {
        // A valid preset id wins: bundled models ship with the app, so a moved program directory
        // or a fresh machine still loads the same preset.
        if (_settings.DesktopPetPresetId != null &&
            TryApplyDesktopPetModel(ResolvePresetPath(_settings.DesktopPetPresetId)))
        {
            _activePresetId = _settings.DesktopPetPresetId;
            return;
        }

        // No (valid) preset: keep the legacy custom path while it still loads.
        if (_settings.DesktopPetModelPath != null && TryApplyDesktopPetModel(_settings.DesktopPetModelPath))
        {
            _activePresetId = null;
            return;
        }

        // First launch or a lost model: fall back to the bundled DS preset without any picker.
        if (TryApplyDesktopPetModel(ResolvePresetPath(Presets[0].Id)))
        {
            _activePresetId = Presets[0].Id;
            return;
        }

        _activePresetId = null;
        ShowDesktopPetChooser();
    }

    private static string? ResolvePresetPath(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            return null;
        }

        foreach (var preset in Presets)
        {
            if (string.Equals(preset.Id, presetId, StringComparison.Ordinal))
            {
                return Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Resources", "DesktopPet", "Models", preset.Id);
            }
        }

        return null;
    }

    /// <summary>
    /// Loads and shows a model, reporting success so callers only persist choices that actually
    /// work. A blank path is a silent failure (no error page for the "nothing chosen yet" state).
    /// </summary>
    private bool TryApplyDesktopPetModel(string? rootPath)
    {
        string? error = null;
        if (string.IsNullOrWhiteSpace(rootPath) ||
            !DesktopPetModelLoader.TryLoad(rootPath, out var package, out error))
        {
            if (!string.IsNullOrWhiteSpace(rootPath))
            {
                ShowDesktopPetError(error ?? "模型加载失败");
            }

            return false;
        }

        DesktopPetErrorText.Visibility = Visibility.Collapsed;
        ChooseModelButton.Visibility = Visibility.Collapsed;
        _desktopPet.Visibility = Visibility.Visible;
        _desktopPet.LoadModel(package);
        return true;
    }

    /// <summary>Selects a bundled preset; only a successful load persists the choice.</summary>
    private void SelectPreset(string presetId)
    {
        var presetPath = ResolvePresetPath(presetId);
        if (presetPath == null || !TryApplyDesktopPetModel(presetPath))
        {
            return;
        }

        _activePresetId = presetId;
        _settings.DesktopPetPresetId = presetId;
        _settings.DesktopPetModelPath = null;
        _saveSettings();
    }

    private void ShowDesktopPetChooser()
    {
        _desktopPet.LoadModel(null);
        _desktopPet.Visibility = Visibility.Collapsed;
        DesktopPetErrorText.Visibility = Visibility.Collapsed;
        ChooseModelButton.Visibility = Visibility.Visible;
    }

    private void ShowDesktopPetError(string message)
    {
        _desktopPet.Visibility = Visibility.Collapsed;
        ChooseModelButton.Visibility = Visibility.Visible;
        DesktopPetErrorText.Text = message;
        DesktopPetErrorText.Visibility = Visibility.Visible;
    }

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            App.CurrentApp?.ShowMainWindow();
            e.Handled = true;
            return;
        }

        if (_settings.FloatingStatsPositionLocked)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse may be released before WPF enters the native drag loop.
        }
        finally
        {
            EnsureVisiblePosition();
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var lockPositionItem = new MenuItem
        {
            Header = KeyStats.Properties.Strings.FloatingStats_LockPosition,
            IsCheckable = true,
            IsChecked = _settings.FloatingStatsPositionLocked
        };
        lockPositionItem.Click += (_, _) =>
        {
            var isLocked = lockPositionItem.IsChecked;
            var settings = _settings;
            settings.FloatingStatsPositionLocked = isLocked;
            _saveSettings();
            ApplyBehaviorSettings();
        };
        menu.Items.Add(lockPositionItem);

        // The two fixed presets are root-level leaf items: the production MenuItem template has
        // no ItemsPresenter/Popup, so a nested submenu can never open (a real user entry blocker).
        foreach (var preset in Presets)
        {
            var presetItem = new MenuItem
            {
                Header = preset.Title,
                IsCheckable = true,
                Tag = preset.Id,
                IsChecked = string.Equals(_activePresetId, preset.Id, StringComparison.Ordinal)
            };
            presetItem.Click += (_, _) =>
            {
                // Clicking the already-active preset must not unselect it.
                if (!string.Equals(_activePresetId, preset.Id, StringComparison.Ordinal))
                {
                    SelectPreset(preset.Id);
                }

                presetItem.IsChecked = string.Equals(_activePresetId, preset.Id, StringComparison.Ordinal);
                SyncPresetChecks(menu);
            };
            menu.Items.Add(presetItem);
        }

        var importItem = new MenuItem
        {
            Header = "导入其他模型…"
        };
        importItem.Click += (_, _) => ChooseModel();
        menu.Items.Add(importItem);
        var hideItem = new MenuItem
        {
            Header = KeyStats.Properties.Strings.FloatingStats_Hide
        };
        hideItem.Click += (_, _) =>
        {
            App.CurrentApp?.SetFloatingStatsVisible(false);
        };
        menu.Items.Add(hideItem);
        menu.Items.Add(new Separator());

        var settingsItem = new MenuItem
        {
            Header = KeyStats.Properties.Strings.Tray_Settings
        };
        settingsItem.Click += (_, _) =>
        {
            App.CurrentApp?.ShowSettingsWindow();
        };
        menu.Items.Add(settingsItem);
        menu.Opened += (_, _) =>
        {
            lockPositionItem.IsChecked = _settings.FloatingStatsPositionLocked;
            SyncPresetChecks(menu);
        };

        return menu;
    }

    /// <summary>Marks only the currently loaded preset; called when the menu opens and after a click.</summary>
    private void SyncPresetChecks(System.Windows.Controls.ItemsControl itemsHost)
    {
        foreach (var item in itemsHost.Items)
        {
            if (item is MenuItem menuItem && menuItem.Tag is string presetId)
            {
                menuItem.IsChecked = string.Equals(presetId, _activePresetId, StringComparison.Ordinal);
            }
        }
    }

    private void UpdateDragCursor()
    {
        RootBorder.Cursor = _settings.FloatingStatsPositionLocked
            ? Cursors.Arrow
            : Cursors.SizeAll;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The window has a fixed size now, so only a display change can move it out of bounds.
        if (!_isLoaded)
        {
            return;
        }

        EnsureVisiblePosition();
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!_isLoaded || _isRestoringPosition)
        {
            return;
        }

        ClampCurrentPositionToWorkingArea();
        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
    }

    private void PositionSaveTimer_Tick(object? sender, EventArgs e)
    {
        _positionSaveTimer.Stop();
        SaveCurrentPosition();
    }

    private void SaveCurrentPosition()
    {
        var settings = _settings;
        settings.FloatingStatsLeft = Left;
        settings.FloatingStatsTop = Top;
        var monitorDeviceName = GetCurrentMonitorDeviceName();
        if (!string.IsNullOrWhiteSpace(monitorDeviceName))
        {
            settings.FloatingStatsMonitorDeviceName = monitorDeviceName;
        }
        _saveSettings();
    }

    private void RestorePosition()
    {
        var workingAreas = GetWorkingAreasInDips();
        var primaryArea = workingAreas.Count > 0
            ? workingAreas[0]
            : new WorkingAreaInfo(string.Empty, SystemParameters.WorkArea);
        var settings = _settings;
        var savedArea = FindWorkingAreaByDeviceName(
            settings.FloatingStatsMonitorDeviceName,
            workingAreas);
        var preferredArea = savedArea ?? primaryArea;
        var requestedBounds = settings.FloatingStatsLeft.HasValue && settings.FloatingStatsTop.HasValue
            ? new Rect(settings.FloatingStatsLeft.Value, settings.FloatingStatsTop.Value, ActualWidth, Height)
            : new Rect(
                preferredArea.Bounds.Right - ActualWidth - EdgeMargin,
                preferredArea.Bounds.Top + EdgeMargin,
                ActualWidth,
                Height);

        var targetArea = savedArea ?? FindBestWorkingArea(requestedBounds, workingAreas) ?? preferredArea;
        var clamped = ClampToArea(requestedBounds, targetArea.Bounds);

        _isRestoringPosition = true;
        try
        {
            Left = clamped.Left;
            Top = clamped.Top;
        }
        finally
        {
            _isRestoringPosition = false;
        }
    }

    private void EnsureVisiblePosition()
    {
        if (!_isLoaded)
        {
            return;
        }

        ClampCurrentPositionToWorkingArea();
        _positionSaveTimer.Stop();
        SaveCurrentPosition();
    }

    private void ClampCurrentPositionToWorkingArea()
    {
        var workingAreas = GetWorkingAreasInDips();
        var preferredArea = workingAreas.Count > 0
            ? workingAreas[0]
            : new WorkingAreaInfo(string.Empty, SystemParameters.WorkArea);
        var bounds = new Rect(Left, Top, ActualWidth, Height);
        var currentArea = FindWorkingAreaByDeviceName(GetCurrentMonitorDeviceName(), workingAreas);
        var targetArea = currentArea ?? FindBestWorkingArea(bounds, workingAreas) ?? preferredArea;
        var clamped = ClampToArea(bounds, targetArea.Bounds);

        _isRestoringPosition = true;
        try
        {
            Left = clamped.Left;
            Top = clamped.Top;
        }
        finally
        {
            _isRestoringPosition = false;
        }
    }

    private List<WorkingAreaInfo> GetWorkingAreasInDips()
    {
        var areas = new List<WorkingAreaInfo>();
        var source = PresentationSource.FromVisual(this);
        var fallbackTransform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

        foreach (var screen in Forms.Screen.AllScreens)
        {
            var area = new WorkingAreaInfo(
                screen.DeviceName,
                MonitorGeometryHelper.GetWorkingAreaInDips(screen, fallbackTransform));
            if (screen.Primary)
            {
                areas.Insert(0, area);
            }
            else
            {
                areas.Add(area);
            }
        }

        return areas;
    }

    private string? GetCurrentMonitorDeviceName()
    {
        var handle = new WindowInteropHelper(this).Handle;
        return handle == IntPtr.Zero
            ? null
            : Forms.Screen.FromHandle(handle).DeviceName;
    }

    private static WorkingAreaInfo? FindWorkingAreaByDeviceName(
        string? deviceName,
        IReadOnlyList<WorkingAreaInfo> workingAreas)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        foreach (var area in workingAreas)
        {
            if (string.Equals(area.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return area;
            }
        }

        return null;
    }

    private static WorkingAreaInfo? FindBestWorkingArea(
        Rect bounds,
        IReadOnlyList<WorkingAreaInfo> workingAreas)
    {
        WorkingAreaInfo? bestArea = null;
        var bestIntersection = 0.0;
        foreach (var area in workingAreas)
        {
            var intersection = Rect.Intersect(bounds, area.Bounds);
            var intersectionSize = intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
            if (intersectionSize <= bestIntersection)
            {
                continue;
            }

            bestIntersection = intersectionSize;
            bestArea = area;
        }

        return bestArea;
    }

    private static Rect ClampToArea(Rect bounds, Rect workingArea)
    {
        var left = Math.Max(workingArea.Left, Math.Min(bounds.Left, workingArea.Right - bounds.Width));
        var top = Math.Max(workingArea.Top, Math.Min(bounds.Top, workingArea.Bottom - bounds.Height));
        return new Rect(left, top, bounds.Width, bounds.Height);
    }

    private sealed class WorkingAreaInfo
    {
        public WorkingAreaInfo(string deviceName, Rect bounds)
        {
            DeviceName = deviceName;
            Bounds = bounds;
        }

        public string DeviceName { get; }

        public Rect Bounds { get; }
    }
}
