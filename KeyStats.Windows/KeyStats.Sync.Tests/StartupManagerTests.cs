using System;
using KeyStats.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace KeyStats.Sync.Tests;

[TestClass]
public sealed class StartupManagerTests
{
    private const string ExeA = @"C:\Fake\CopyA\KeyStats.exe";
    private const string ExeB = @"C:\Fake\CopyB\KeyStats.exe";

    [TestMethod]
    public void BuildTaskName_ContainsOnlyAppNameAndUserSid()
    {
        Assert.AreEqual("KeyStats-S-1-5-21-1000", StartupManager.BuildTaskName("S-1-5-21-1000"));
        Assert.IsFalse(StartupManager.BuildTaskName("S-1-5-21-1000").Contains(@"\"));
    }

    [TestMethod]
    public void BuildTaskName_WithMissingSid_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => StartupManager.BuildTaskName(null!));
        Assert.ThrowsExactly<ArgumentException>(() => StartupManager.BuildTaskName(" "));
    }

    [TestMethod]
    public void ElevatedEnable_RegistersTaskAndRemovesRunValue()
    {
        using var runKey = new TempRunKey();
        runKey.Set("KeyStats", $"\"{ExeA}\"");
        var store = new FakeTaskStore();
        var setting = new Setting { Value = true };
        var manager = Create(store, runKey, elevated: true, exePath: ExeB, setting);

        manager.SetEnabled(true);

        Assert.AreEqual(ExeB, store.RegisteredPath);
        Assert.IsNull(runKey.Get("KeyStats"));
        Assert.IsTrue(setting.Value);
        Assert.IsTrue(manager.IsEnabled);
    }

    [TestMethod]
    public void NonElevatedEnable_WithoutExistingTask_UsesRunValue()
    {
        using var runKey = new TempRunKey();
        var store = new FakeTaskStore();
        var setting = new Setting();
        var manager = Create(store, runKey, elevated: false, exePath: ExeA, setting);

        manager.SetEnabled(true);

        Assert.IsNull(store.RegisteredPath);
        Assert.AreEqual($"\"{ExeA}\"", runKey.Get("KeyStats"));
        Assert.IsTrue(setting.Value);
    }

    [TestMethod]
    public void NonElevatedEnable_WhenTaskStateIsUnreadable_FailsWithoutCreatingRun()
    {
        using var runKey = new TempRunKey();
        var store = new FakeTaskStore { ThrowOnExists = true };
        var setting = new Setting();
        var manager = Create(store, runKey, elevated: false, exePath: ExeA, setting);

        Assert.ThrowsExactly<InvalidOperationException>(() => manager.SetEnabled(true));

        Assert.IsNull(runKey.Get("KeyStats"));
        Assert.IsFalse(setting.Value);
    }

    [TestMethod]
    public void NonElevatedEnable_WithTaskForSamePath_KeepsSingleEntryWithoutFailure()
    {
        using var runKey = new TempRunKey();
        var store = new FakeTaskStore { RegisteredPath = ExeA };
        var setting = new Setting();
        var manager = Create(store, runKey, elevated: false, exePath: ExeA, setting);

        manager.SetEnabled(true);

        Assert.AreEqual(0, store.RegisterCount);
        Assert.AreEqual(ExeA, store.RegisteredPath);
        Assert.IsNull(runKey.Get("KeyStats"));
        Assert.IsTrue(setting.Value);
    }

    [TestMethod]
    public void NonElevatedEnable_WithTaskForOtherPath_FailsWithoutDowngradingOrDuplicating()
    {
        using var runKey = new TempRunKey();
        var store = new FakeTaskStore { RegisteredPath = ExeA };
        var setting = new Setting { Value = true };
        var manager = Create(store, runKey, elevated: false, exePath: ExeB, setting);

        Assert.ThrowsExactly<InvalidOperationException>(() => manager.SetEnabled(true));

        Assert.AreEqual(ExeA, store.RegisteredPath);
        Assert.AreEqual(0, store.RegisterCount);
        Assert.IsNull(runKey.Get("KeyStats"));
        Assert.IsTrue(setting.Value, "持久化设置不应在失败时被改写");
    }

    [TestMethod]
    public void ElevatedSync_FromSecondCopy_UpdatesExistingTaskPathInsteadOfDuplicating()
    {
        using var runKey = new TempRunKey();
        var store = new FakeTaskStore { RegisteredPath = ExeA };
        var setting = new Setting { Value = true };
        var manager = Create(store, runKey, elevated: true, exePath: ExeB, setting);

        manager.SyncWithSettings();

        Assert.AreEqual(ExeB, store.RegisteredPath);
        Assert.AreEqual(1, store.RegisterCount);
        Assert.IsNull(runKey.Get("KeyStats"));
    }

