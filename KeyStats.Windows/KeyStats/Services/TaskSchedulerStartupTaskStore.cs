using System;
using System.IO;
using System.Runtime.InteropServices;
using KeyStats.Properties;

namespace KeyStats.Services;

/// <summary>
/// 通过任务计划程序 COM API 注册「仅当前用户登录时触发、最高权限、交互式令牌」的启动任务。
/// 任务名固定为「应用 + 当前用户 SID」，同一用户的多个安装副本共用一个任务。
/// </summary>
public class TaskSchedulerStartupTaskStore
{
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskTriggerLogon = 9;
    private const int TaskActionExec = 0;
    private const int TaskInstancesIgnoreNew = 2;

    private const int HResultFileNotFound = unchecked((int)0x80070002);
    private const int HResultPathNotFound = unchecked((int)0x80070003);

    private readonly string _taskName;
    private readonly string _userId;
    private readonly bool _available;
    private readonly bool _addLogonTrigger;
    private readonly int _runLevel;

    /// <param name="userId">当前用户 SID；任务计划程序接受 SID 作为账户标识。</param>
    /// <param name="addLogonTrigger">隔离测试可创建无触发器的一次性任务。</param>
    /// <param name="highestPrivileges">隔离测试可用普通权限任务覆盖真实读写路径。</param>
    public TaskSchedulerStartupTaskStore(
        string taskName, string userId, bool addLogonTrigger = true, bool highestPrivileges = true)
    {
        _taskName = taskName;
        _userId = userId;
        _available = !string.IsNullOrWhiteSpace(taskName) && !string.IsNullOrWhiteSpace(userId);
        _addLogonTrigger = addLogonTrigger;
        _runLevel = highestPrivileges ? TaskRunLevelHighest : 0;
    }

