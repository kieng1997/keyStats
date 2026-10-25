using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KeyStats.Helpers;

namespace KeyStats.Services;

public class InputMonitorService : IDisposable
{
    private static InputMonitorService? _instance;
    public static InputMonitorService Instance => _instance ??= new InputMonitorService();

    private IntPtr _keyboardHookId = IntPtr.Zero;
    private IntPtr _mouseHookId = IntPtr.Zero;
    private NativeInterop.LowLevelKeyboardProc? _keyboardProc;
    private NativeInterop.LowLevelMouseProc? _mouseProc;

    private bool _isMonitoring;
    private readonly HashSet<int> _pressedKeys = new(); // Tracks currently held keys to avoid double-counting on key repeats.
    private readonly double _mouseSampleInterval = 1.0 / 30.0; // 30 FPS
    private DateTime _lastMouseSampleTime = DateTime.MinValue;
    private System.Drawing.Point? _lastMousePosition;
    private double _accumulatedDistance = 0.0;

    // Dedicated hook thread so UI-thread stalls cannot cause hook callback timeouts.
    private Thread? _hookThread;
    private uint _hookThreadId;

    // Hook health watchdog: detects whether Windows has silently removed our hooks.
    // Each hook keeps its own timestamp so a live device cannot mask a dead one.
    private Timer? _watchdogTimer;
    private int _lastKeyboardHookCallbackTick;
    private int _lastMouseHookCallbackTick;
    private int _isReinstallingHooks;
    private NativeInterop.POINT _lastCursorPos;
    private const int WatchdogIntervalMs = 3000;
    private const int HookDeadThresholdMs = 5000;
    private const int HookInstallReadyTimeoutMs = 5000;
    private const int HookThreadStopTimeoutMs = 2000;
    private const int HookReinstallRetryCount = 3;
    private const int HookReinstallRetryDelayMs = 750;

    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLMenu = 0xA4;
    private const int VkRMenu = 0xA5;

    public event Action<string, string, string>? KeyPressed;
    public event Action<string, string>? LeftMouseClicked;
    public event Action<string, string>? RightMouseClicked;
    public event Action<string, string>? MiddleMouseClicked;
    public event Action<string, string>? SideBackMouseClicked;
    public event Action<string, string>? SideForwardMouseClicked;
    public event Action<double>? MouseMoved;
    public event Action<double, string, string>? MouseScrolled;

    private InputMonitorService() { }

    public void StartMonitoring()
    {
        if (_isMonitoring) return;

        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
        ResetTransientState();

        ResetHookHealthTimestamps(Environment.TickCount);
        StartHookThread();

        _isMonitoring = true;

        // Start the watchdog timer that periodically checks whether the hooks are still alive.
        _watchdogTimer = new Timer(WatchdogCallback, null, WatchdogIntervalMs, WatchdogIntervalMs);

        Debug.WriteLine("Input monitoring started successfully (dedicated hook thread)");
    }

    // Both install paths reset the two timestamps together so a fresh hook is never overdue.
    private void ResetHookHealthTimestamps(int nowTick)
    {
        Volatile.Write(ref _lastKeyboardHookCallbackTick, nowTick);
        Volatile.Write(ref _lastMouseHookCallbackTick, nowTick);
    }

    private void InstallHooks()
    {
        var moduleHandle = NativeInterop.GetModuleHandle(null);

        _keyboardHookId = NativeInterop.SetWindowsHookEx(
            NativeInterop.WH_KEYBOARD_LL,
            _keyboardProc!,
            moduleHandle,
            0);

        if (_keyboardHookId == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"Failed to install keyboard hook. Error code: {error}");
            throw new System.ComponentModel.Win32Exception(error, "Failed to install keyboard hook");
        }

        _mouseHookId = NativeInterop.SetWindowsHookEx(
            NativeInterop.WH_MOUSE_LL,
            _mouseProc!,
            moduleHandle,
            0);