    [TestMethod]
    public void ElevatedSync_WhenTaskAlreadyMatches_DoesNotTouchTask()
    {
        using var runKey = new TempRunKey();
        var store = new FakeTaskStore { RegisteredPath = ExeA };
        var setting = new Setting { Value = true };
        var manager = Create(store, runKey, elevated: true, exePath: ExeA, setting);

        manager.SyncWithSettings();

        Assert.AreEqual(0, store.RegisterCount);
        Assert.IsNull(runKey.Get("KeyStats"));
    }

    [TestMethod]
    public void NonElevatedSync_WithExistingAdminTask_DoesNotCreateRunOrDowngrade()
    {
        using var runKey = new TempRunKey();
        runKey.Set("KeyStats", $"\"{ExeB}\"");
        var store = new FakeTaskStore { RegisteredPath = ExeA };
        var setting = new Setting { Value = true };
        var manager = Create(store, runKey, elevated: false, exePath: ExeB, setting);

        manager.SyncWithSettings();

        Assert.AreEqual(ExeA, store.RegisteredPath, "普通进程不应改写管理员任务");
        Assert.AreEqual(0, store.RegisterCount);
        Assert.AreEqual($"\"{ExeB}\"", runKey.Get("KeyStats"), "任务配置不匹配时保留原有 Run 入口");
    }

    [TestMethod]
    public void NonElevatedSync_WhenTaskIsDisabledAndNoRun_DoesNotCreateRun()
    {
        using var runKey = new TempRunKey();
        var store = new FakeTaskStore { RegisteredPath = ExeA, TaskEnabled = false };
        var setting = new Setting { Value = true };
        var manager = Create(store, runKey, elevated: false, exePath: ExeA, setting);

        manager.SyncWithSettings();

        Assert.IsNull(runKey.Get("KeyStats"), "已有管理员模式时不应降级为 Run");
        Assert.AreEqual(0, store.RegisterCount);
    }

    [TestMethod]
    public void NonElevatedSync_WhenTaskMatchesCurrentCopy_ClearsRunEntry()
    {
        using var runKey = new TempRunKey();
        runKey.Set("KeyStats", $"\"{ExeA}\"");
        var store = new FakeTaskStore { RegisteredPath = ExeA };
        var setting = new Setting { Value = true };
        var manager = Create(store, runKey, elevated: false, exePath: ExeA, setting);

        manager.SyncWithSettings();

        Assert.IsNull(runKey.Get("KeyStats"), "确证任务匹配时应移除重复入口");
        Assert.AreEqual(0, store.RegisterCount);
    }

    [TestMethod]
    public void NonElevatedSync_WithRunOnly_UpgradesRunPathForCurrentCopy()
    {
        using var runKey = new TempRunKey();
        runKey.Set("KeyStats", $"\"{ExeA}\"");
        var store = new FakeTaskStore();
        var setting = new Setting { Value = true };
        var manager = Create(store, runKey, elevated: false, exePath: ExeB, setting);

        manager.SyncWithSettings();

        Assert.AreEqual($"\"{ExeB}\"", runKey.Get("KeyStats"));
        Assert.IsNull(store.RegisteredPath);
    }

    [TestMethod]
    public void ElevatedEnable_WhenTaskRegistrationFails_KeepsRunValueAndDoesNotPersistSuccess()
    {
        using var runKey = new TempRunKey();
        runKey.Set("KeyStats", $"\"{ExeA}\"");
        var store = new FakeTaskStore { ThrowOnRegister = true };
        var setting = new Setting();
        var manager = Create(store, runKey, elevated: true, exePath: ExeB, setting);

        Assert.ThrowsExactly<InvalidOperationException>(() => manager.SetEnabled(true));

        Assert.AreEqual($"\"{ExeA}\"", runKey.Get("KeyStats"));
        Assert.IsFalse(setting.Value);
    }

