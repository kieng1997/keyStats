using System;
using System.Globalization;
using System.IO;
using KeyStats.Models;
using KeyStats.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

[TestClass]
public sealed class ScheduledBackupServiceTests
{
    [TestMethod]
    public void ScheduledBackup_UsesUtcFileScheduleAndRetainsNewestCopy()
    {
        using var directory = new TestDirectory();
        var backups = Path.Combine(directory.Path, "backups");
        Directory.CreateDirectory(backups);
        var now = new DateTime(2026, 11, 26, 5, 30, 0, DateTimeKind.Utc);
        var settings = new AppSettings();
        var exportCount = 0;
        var exportFails = false;
        using var service = new ScheduledBackupService(directory.Path, () =>
        {
            if (exportFails) throw new IOException("模拟导出失败");
            exportCount++;
            return System.Text.Encoding.UTF8.GetBytes("{\"version\":1}");
        }, () => settings.BackupIntervalDays, () => settings.BackupRetentionDays);

        service.RunOnce(now);
        var initial = Directory.GetFiles(backups, ScheduledBackupService.BackupFileNamePrefix + "*" + ScheduledBackupService.BackupFileNameSuffix);
        Assert.AreEqual(1, initial.Length, "首次启动应立即创建备份");
        Assert.AreEqual(1, exportCount);
        Assert.AreEqual(BackupName(now), Path.GetFileName(initial[0]));
        Assert.AreEqual("{\"version\":1}", File.ReadAllText(initial[0], System.Text.Encoding.UTF8));

        var beforeDue = now.AddHours(23);
        service.RunOnce(beforeDue);
        Assert.AreEqual(1, exportCount, "未到间隔时重启不应重复备份");
        settings.BackupIntervalDays = int.MaxValue;
        service.RunOnce(beforeDue);
        Assert.AreEqual(1, exportCount, "极大间隔设置不应因时间跨度溢出触发备份");

        settings.BackupIntervalDays = 1;
        settings.BackupRetentionDays = 7;
        now = now.AddHours(25);
        var expiredName = BackupName(now.AddDays(-10));
        var recentName = BackupName(now.AddDays(-2));
        var manualName = "my-personal-copy.json";
        File.WriteAllText(Path.Combine(backups, expiredName), "old");
        File.WriteAllText(Path.Combine(backups, recentName), "recent");
        File.WriteAllText(Path.Combine(backups, manualName), "keep");
        settings.BackupIntervalDays = int.MaxValue;
        using var restarted = new ScheduledBackupService(directory.Path,
            () => System.Text.Encoding.UTF8.GetBytes("{}"),
            () => settings.BackupIntervalDays, () => settings.BackupRetentionDays);
        restarted.RunOnce(now);
        Assert.IsFalse(File.Exists(Path.Combine(backups, expiredName)), "重启后未到期也应执行保留清理");
        Assert.IsTrue(File.Exists(Path.Combine(backups, recentName)), "最新副本始终保留");
        Assert.IsTrue(File.Exists(Path.Combine(backups, manualName)), "非受管文件始终保留");

        exportFails = true;
        var successTime = now.AddHours(2);
        service.RunOnce(successTime);
        Assert.AreEqual(1, exportCount, "导出失败不增加成功次数");
        Assert.IsTrue(File.Exists(Path.Combine(backups, recentName)), "失败时保留最新副本");
        Assert.IsFalse(File.Exists(Path.Combine(backups, BackupName(successTime))), "失败不应留下半成品备份");
        Assert.IsTrue(File.Exists(Path.Combine(backups, manualName)), "失败时仍保留非受管文件");
        exportFails = false;

        settings.BackupIntervalDays = 1;
        settings.BackupRetentionDays = 1;
        service.RunOnce(successTime);
        Assert.AreEqual(2, exportCount, "到期后应创建新备份");
        Assert.IsFalse(File.Exists(Path.Combine(backups, recentName)), "新副本成功后旧备份按保留期清理");
        Assert.IsTrue(File.Exists(Path.Combine(backups, BackupName(successTime))), "新副本应保留");
    }

    private static string BackupName(DateTime timestamp) => ScheduledBackupService.BackupFileNamePrefix +
        timestamp.ToString(ScheduledBackupService.BackupFileNameTimestampFormat, CultureInfo.InvariantCulture) +
        ScheduledBackupService.BackupFileNameSuffix;
}