        if (_mouseHookId == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"Failed to install mouse hook. Error code: {error}");
            if (_keyboardHookId != IntPtr.Zero)
            {
                NativeInterop.UnhookWindowsHookEx(_keyboardHookId);
                _keyboardHookId = IntPtr.Zero;
            }
            throw new System.ComponentModel.Win32Exception(error, "Failed to install mouse hook");
        }
    }

    private void ReinstallHooks()
    {
        Debug.WriteLine("Watchdog: reinstalling hooks...");

        if (!TryStopHookThread())
        {
            throw new InvalidOperationException("Existing hook thread did not stop within the timeout.");
        }

        if (_keyboardHookId != IntPtr.Zero)
        {
            NativeInterop.UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }
        if (_mouseHookId != IntPtr.Zero)
        {
            NativeInterop.UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
        }

        ResetHookHealthTimestamps(Environment.TickCount);
        StartHookThread();
        ResetTransientState();
        Debug.WriteLine("Watchdog: hooks reinstalled");
    }

    private void StartHookThread()
    {
        var hookStartup = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _hookThread = new Thread(() =>
        {
            try
            {
                _hookThreadId = NativeInterop.GetCurrentThreadId();
                InstallHooks();
                hookStartup.TrySetResult(null);

                while (NativeInterop.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    NativeInterop.TranslateMessage(ref msg);
                    NativeInterop.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                hookStartup.TrySetResult(ex);
            }
        });
        _hookThread.IsBackground = true;
        _hookThread.Name = "InputHookThread";
        _hookThread.Start();

        if (!hookStartup.Task.Wait(HookInstallReadyTimeoutMs))
        {
            if (!TryStopHookThread())
            {
                throw new TimeoutException($"Timed out waiting {HookInstallReadyTimeoutMs}ms for hook installation, and the hook thread did not stop within {HookThreadStopTimeoutMs}ms.");
            }

            throw new TimeoutException($"Timed out waiting {HookInstallReadyTimeoutMs}ms for hook installation.");
        }

        var hookError = hookStartup.Task.Result;
        if (hookError != null)
        {
            if (!TryStopHookThread())
            {
                throw new InvalidOperationException($"Hook installation failed and the hook thread did not stop within {HookThreadStopTimeoutMs}ms.", hookError);
            }

            throw hookError;
        }
    }

    private void WatchdogCallback(object? state)
    {
        if (!_isMonitoring) return;

        // A failed cursor sample must not read as movement.
        var cursorMoved = false;
        if (NativeInterop.GetCursorPos(out var currentPos))
        {
            cursorMoved = currentPos.x != _lastCursorPos.x || currentPos.y != _lastCursorPos.y;
            _lastCursorPos = currentPos;
        }

        // Probe before sampling the clock, so input arriving during the probe is compared against
        // a timestamp no older than the probe itself.
        var keyboardActivity = HasRecentKeyboardActivity();
        var keyboardTick = Volatile.Read(ref _lastKeyboardHookCallbackTick);
        var mouseTick = Volatile.Read(ref _lastMouseHookCallbackTick);
        var nowTick = Environment.TickCount;

        if (!ShouldRecoverHooks(nowTick, cursorMoved, keyboardActivity, keyboardTick, mouseTick))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _isReinstallingHooks, 1, 0) != 0)
        {
            return;
        }

        Debug.WriteLine("Watchdog: hook appears dead, reinstalling...");
        try
        {
            RetryHookRecovery("watchdog");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Watchdog: recovery failed after retries. {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _isReinstallingHooks, 0);
        }
    }

    // Each device is judged only against its own evidence and its own timestamp.
    private static bool ShouldRecoverHooks(
        int nowTick,
        bool cursorMoved,
        bool keyboardActivity,
        int lastKeyboardTick,
        int lastMouseTick)
    {
        return IsDeviceHookDead(nowTick, lastKeyboardTick, keyboardActivity)
            || IsDeviceHookDead(nowTick, lastMouseTick, cursorMoved);
    }

    private static bool IsDeviceHookDead(int nowTick, int lastCallbackTick, bool hasActivityEvidence)
    {
        if (!hasActivityEvidence) return false;

        // Unsigned elapsed survives Environment.TickCount's wrap at int.MaxValue.
        var elapsed = unchecked((uint)(nowTick - lastCallbackTick));
        return elapsed > HookDeadThresholdMs;
    }

    public void StopMonitoring()
    {
        if (!_isMonitoring) return;

        _watchdogTimer?.Dispose();
        _watchdogTimer = null;

        // Terminate the hook thread's message loop.
        if (!TryStopHookThread())
        {
            Debug.WriteLine("Failed to stop the hook thread within the timeout. Continuing shutdown cleanup.");
        }

        if (_keyboardHookId != IntPtr.Zero)
        {
            NativeInterop.UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }

        if (_mouseHookId != IntPtr.Zero)
        {
            NativeInterop.UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
        }

        ResetTransientState();

        _isMonitoring = false;
        Debug.WriteLine("Input monitoring stopped");
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Interlocked.Exchange(ref _lastKeyboardHookCallbackTick, Environment.TickCount);

            var message = (int)wParam;
            var hookStruct = Marshal.PtrToStructure<NativeInterop.KBDLLHOOKSTRUCT>(lParam);
            var vkCode = (int)hookStruct.vkCode;

            if (message == NativeInterop.WM_KEYDOWN || message == NativeInterop.WM_SYSKEYDOWN)
            {
                bool isNewKey;
                lock (_pressedKeys)
                {
                    isNewKey = _pressedKeys.Add(vkCode);
                }

                if (isNewKey)
                {
                    var keyName = KeyNameMapper.GetKeyName(vkCode, hookStruct.scanCode, hookStruct.flags);
                    var hWnd = NativeInterop.GetForegroundWindow();
                    NativeInterop.GetWindowThreadProcessId(hWnd, out uint pid);
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        InvokeSubscriber("KeyPressed", () =>
                        {
                            var activeApp = ActiveWindowManager.ResolveAppInfo(hWnd, pid);
                            KeyPressed?.Invoke(keyName, activeApp.AppName, activeApp.DisplayName);
                        });
                    });
                }
            }
            else if (message == NativeInterop.WM_KEYUP || message == NativeInterop.WM_SYSKEYUP)
            {
                lock (_pressedKeys)
                {
                    _pressedKeys.Remove(vkCode);
                }
            }
        }

        return NativeInterop.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Interlocked.Exchange(ref _lastMouseHookCallbackTick, Environment.TickCount);

            var message = (int)wParam;
            var hookStruct = Marshal.PtrToStructure<NativeInterop.MSLLHOOKSTRUCT>(lParam);

            switch (message)
            {
                case NativeInterop.WM_LBUTTONDOWN:
                case NativeInterop.WM_RBUTTONDOWN:
                case NativeInterop.WM_MBUTTONDOWN:
                    {
                        var msg = message;
                        var hWnd = NativeInterop.GetForegroundWindow();
                        NativeInterop.GetWindowThreadProcessId(hWnd, out uint pid);
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            InvokeSubscriber("MouseClicked", () =>
                            {
                                var activeApp = ActiveWindowManager.ResolveAppInfo(hWnd, pid);
                                var appName = activeApp.AppName;
                                var displayName = activeApp.DisplayName;
                                if (msg == NativeInterop.WM_LBUTTONDOWN)
                                {
                                    LeftMouseClicked?.Invoke(appName, displayName);
                                }
                                else if (msg == NativeInterop.WM_RBUTTONDOWN)
                                {
                                    RightMouseClicked?.Invoke(appName, displayName);
                                }
                                else
                                {
                                    MiddleMouseClicked?.Invoke(appName, displayName);
                                }
                            });
                        });
                    }
                    break;

                case NativeInterop.WM_XBUTTONDOWN:
                    {
                        var mouseData = hookStruct.mouseData;
                        var hWnd = NativeInterop.GetForegroundWindow();
                        NativeInterop.GetWindowThreadProcessId(hWnd, out uint pid);
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            InvokeSubscriber("SideMouseClicked", () =>
                            {
                                var activeApp = ActiveWindowManager.ResolveAppInfo(hWnd, pid);
                                var button = NativeInterop.HiWord((int)mouseData);
                                if (button == NativeInterop.XBUTTON2)
                                {
                                    SideForwardMouseClicked?.Invoke(activeApp.AppName, activeApp.DisplayName);
                                }
                                else
                                {
                                    SideBackMouseClicked?.Invoke(activeApp.AppName, activeApp.DisplayName);
                                }
                            });
                        });
                    }
                    break;

                case NativeInterop.WM_MOUSEMOVE:
                    HandleMouseMove(hookStruct.pt);
                    break;

                case NativeInterop.WM_MOUSEWHEEL:
                case NativeInterop.WM_MOUSEHWHEEL:
                    {
                        var mouseData = hookStruct.mouseData;
                        var hWnd = NativeInterop.GetForegroundWindow();
                        NativeInterop.GetWindowThreadProcessId(hWnd, out uint pid);
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            InvokeSubscriber("MouseScrolled", () =>
                            {
                                var activeApp = ActiveWindowManager.ResolveAppInfo(hWnd, pid);
                                HandleScroll(mouseData, activeApp.AppName, activeApp.DisplayName);
                            });
                        });
                    }
                    break;
            }
        }

        return NativeInterop.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private void HandleMouseMove(NativeInterop.POINT pt)
    {
        var currentPosition = new System.Drawing.Point(pt.x, pt.y);

        if (!_lastMousePosition.HasValue)
        {
            _lastMousePosition = currentPosition;
            _lastMouseSampleTime = DateTime.MinValue;
            return;
        }

        var dx = currentPosition.X - _lastMousePosition.Value.X;
        var dy = currentPosition.Y - _lastMousePosition.Value.Y;
        var segmentDistance = Math.Sqrt(dx * dx + dy * dy);

        const double maxSegmentDistance = 250.0;
        if (segmentDistance > maxSegmentDistance)
        {
            _accumulatedDistance = 0.0;
            _lastMousePosition = currentPosition;
            _lastMouseSampleTime = DateTime.Now;
            return;
        }

        _accumulatedDistance += segmentDistance;
        _lastMousePosition = currentPosition;

        var elapsed = (DateTime.Now - _lastMouseSampleTime).TotalSeconds;
        if (elapsed < _mouseSampleInterval)
        {
            return;
        }

        var reportedDistance = _accumulatedDistance;
        _accumulatedDistance = 0.0;
        _lastMouseSampleTime = DateTime.Now;

        if (reportedDistance <= 0)
        {
            return;
        }

        var distance = reportedDistance;
        ThreadPool.QueueUserWorkItem(_ => InvokeSubscriber("MouseMoved", () => MouseMoved?.Invoke(distance)));
    }

    private void HandleScroll(uint mouseData, string appName, string displayName)
    {
        var delta = NativeInterop.HiWord((int)mouseData);
        var scrollDistance = Math.Abs(delta) / 120.0;
        MouseScrolled?.Invoke(scrollDistance, appName, displayName);
    }

    private static void InvokeSubscriber(string eventName, Action invoke)
    {
        try
        {
            invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{eventName} subscriber failed: {ex}");
        }
    }

    public void ResetLastMousePosition()
    {
        _lastMousePosition = null;
        _accumulatedDistance = 0.0;
    }

    public void HandleSystemResume()
    {
        if (!_isMonitoring)
        {
            StartMonitoring();
            return;
        }

        if (Interlocked.CompareExchange(ref _isReinstallingHooks, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Debug.WriteLine("Handling system resume: refreshing hooks and transient state.");
            RetryHookRecovery("resume");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Resume recovery failed after retries. {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _isReinstallingHooks, 0);
        }
    }

    private void RestartMonitoring()
    {
        StopMonitoring();
        StartMonitoring();
    }

    private void RetryHookRecovery(string reason)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= HookReinstallRetryCount; attempt++)
        {
            try
            {
                Debug.WriteLine($"Hook recovery attempt {attempt}/{HookReinstallRetryCount} ({reason}).");
                ReinstallHooks();
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Debug.WriteLine($"Hook recovery attempt {attempt} failed: {ex.Message}");
                if (attempt < HookReinstallRetryCount)
                {
                    Thread.Sleep(HookReinstallRetryDelayMs);
                }
            }
        }

        Debug.WriteLine("Hook reinstall retries exhausted, restarting monitoring.");
        RestartMonitoring();

        if (!_isMonitoring)
        {
            throw lastError ?? new InvalidOperationException("Hook recovery failed and monitoring did not restart.");
        }
    }

    private bool HasRecentKeyboardActivity()
    {
        // Scan every virtual key each round: an early return would leave later keys' low bits
        // unread, and GetAsyncKeyState's low bit means "changed since the last poll", so a stale
        // low bit would then surface on a later round as if the input had just happened.
        var hasActivity = false;
        for (var vk = 0x08; vk <= 0xFE; vk++)
        {
            if (IsKeyboardActivityEvidence(vk, NativeInterop.GetAsyncKeyState(vk)))
            {
                hasActivity = true;
            }
        }

        return hasActivity;
    }

    private bool IsKeyboardActivityEvidence(int vk, short state)
    {
        if ((state & 0x0001) != 0) return true;
        if ((state & 0x8000) == 0) return false;

        return !IsHeldModifier(vk);
    }

    // A held modifier keeps the high bit set without producing new input, so it is not treated as
    // evidence once the hook has recorded its key down and not yet its key up. Generic
    // VK_CONTROL/VK_SHIFT/VK_MENU never match _pressedKeys because the low-level hook reports the
    // left/right variants, so they are ignored outright.
    private bool IsHeldModifier(int vk)
    {
        if (vk == NativeInterop.VK_CONTROL || vk == NativeInterop.VK_SHIFT || vk == NativeInterop.VK_MENU)
        {
            return true;
        }

        if (vk != VkLControl && vk != VkRControl && vk != VkLShift && vk != VkRShift
            && vk != VkLMenu && vk != VkRMenu
            && vk != NativeInterop.VK_LWIN && vk != NativeInterop.VK_RWIN)
        {
            return false;
        }

        lock (_pressedKeys)
        {
            return _pressedKeys.Contains(vk);
        }
    }

    private bool TryStopHookThread()
    {
        var thread = _hookThread;
        var threadId = _hookThreadId;
        if (thread == null && threadId == 0)
        {
            return true;
        }

        if (threadId != 0)
        {
            NativeInterop.PostThreadMessage(threadId, NativeInterop.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        if (thread == null)
        {
            Debug.WriteLine("Hook thread reference is missing while thread id is still set; keeping thread state for a later retry.");
            return false;
        }

        if (thread != null && !thread.Join(HookThreadStopTimeoutMs))
        {
            Debug.WriteLine($"Hook thread did not exit within {HookThreadStopTimeoutMs}ms.");
            return false;
        }

        _hookThread = null;
        _hookThreadId = 0;
        return true;
    }

    private void ResetTransientState()
    {
        lock (_pressedKeys)
        {
            _pressedKeys.Clear();
        }

        _lastMousePosition = null;
        _accumulatedDistance = 0.0;
        _lastMouseSampleTime = DateTime.MinValue;
    }

    public void Dispose()
    {
        StopMonitoring();
        _instance = null;
    }
}