    [TestMethod]
    public void Disable_RemovesBothBackendsAndPersists()
    {
        using var runKey = new TempRunKey();
        runKey.Set("KeyStats", $"\"{ExeA}\"");
        var store = new FakeTaskStore { RegisteredPath = ExeA };
        var setting = new Setting { Value = true };
        var manager = Create(store, runKey, elevated: true, exePath: ExeA, setting);

        manager.SetEnabled(false);

        Assert.IsNull(store.RegisteredPath);
        Assert.AreEqual(1, store.DeleteCount);
        Assert.IsNull(runKey.Get("KeyStats"));
        Assert.IsFalse(setting.Value);
        Assert.IsFalse(manager.IsEnabled);
    }

    [TestMethod]
    public void Disable_WhenTaskDeletionIsDenied_StillClearsRunAndPersistsButReportsFailure()
    {
        using var runKey = new TempRunKey();
        runKey.Set("KeyStats", $"\"{ExeA}\"");
        var store = new FakeTaskStore { RegisteredPath = ExeA, ThrowOnDelete = true };
        var setting = new Setting { Value = true };
        var manager = Create(store, runKey, elevated: false, exePath: ExeA, setting);

        Assert.ThrowsExactly<InvalidOperationException>(() => manager.SetEnabled(false));

        Assert.AreEqual(ExeA, store.RegisteredPath, "残留任务仍需管理员处理");
        Assert.IsNull(runKey.Get("KeyStats"), "普通 Run 入口仍应被清除");
        Assert.IsFalse(setting.Value, "禁用意图应被持久化");
        Assert.IsTrue(manager.IsEnabled, "残留任务仍会启动，界面应反映真实状态");
    }

    [TestMethod]
    public void NonElevatedSync_WhenTaskExistsButIsDisabled_KeepsUsableRunEntry()
    {
        using var runKey = new TempRunKey();
        runKey.Set("KeyStats", $"\"{ExeA}\"");
        var store = new FakeTaskStore { RegisteredPath = ExeA, TaskEnabled = false };
        var manager = Create(store, runKey, elevated: false, exePath: ExeA, new Setting { Value = true });

        manager.SyncWithSettings();

        Assert.AreEqual($"\"{ExeA}\"", runKey.Get("KeyStats"), "无效任务不应导致唯一可用入口被删除");
    }

    [TestMethod]
    public void IsEnabled_RecognizesTaskBackendAndRunBackend()
    {
        using var runKey = new TempRunKey();
        var taskStore = new FakeTaskStore { RegisteredPath = ExeA };
        Assert.IsTrue(Create(taskStore, runKey, elevated: false, exePath: ExeA, new Setting { Value = true }).IsEnabled);

        var runStore = new FakeTaskStore();
        runKey.Set("KeyStats", $"\"{ExeA}\"");
        Assert.IsTrue(Create(runStore, runKey, elevated: false, exePath: ExeA, new Setting { Value = true }).IsEnabled);

        runKey.Delete();
        Assert.IsFalse(Create(runStore, runKey, elevated: false, exePath: ExeA, new Setting()).IsEnabled);
    }

    [TestMethod]
    public void IsEnabled_WhenTaskExistsButIsDisabled_ReportsDisabled()
    {
        using var runKey = new TempRunKey();
        var store = new FakeTaskStore { RegisteredPath = ExeA, TaskEnabled = false };
        var manager = Create(store, runKey, elevated: false, exePath: ExeA, new Setting { Value = true });

        Assert.IsFalse(manager.IsEnabled, "任务被禁用时不应显示为已启用");
    }

    [TestMethod]
    public void NonElevatedEnable_WhenTaskIsDisabled_FailsInsteadOfReportingSuccess()
    {
        using var runKey = new TempRunKey();
        var store = new FakeTaskStore { RegisteredPath = ExeA, TaskEnabled = false };
        var setting = new Setting();
        var manager = Create(store, runKey, elevated: false, exePath: ExeA, setting);

        Assert.ThrowsExactly<InvalidOperationException>(() => manager.SetEnabled(true));

        Assert.AreEqual(0, store.RegisterCount);
        Assert.IsNull(runKey.Get("KeyStats"));
        Assert.IsFalse(setting.Value);
    }

    [TestMethod]
    public void ElevatedSync_WhenTaskIsDisabled_ReRegistersToRestoreIt()
    {
        using var runKey = new TempRunKey();
        var store = new FakeTaskStore { RegisteredPath = ExeA, TaskEnabled = false };
        var manager = Create(store, runKey, elevated: true, exePath: ExeA, new Setting { Value = true });

        manager.SyncWithSettings();

        Assert.AreEqual(1, store.RegisterCount);
        Assert.IsTrue(store.TaskEnabled);
        Assert.AreEqual(ExeA, store.RegisteredPath);
    }

