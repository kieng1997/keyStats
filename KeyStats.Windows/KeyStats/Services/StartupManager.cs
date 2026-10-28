using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace KeyStats.Services;

public class StartupManager
{
    private static StartupManager? _instance;
    public static StartupManager Instance => _instance ??= CreateDefault();

    private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "KeyStats";

    private readonly TaskSchedulerStartupTaskStore _taskStore;
    private readonly Func<bool> _isElevated;
    private readonly Func<string?> _getExecutablePath;
    private readonly string _registryKeyPath;
    private readonly Func<bool> _readLaunchAtStartup;
    private readonly Action<bool> _writeLaunchAtStartup;

    public StartupManager(
        TaskSchedulerStartupTaskStore taskStore,
        Func<bool> isElevated,
        Func<string?> getExecutablePath,
        string registryKeyPath,
        Func<bool> readLaunchAtStartup,
        Action<bool> writeLaunchAtStartup)
    {
        _taskStore = taskStore;
        _isElevated = isElevated;
        _getExecutablePath = getExecutablePath;
        _registryKeyPath = registryKeyPath;
        _readLaunchAtStartup = readLaunchAtStartup;
        _writeLaunchAtStartup = writeLaunchAtStartup;
    }

    private static StartupManager CreateDefault()
    {
        var userSid = TryGetCurrentUserSid();
        return new StartupManager(
            new TaskSchedulerStartupTaskStore(
                userSid == null ? string.Empty : BuildTaskName(userSid), userSid ?? string.Empty),
            IsProcessElevated,
            GetCurrentExecutablePath,
            RegistryKeyPath,
            () => StatsManager.Instance.Settings.LaunchAtStartup,
            enabled =>
            {
                StatsManager.Instance.Settings.LaunchAtStartup = enabled;
                StatsManager.Instance.SaveSettings();
            });
    }

    public bool IsEnabled
    {
        get
        {
            try
            {
                if (_taskStore.IsTaskEnabled())
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading startup task: {ex.Message}");
            }

            return IsRunValuePresent();
        }
    }

    public void SyncWithSettings()
    {
        try
        {
            if (!_readLaunchAtStartup())
            {
                return;
            }

            var exePath = _getExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return;
            }

            var executablePath = exePath!;
            if (_isElevated())
            {
                // 管理员启动时把入口升级/收敛到计划任务，并移除 Run 值避免双启动。
                if (!_taskStore.MatchesConfiguration(executablePath))
                {
                    _taskStore.Register(executablePath);
                }

                RemoveRunValue();
                return;
            }

            if (_taskStore.Exists())
            {
                // 已有管理员模式：只有确证配置匹配当前运行程序时才清 Run，
                // 路径不同/被禁用/配置失效时保持原有入口，也不另建 Run。
                if (_taskStore.MatchesConfiguration(executablePath))
                {
                    RemoveRunValue();
                }

                return;
            }

            WriteRunValueIfChanged(executablePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error syncing startup: {ex.Message}");
        }
    }

    public void SetEnabled(bool enabled)
    {
        var exePath = _getExecutablePath();
        if (enabled && string.IsNullOrWhiteSpace(exePath))
        {
            throw new InvalidOperationException(KeyStats.Properties.Strings.Error_StartupTaskFailed);
        }

        if (enabled)
        {
            // 先让新后端成功，再清理旧入口：失败时不会留下「已被删掉、却仍报告已启用」的状态。
            if (_isElevated())
            {
                _taskStore.Register(exePath!);
                RemoveRunValue();
            }
            else if (_taskStore.Exists())
            {
                // 已有管理员任务时，普通进程既不能改它（最高权限任务需要提权），
                // 也不能另建 Run 造成双入口，只能明确失败并保留原状。
                if (!_taskStore.MatchesConfiguration(exePath!))
                {
                    throw new InvalidOperationException(KeyStats.Properties.Strings.Error_StartupTaskFailed);
                }

                RemoveRunValue();
            }
            else
            {
                WriteRunValueIfChanged(exePath!);
            }
        }
        else
        {
            // 先尽力清任务；即使任务清理失败（如需提权），也要清掉 Run 并持久化禁用意图，
            // 然后明确报错，不宣称已全部关闭。
            Exception? taskError = null;
            try
            {
                _taskStore.Delete();
            }
            catch (Exception ex)
            {
                taskError = ex;
            }

            RemoveRunValue();
            _writeLaunchAtStartup(false);

            if (taskError != null)
            {
                throw taskError is InvalidOperationException
                    ? taskError
                    : new InvalidOperationException(KeyStats.Properties.Strings.Error_StartupTaskFailed, taskError);
            }

            return;
        }

        _writeLaunchAtStartup(enabled);
    }

    private bool IsRunValuePresent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registryKeyPath, false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    private void WriteRunValueIfChanged(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_registryKeyPath, true);
        if (key == null)
        {
            throw new InvalidOperationException(KeyStats.Properties.Strings.Error_StartupTaskFailed);
        }

        if (!PathsEqual(GetConfiguredExecutablePath(key), exePath))
        {
            key.SetValue(AppName, $"\"{exePath}\"");
        }
    }

    private void RemoveRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_registryKeyPath, true);
        key?.DeleteValue(AppName, false);
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // 任务名只含应用名与当前用户 SID，不含安装路径/版本/随机值，
    // 使同一用户的多个副本共享同一个任务。
    public static string BuildTaskName(string userSid)
    {
        if (string.IsNullOrWhiteSpace(userSid))
        {
            throw new ArgumentException(KeyStats.Properties.Strings.Error_StartupTaskFailed);
        }

        return $"{AppName}-{userSid}";
    }

    // 取不到 SID 时返回 null，由任务后端拒绝一切任务操作，避免突破当前用户边界。
    private static string? TryGetCurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            return string.IsNullOrWhiteSpace(sid) ? null : sid;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetCurrentExecutablePath()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
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

    private static string? GetConfiguredExecutablePath(RegistryKey key)
    {
        if (key.GetValue(AppName) is not string rawValue || string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var trimmed = rawValue.Trim();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            var closingQuoteIndex = trimmed.IndexOf('"', 1);
            if (closingQuoteIndex > 1)
            {
                trimmed = trimmed.Substring(1, closingQuoteIndex - 1);
            }
        }
        else
        {
            var firstSpaceIndex = trimmed.IndexOf(' ');
            if (firstSpaceIndex > 0)
            {
                trimmed = trimmed.Substring(0, firstSpaceIndex);
            }
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
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
}
