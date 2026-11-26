using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace KeyStats.Services;

/// <summary>
/// 将统计导出写入本机 backups 目录，并按设置清理过期备份。
/// </summary>
internal sealed class ScheduledBackupService : IDisposable
{
    internal const string BackupFileNamePrefix = "KeyStats-AutoBackup-";
    internal const string BackupFileNameSuffix = ".json";
    internal const string BackupFileNameTimestampFormat = "yyyyMMddTHHmmssfffffffZ";

    private const string BackupDirectoryName = "backups";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly string _backupDirectory;
    private readonly Func<byte[]> _exportSnapshot;
    private readonly Func<int> _readIntervalDays;
    private readonly Func<int> _readRetentionDays;
    private Timer? _timer;
    private bool _disposed;

    internal ScheduledBackupService(
        string dataFolder,
        Func<byte[]> exportSnapshot,
        Func<int> readIntervalDays,
        Func<int> readRetentionDays)
    {
        if (string.IsNullOrWhiteSpace(dataFolder)) throw new ArgumentException("数据目录不能为空。", nameof(dataFolder));

        _backupDirectory = Path.Combine(dataFolder, BackupDirectoryName);
        _exportSnapshot = exportSnapshot ?? throw new ArgumentNullException(nameof(exportSnapshot));
        _readIntervalDays = readIntervalDays ?? throw new ArgumentNullException(nameof(readIntervalDays));
        _readRetentionDays = readRetentionDays ?? throw new ArgumentNullException(nameof(readRetentionDays));
    }

    internal void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer != null) return;
            // 首检在线程池立即执行；Timer 负责后续每分钟检查。
            _timer = new Timer(OnTimer, null, TimeSpan.Zero, CheckInterval);
        }
    }

    /// <summary>同步执行一次检查；重复定时回调会串行执行。</summary>
    internal void RunOnce(DateTime utcNow)
    {
        var now = NormalizeUtc(utcNow);
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                CheckAndBackup(now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // 备份失败不影响应用运行，且不记录统计内容或路径。
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            var timer = _timer;
            _timer = null;
            timer?.Dispose();
            // RunOnce 使用同一把锁，返回时正在运行的检查已结束。
        }
    }

    private void OnTimer(object? state) => RunOnce(DateTime.UtcNow);

    private void CheckAndBackup(DateTime now)
    {
        if (!Directory.Exists(_backupDirectory))
        {
            try { Directory.CreateDirectory(_backupDirectory); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
        }
        if ((File.GetAttributes(_backupDirectory) & FileAttributes.ReparsePoint) != 0) return;

        var existing = GetManagedBackups();
        DateTime newestTime = DateTime.MinValue;
        string? newestName = null;
        foreach (var backup in existing)
        {
            if (backup.Timestamp > newestTime)
            {
                newestTime = backup.Timestamp;
                newestName = backup.Name;
            }
        }

        var due = newestName == null || (now - newestTime).TotalDays >= _readIntervalDays();
        if (!due)
        {
            PruneExpired(now, newestName, existing);
            return;
        }

        var snapshot = _exportSnapshot();
        var targetName = BackupFileNamePrefix +
            now.ToString(BackupFileNameTimestampFormat, CultureInfo.InvariantCulture) +
            BackupFileNameSuffix;
        var targetPath = Path.Combine(_backupDirectory, targetName);
        string? tempPath = null;
        bool ownsTempFile = false;
        bool moved = false;
        try
        {
            tempPath = Path.Combine(
                _backupDirectory,
                BackupFileNamePrefix + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.WriteThrough))
            {
                ownsTempFile = true;
                stream.Write(snapshot, 0, snapshot.Length);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(tempPath, targetPath);
                moved = true;
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                // 同 UTC 时间戳已有副本：保留原件，也不基于它执行清理。
            }
        }
        finally
        {
            if (ownsTempFile && tempPath != null)
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        if (!moved) return;
        existing.Add(new ManagedBackup(targetName, now));
        PruneExpired(now, targetName, existing);
    }

    private List<ManagedBackup> GetManagedBackups()
    {
        var backups = new List<ManagedBackup>();
        foreach (var path in Directory.GetFiles(_backupDirectory))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            var name = Path.GetFileName(path);
            var timestamp = TryParseBackupTimestamp(name);
            if (timestamp != DateTime.MinValue) backups.Add(new ManagedBackup(name, timestamp));
        }
        return backups;
    }

    private void PruneExpired(DateTime now, string? newestName, List<ManagedBackup> backups)
    {
        var retention = _readRetentionDays();
        foreach (var backup in backups)
        {
            if (backup.Name == newestName) continue; // 过期也至少保留最近一份成功备份。
            if ((now - backup.Timestamp).TotalDays > retention)
            {
                File.Delete(Path.Combine(_backupDirectory, backup.Name));
            }
        }
    }

    internal static DateTime TryParseBackupTimestamp(string fileName)
    {
        if (fileName.Length != BackupFileNamePrefix.Length + BackupFileNameTimestampFormat.Length + BackupFileNameSuffix.Length ||
            !fileName.StartsWith(BackupFileNamePrefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(BackupFileNameSuffix, StringComparison.Ordinal))
        {
            return DateTime.MinValue;
        }

        var text = fileName.Substring(
            BackupFileNamePrefix.Length,
            BackupFileNameTimestampFormat.Length);
        return DateTime.TryParseExact(
            text,
            BackupFileNameTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp) && timestamp.Kind == DateTimeKind.Utc
                ? timestamp
                : DateTime.MinValue;
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private sealed class ManagedBackup
    {
        internal ManagedBackup(string name, DateTime timestamp)
        {
            Name = name;
            Timestamp = timestamp;
        }

        internal string Name { get; }
        internal DateTime Timestamp { get; }
    }
}