    [TestMethod]
    public void ElevatedEnable_WhenTaskIsDisabled_ReRegistersInsteadOfLeavingDisabled()
    {
        using var runKey = new TempRunKey();
        var store = new FakeTaskStore { RegisteredPath = ExeA, TaskEnabled = false };
        var manager = Create(store, runKey, elevated: true, exePath: ExeA, new Setting { Value = true });

        manager.SetEnabled(true);

        Assert.AreEqual(1, store.RegisterCount);
        Assert.IsTrue(store.IsTaskEnabled());
        Assert.IsNull(runKey.Get("KeyStats"));
    }

    [TestMethod]
    public void SyncWithSettings_WhenLaunchAtStartupIsOff_LeavesBothBackendsUntouched()
    {
        using var runKey = new TempRunKey();
        runKey.Set("KeyStats", $"\"{ExeA}\"");
        var store = new FakeTaskStore();
        var manager = Create(store, runKey, elevated: true, exePath: ExeB, new Setting());

        manager.SyncWithSettings();

        Assert.AreEqual(0, store.RegisterCount);
        Assert.AreEqual($"\"{ExeA}\"", runKey.Get("KeyStats"));
    }

    [TestMethod]
    public void SyncWithSettings_WhenTaskRegistrationFails_DoesNotThrow()
    {
        using var runKey = new TempRunKey();
        var store = new FakeTaskStore { ThrowOnRegister = true };
        var manager = Create(store, runKey, elevated: true, exePath: ExeA, new Setting { Value = true });

        manager.SyncWithSettings();

        Assert.AreEqual(0, store.RegisterCount);
    }

    private static StartupManager Create(
        FakeTaskStore store, TempRunKey runKey, bool elevated, string exePath, Setting setting)
    {
        return new StartupManager(
            store,
            () => elevated,
            () => exePath,
            runKey.Path,
            () => setting.Value,
            enabled => setting.Value = enabled);
    }

    private sealed class Setting
    {
        public bool Value { get; set; }
    }

    private sealed class FakeTaskStore : TaskSchedulerStartupTaskStore
    {
        public FakeTaskStore() : base("KeyStats-test", "S-1-5-21-0", addLogonTrigger: false)
        {
        }

        public string? RegisteredPath { get; set; }
        public bool TaskEnabled { get; set; } = true;
        public int RegisterCount { get; private set; }
        public int DeleteCount { get; private set; }
        public bool ThrowOnRegister { get; set; }
        public bool ThrowOnDelete { get; set; }
        public bool ThrowOnExists { get; set; }

        public override bool Exists()
        {
            if (ThrowOnExists)
            {
                throw new InvalidOperationException("模拟任务状态读取被拒绝");
            }

            return RegisteredPath != null;
        }

        public override bool IsTaskEnabled() => Exists() && TaskEnabled;

        public override bool MatchesConfiguration(string executablePath) =>
            IsTaskEnabled() &&
            string.Equals(RegisteredPath, executablePath, StringComparison.OrdinalIgnoreCase);

        public override void Register(string executablePath)
        {
            if (ThrowOnRegister)
            {
                throw new InvalidOperationException("模拟任务注册被拒绝");
            }

            RegisterCount++;
            RegisteredPath = executablePath;
            TaskEnabled = true;
        }

        public override void Delete()
        {
            if (ThrowOnDelete)
            {
                throw new InvalidOperationException("模拟任务删除被拒绝");
            }

            DeleteCount++;
            RegisteredPath = null;
        }
    }

    /// <summary>
    /// 使用 HKCU 下的隔离临时子键模拟 Run 项，避免触碰真实自启配置。
    /// </summary>
    private sealed class TempRunKey : IDisposable
    {
        public string Path { get; }

        public TempRunKey()
        {
            Path = $@"SOFTWARE\KeyStats\Tests\Startup\{Guid.NewGuid():N}";
            using var key = Registry.CurrentUser.CreateSubKey(Path);
            Assert.IsNotNull(key);
        }

        public void Set(string name, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(Path);
            key!.SetValue(name, value);
        }

        public string? Get(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(Path, false);
            return key?.GetValue(name) as string;
        }

        public void Delete()
        {
            using var key = Registry.CurrentUser.OpenSubKey(Path, true);
            key?.DeleteValue("KeyStats", false);
        }

        public void Dispose()
        {
            // 只删除本测试的 GUID 子键，绝不递归删除父目录。
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(Path, false);
            }
            catch (ArgumentException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
        }
    }
}
