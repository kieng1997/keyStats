using System;
using System.IO;
using System.Security.Principal;
using KeyStats.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

/// <summary>
/// 针对真实 Schedule.Service 的隔离测试：只使用随机名、无触发器的临时任务，
/// 绝不触碰 KeyStats 正式任务或 HKCU Run 值。
/// </summary>
[TestClass]
public sealed class StartupTaskStoreTests
{
    [TestMethod]
    public void Exists_ForMissingTask_ReturnsFalseInsteadOfThrowing()
    {
        TestPlatform.RequireWindows();
        var store = CreateThrowawayStore("KeyStatsTestMissing");

        Assert.IsFalse(store.Exists());
        Assert.IsFalse(store.IsTaskEnabled());
        Assert.IsFalse(store.MatchesConfiguration(@"C:\nowhere\KeyStats.exe"));
    }

    [TestMethod]
    public void Register_ThenExistsAndMatchesPath_AndDeleteRemoves()
    {
        TestPlatform.RequireWindows();
        var name = "KeyStatsTest-" + Guid.NewGuid().ToString("N");
        // 普通权限任务：非管理员进程也能注册，用于覆盖真实的读写/删除链路。
        // 动作指向不存在的临时路径，避免残留任务在登录时启动任何程序。
        var store = new TaskSchedulerStartupTaskStore(
            name, CurrentSid(), addLogonTrigger: true, highestPrivileges: false);
        var exePath = Path.Combine(Path.GetTempPath(), "KeyStatsTestCopy", "KeyStats.exe");
        try
        {
            store.Register(exePath);

            Assert.IsTrue(store.Exists());
            Assert.IsTrue(store.IsTaskEnabled());
            Assert.IsTrue(store.MatchesConfiguration(exePath));
            Assert.IsFalse(store.MatchesConfiguration(Path.Combine(Path.GetTempPath(), "Other", "KeyStats.exe")));

            store.Delete();
            Assert.IsFalse(store.Exists());

            store.Delete();
            Assert.IsFalse(store.Exists(), "重复删除不应抛出");
        }
        finally
        {
            try
            {
                store.Delete();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [TestMethod]
    public void Register_WithHighestPrivileges_WhenNotElevated_FailsClearlyAndLeavesNoTask()
    {
        TestPlatform.RequireWindows();
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.Inconclusive("需要普通权限进程才能验证最高权限任务注册被拒绝。");
        }

        var store = new TaskSchedulerStartupTaskStore(
            "KeyStatsTest-" + Guid.NewGuid().ToString("N"), CurrentSid(), addLogonTrigger: false);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => store.Register(Path.Combine(Path.GetTempPath(), "KeyStatsTestCopy", "KeyStats.exe")));
            Assert.IsFalse(store.Exists(), "注册失败时不应留下任务");
        }
        finally
        {
            try
            {
                store.Delete();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [TestMethod]
    public void MatchesConfiguration_WhenTaskIsDisabled_ReturnsFalse()
    {
        TestPlatform.RequireWindows();
        var taskName = "KeyStatsTest-" + Guid.NewGuid().ToString("N");
        // 动作指向不存在的临时路径：即使测试异常留下任务，登录触发也不会启动任何程序。
        var store = new TaskSchedulerStartupTaskStore(
            taskName, CurrentSid(), addLogonTrigger: true, highestPrivileges: false);
        var exePath = Path.Combine(Path.GetTempPath(), "KeyStatsTestCopy", "KeyStats.exe");
        try
        {
            store.Register(exePath);
            Assert.IsTrue(store.MatchesConfiguration(exePath));

            SetTaskEnabled(taskName, false);

            Assert.IsTrue(store.Exists(), "禁用的任务仍然存在");
            Assert.IsFalse(store.IsTaskEnabled(), "禁用后应识别为未启用");
            Assert.IsFalse(store.MatchesConfiguration(exePath), "禁用任务不应视为已正确配置");
        }
        finally
        {
            try
            {
                store.Delete();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static void SetTaskEnabled(string taskName, bool enabled)
    {
        MutateTask(taskName, task => task.Enabled = enabled);
    }

    private static void MutateTask(string taskName, Action<dynamic> mutate)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        Assert.IsNotNull(type);
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        try
        {
            dynamic folder = service.GetFolder("\\");
            dynamic task = folder.GetTask(taskName);
            try
            {
                mutate(task);
                // task.Definition 是快照，动作改动需回写才会持久化。
                folder.RegisterTaskDefinition(
                    taskName, task.Definition, 4, CurrentSid(), null, 3, null);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(task);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(folder);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(service);
        }
    }

    private static TaskSchedulerStartupTaskStore CreateThrowawayStore(string prefix)
    {
        return new TaskSchedulerStartupTaskStore(
            prefix + "-" + Guid.NewGuid().ToString("N"), CurrentSid(), addLogonTrigger: false);
    }

    private static string CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? string.Empty;
    }
}