    public virtual bool Exists()
    {
        EnsureAvailable();
        dynamic? service = null;
        dynamic? folder = null;
        dynamic? task = null;
        try
        {
            service = CreateService();
            folder = service.GetFolder("\\");
            task = GetTaskOrNull(folder);
            return task != null;
        }
        catch (Exception ex) when (!(ex is InvalidOperationException))
        {
            throw Wrap(ex);
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    /// <summary>任务当前是否处于启用状态；任务不存在或读取失败返回 false。</summary>
    public virtual bool IsTaskEnabled()
    {
        EnsureAvailable();
        dynamic? service = null;
        dynamic? folder = null;
        dynamic? found = null;
        try
        {
            service = CreateService();
            folder = service.GetFolder("\\");
            found = GetTaskOrNull(folder);
            if (found == null)
            {
                return false;
            }

            dynamic task = found;
            return (bool)task.Enabled;
        }
        catch (Exception ex) when (!(ex is InvalidOperationException))
        {
            throw Wrap(ex);
        }
        finally
        {
            Release(found);
            Release(folder);
            Release(service);
        }
    }

    /// <summary>
    /// 现有任务是否已完整匹配所需配置：启用、唯一 Exec 动作指向该路径且无参数、
    /// 工作目录一致、交互式令牌 + 预期运行级别、存在当前用户的登录触发器。
    /// </summary>
    public virtual bool MatchesConfiguration(string executablePath)
    {
        EnsureAvailable();
        dynamic? service = null;
        dynamic? folder = null;
        dynamic? found = null;
        try
        {
            service = CreateService();
            folder = service.GetFolder("\\");
            found = GetTaskOrNull(folder);
            if (found == null)
            {
                return false;
            }

            dynamic task = found;
            if (!(bool)task.Enabled)
            {
                return false;
            }

            dynamic definition = task.Definition;
            try
            {
                dynamic principal = definition.Principal;
                try
                {
                    if ((int)principal.LogonType != TaskLogonInteractiveToken ||
                        (int)principal.RunLevel != _runLevel ||
                        !IsCurrentUserAccount((string?)principal.UserId))
                    {
                        return false;
                    }
                }
                finally
                {
                    Release(principal);
                }

                if (!HasLogonTriggerForCurrentUser(definition))
                {
                    return false;
                }

                return HasSingleMatchingAction(definition, executablePath);
            }
            finally
            {
                Release(definition);
            }
        }
        catch (Exception ex) when (!(ex is InvalidOperationException))
        {
            throw Wrap(ex);
        }
        finally
        {
            Release(found);
            Release(folder);
            Release(service);
        }
    }

    private static bool HasSingleMatchingAction(dynamic definition, string executablePath)
    {
        dynamic actions = definition.Actions;
        try
        {
            if ((int)actions.Count != 1)
            {
                return false;
            }

            dynamic action = actions.Item(1);
            try
            {
                if ((int)action.Type != TaskActionExec)
                {
                    return false;
                }

                var arguments = (string?)action.Arguments;
                var workingDirectory = (string?)action.WorkingDirectory;
                return string.Equals((string?)action.Path, executablePath, StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(arguments)
                    && string.Equals(
                        workingDirectory,
                        Path.GetDirectoryName(executablePath),
                        StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Release(action);
            }
        }
        finally
        {
            Release(actions);
        }
    }

    private bool HasLogonTriggerForCurrentUser(dynamic definition)
    {
        dynamic triggers = definition.Triggers;
        try
        {
            int count = (int)triggers.Count;
            for (int index = 1; index <= count; index++)
            {
                dynamic trigger = triggers.Item(index);
                try
                {
                    if ((int)trigger.Type == TaskTriggerLogon &&
                        IsCurrentUserAccount((string?)trigger.UserId))
                    {
                        return true;
                    }
                }
                finally
                {
                    Release(trigger);
                }
            }

            return false;
        }
        finally
        {
            Release(triggers);
        }
    }

    // 读回时 principal.UserId 可能是 SID 或账户名，两种形式都接受。
    private bool IsCurrentUserAccount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (string.Equals(value, _userId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var userName = Environment.UserName;
        return string.Equals(value, userName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                value,
                Environment.UserDomainName + "\\" + userName,
                StringComparison.OrdinalIgnoreCase);
    }

    public virtual void Register(string executablePath)
    {
        EnsureAvailable();
        dynamic? service = null;
        dynamic? folder = null;
        dynamic? definition = null;
        dynamic? registeredTask = null;
        try
        {
            service = CreateService();
            folder = service.GetFolder("\\");
            definition = service.NewTask(0);

            dynamic registrationInfo = definition.RegistrationInfo;
            try
            {
                registrationInfo.Author = "KeyStats";
                registrationInfo.Description = "KeyStats startup for the current user with highest privileges.";
            }
            finally
            {
                Release(registrationInfo);
            }

            dynamic principal = definition.Principal;
            try
            {
                principal.UserId = _userId;
                principal.LogonType = TaskLogonInteractiveToken;
                principal.RunLevel = _runLevel;
            }
            finally
            {
                Release(principal);
            }

            // 托盘常驻应用：不因电池/空闲/网络条件跳过，不中途停止，无执行时限；
            // 同一任务重复触发时忽略新实例，避免登录时启动多个副本。
            dynamic settings = definition.Settings;
            try
            {
                settings.Enabled = true;
                settings.DisallowStartIfOnBatteries = false;
                settings.StopIfGoingOnBatteries = false;
                settings.StartWhenAvailable = true;
                settings.ExecutionTimeLimit = "PT0S";
                settings.MultipleInstances = TaskInstancesIgnoreNew;
                settings.RunOnlyIfIdle = false;
                settings.RunOnlyIfNetworkAvailable = false;
                settings.AllowDemandStart = true;
            }
            finally
            {
                Release(settings);
            }

            if (_addLogonTrigger)
            {
                dynamic triggers = definition.Triggers;
                dynamic? trigger = null;
                try
                {
                    trigger = triggers.Create(TaskTriggerLogon);
                    trigger.UserId = _userId;
                    trigger.Enabled = true;
                }
                finally
                {
                    Release(trigger);
                    Release(triggers);
                }
            }

            dynamic actions = definition.Actions;
            dynamic? action = null;
            try
            {
                action = actions.Create(TaskActionExec);
                action.Path = executablePath;
                var workingDirectory = Path.GetDirectoryName(executablePath);
                if (!string.IsNullOrEmpty(workingDirectory))
                {
                    action.WorkingDirectory = workingDirectory;
                }
            }
            finally
            {
                Release(action);
                Release(actions);
            }

            registeredTask = folder.RegisterTaskDefinition(
                _taskName, definition, TaskCreateOrUpdate, _userId, null, TaskLogonInteractiveToken, null);
        }
        catch (Exception ex) when (!(ex is InvalidOperationException))
        {
            throw Wrap(ex);
        }
        finally
        {
            Release(registeredTask);
            Release(definition);
            Release(folder);
            Release(service);
        }
    }

    public virtual void Delete()
    {
        EnsureAvailable();
        dynamic? service = null;
        dynamic? folder = null;
        dynamic? task = null;
        try
        {
            service = CreateService();
            folder = service.GetFolder("\\");
            task = GetTaskOrNull(folder);
            if (task == null)
            {
                return;
            }

            // dynamic 无法绑定 IRegisteredTask.Delete；DeleteTask 对本应用固定任务名等价。
            folder.DeleteTask(_taskName, 0);
        }
        catch (Exception ex) when (!(ex is InvalidOperationException))
        {
            if (IsNotFound(ex))
            {
                return;
            }

            throw Wrap(ex);
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    // 无法确定当前用户 SID 时拒绝一切任务操作，而不是退化成全局任务名。
    private void EnsureAvailable()
    {
        if (!_available)
        {
            throw new InvalidOperationException(Strings.Error_StartupTaskFailed);
        }
    }

    private dynamic? GetTaskOrNull(dynamic folder)
    {
        try
        {
            return folder.GetTask(_taskName);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    private static dynamic CreateService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type == null)
        {
            throw new InvalidOperationException(Strings.Error_StartupTaskFailed);
        }

        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            return service;
        }
        catch
        {
            Release(service);
            throw;
        }
    }

    // 任务缺失经 COM 封送后可能是 COMException/FileNotFoundException/DirectoryNotFoundException，
    // 统一按 HRESULT 判定；access denied、服务不可用不算缺失。
    private static bool IsNotFound(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current.HResult == HResultFileNotFound || current.HResult == HResultPathNotFound)
            {
                return true;
            }
        }

        return false;
    }

    private static InvalidOperationException Wrap(Exception ex)
    {
        return new InvalidOperationException(Strings.Error_StartupTaskFailed, ex);
    }

    private static void Release(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }
}
