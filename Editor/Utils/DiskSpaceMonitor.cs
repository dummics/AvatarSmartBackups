#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;
namespace AvatarSmartBackup
{
    internal enum DiskSpaceStatus
    {
        Unknown,
        Ok,
        Warning,
        Critical,
        Error
    }
    internal enum DiskSpaceStage
    {
        PreCheck,
        PlanEstimate,
        PostBackup
    }
    internal struct DiskSpaceSnapshot
    {
        public string driveName;
        public long totalBytes;
        public long freeBytes;
        public long backupSizeBytes;
        public DateTime capturedAtUtc;
    }
    internal struct DiskSpaceReport
    {
        public DiskSpaceStatus Status;
        public DiskSpaceSnapshot Snapshot;
        public long RequiredBytes;
        public string Message;
        public bool BlockBackup;
        public DiskSpaceStage Stage;
    }
    internal static class DiskSpaceMonitor
    {
        const long BytesPerMB = 1024L * 1024L;
        static readonly object _lock = new object();
        static DiskSpaceReport _lastReport;
        static string _lastBackupRoot = null!;
        static long _lastBackupSizeBytes;
        static DateTime _lastBackupSizeTimestampUtc;
        static readonly TimeSpan SizeCacheDuration = TimeSpan.FromMinutes(5);
        public static DiskSpaceReport LastReport
        {
            get
            {
                lock (_lock) return _lastReport;
            }
        }
        public static DiskSpaceReport Check(BackupSettings settings, long requiredBytes, DiskSpaceStage stage)
        {
            if (settings == null || !settings.diskSpaceProtection)
            {
                return UpdateLast(new DiskSpaceReport
                {
                    Status = DiskSpaceStatus.Unknown,
                    Snapshot = default,
                    RequiredBytes = requiredBytes,
                    Message = string.Empty,
                    BlockBackup = false,
                    Stage = stage
                });
            }
            try
            {
                var snapshot = CaptureSnapshot();
                if (snapshot.totalBytes <= 0)
                {
                    return UpdateLast(new DiskSpaceReport
                    {
                        Status = DiskSpaceStatus.Unknown,
                        Snapshot = snapshot,
                        RequiredBytes = requiredBytes,
                        Message = "Unable to determine disk capacity.",
                        BlockBackup = false,
                        Stage = stage
                    });
                }
                long warningAbs = Math.Max(0, settings.diskWarningFreeMB * BytesPerMB);
                long criticalAbs = Math.Max(0, settings.diskCriticalFreeMB * BytesPerMB);
                long warningPercent = settings.diskWarningFreePercent > 0
                    ? (long)(snapshot.totalBytes * settings.diskWarningFreePercent)
                    : 0;
                long criticalPercent = settings.diskCriticalFreePercent > 0
                    ? (long)(snapshot.totalBytes * settings.diskCriticalFreePercent)
                    : 0;
                long warningThreshold = Math.Max(warningAbs, warningPercent);
                long criticalThreshold = Math.Max(criticalAbs, criticalPercent);
                long bufferBytes = Math.Max(0, settings.diskPreBackupBufferMB * BytesPerMB);
                long projectedFree = snapshot.freeBytes - Math.Max(0, requiredBytes) - bufferBytes;
                if (projectedFree < 0) projectedFree = 0;
                bool criticalNow = criticalThreshold > 0 && snapshot.freeBytes <= criticalThreshold;
                bool warningNow = warningThreshold > 0 && snapshot.freeBytes <= warningThreshold;
                bool criticalPredicted = criticalThreshold > 0 && projectedFree <= criticalThreshold;
                bool warningPredicted = warningThreshold > 0 && projectedFree <= warningThreshold;
                var status = DiskSpaceStatus.Ok;
                if (criticalNow || criticalPredicted) status = DiskSpaceStatus.Critical;
                else if (warningNow || warningPredicted) status = DiskSpaceStatus.Warning;
                var sb = new StringBuilder();
                sb.Append($"Free: {FormatBytes(snapshot.freeBytes)} / Total: {FormatBytes(snapshot.totalBytes)} (Drive {snapshot.driveName})");
                sb.AppendLine();
                sb.Append($"Backups occupy: {FormatBytes(snapshot.backupSizeBytes)}");
                if (requiredBytes > 0)
                {
                    sb.AppendLine();
                    sb.Append($"Estimated next backup: {FormatBytes(requiredBytes + bufferBytes)} (incl. buffer)");
                }
                if (status == DiskSpaceStatus.Warning)
                {
                    sb.AppendLine();
                    sb.Append("Warning: low disk space. Consider freeing storage or pruning older versions.");
                }
                else if (status == DiskSpaceStatus.Critical)
                {
                    sb.AppendLine();
                    sb.Append("Critical: insufficient disk space for safe backup.");
                }
                bool block = status == DiskSpaceStatus.Critical && settings.blockBackupOnLowSpace;
                return UpdateLast(new DiskSpaceReport
                {
                    Status = status,
                    Snapshot = snapshot,
                    RequiredBytes = requiredBytes,
                    Message = sb.ToString(),
                    BlockBackup = block,
                    Stage = stage
                });
            }
            catch (Exception ex)
            {
                return UpdateLast(new DiskSpaceReport
                {
                    Status = DiskSpaceStatus.Error,
                    Snapshot = default,
                    RequiredBytes = requiredBytes,
                    Message = $"Disk space check failed: {ex.Message}",
                    BlockBackup = false,
                    Stage = stage
                });
            }
        }
        public static void InvalidateSizeCache()
        {
            lock (_lock)
            {
                _lastBackupSizeTimestampUtc = DateTime.MinValue;
            }
        }
        static DiskSpaceReport UpdateLast(DiskSpaceReport report)
        {
            lock (_lock)
            {
                _lastReport = report;
            }
            return report;
        }
        static DiskSpaceSnapshot CaptureSnapshot()
        {
            string root = FileUtilEx.BackupRoot;
            long backupSize = GetBackupSizeCached(root);
            string driveName = string.Empty;
            long total = 0;
            long free = 0;
            try
            {
                string pathRoot = Path.GetPathRoot(string.IsNullOrEmpty(root) ? Application.dataPath : root);
                if (!string.IsNullOrEmpty(pathRoot))
                {
                    var drive = new DriveInfo(pathRoot);
                    driveName = drive.Name;
                    total = drive.TotalSize;
                    free = drive.AvailableFreeSpace;
                }
            }
            catch
            {
                // ignored, fallback to zeros
            }
            return new DiskSpaceSnapshot
            {
                driveName = string.IsNullOrEmpty(driveName) ? "?" : driveName,
                totalBytes = total,
                freeBytes = free,
                backupSizeBytes = backupSize,
                capturedAtUtc = DateTime.UtcNow
            };
        }
        static long GetBackupSizeCached(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;
            lock (_lock)
            {
                if (_lastBackupRoot == root && DateTime.UtcNow - _lastBackupSizeTimestampUtc < SizeCacheDuration)
                    return _lastBackupSizeBytes;
            }
            long size = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        size += info.Length;
                    }
                    catch { }
                }
            }
            catch { }
            lock (_lock)
            {
                _lastBackupRoot = root;
                _lastBackupSizeBytes = size;
                _lastBackupSizeTimestampUtc = DateTime.UtcNow;
            }
            return size;
        }
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            double value = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int idx = 0;
            while (value >= 1024 && idx < units.Length - 1)
            {
                value /= 1024;
                idx++;
            }
            return idx == 0 ? $"{value:0} {units[idx]}" : $"{value:0.0} {units[idx]}";
        }
    }
}
#endif
