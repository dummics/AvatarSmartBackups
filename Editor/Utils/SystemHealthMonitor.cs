#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// System health monitor and preventive checks
    /// Prevents backup failures before they happen
    /// </summary>
    public static class SystemHealthMonitor
    {
        /// <summary>
        /// Check if system is healthy enough for backup operations
        /// </summary>
        public static HealthCheckResult CheckSystemHealth()
        {
            var result = new HealthCheckResult();
            
            try
            {
                // Check disk space
                CheckDiskSpace(result);
                
                // Check database health
                CheckDatabaseHealth(result);
                
                // Check file permissions
                CheckFilePermissions(result);
                
                // Check memory usage
                CheckMemoryUsage(result);
                
                result.OverallHealth = DetermineOverallHealth(result);
            }
            catch (Exception ex)
            {
                result.AddError($"Health check failed: {ex.Message}");
                result.OverallHealth = HealthStatus.Critical;
            }

            return result;
        }

        static void CheckDiskSpace(HealthCheckResult result)
        {
            try
            {
                var backupRoot = FileUtilEx.BackupRoot;
                var drive = new DriveInfo(Path.GetPathRoot(backupRoot));
                
                long freeSpaceGB = drive.AvailableFreeSpace / (1024 * 1024 * 1024);
                long totalSpaceGB = drive.TotalSize / (1024 * 1024 * 1024);
                
                result.DiskSpaceGB = freeSpaceGB;
                result.TotalDiskSpaceGB = totalSpaceGB;
                
                if (freeSpaceGB < 1) // Less than 1GB
                {
                    result.AddError($"Critical: Only {freeSpaceGB}GB free space remaining");
                }
                else if (freeSpaceGB < 5) // Less than 5GB
                {
                    result.AddWarning($"Low disk space: {freeSpaceGB}GB remaining");
                }
                else
                {
                    result.AddInfo($"Disk space OK: {freeSpaceGB}GB available");
                }
            }
            catch (Exception ex)
            {
                result.AddError($"Disk space check failed: {ex.Message}");
            }
        }

        static void CheckDatabaseHealth(HealthCheckResult result)
        {
            try
            {
                using var versionManager = new AvatarSmartBackup.Versioning.SimpleVersionManager();
                
                if (versionManager.TestConnection())
                {
                    var stats = versionManager.GetStats();
                    result.VersionCount = stats.TotalVersions;
                    result.AddInfo($"Database OK: {stats.TotalVersions} versions tracked");
                    
                    // Check for excessive versions
                    if (stats.TotalVersions > 100)
                    {
                        result.AddWarning($"Many versions ({stats.TotalVersions}) - consider cleanup");
                    }
                }
                else
                {
                    result.AddError("Database connection failed - versioning will use fallback mode");
                }
            }
            catch (Exception ex)
            {
                result.AddWarning($"Database check failed: {ex.Message} - using fallback mode");
            }
        }

        static void CheckFilePermissions(HealthCheckResult result)
        {
            try
            {
                var backupRoot = FileUtilEx.BackupRoot;
                
                // Test write permissions
                var testFile = Path.Combine(backupRoot, "permission_test.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                
                result.AddInfo("File permissions OK");
            }
            catch (UnauthorizedAccessException)
            {
                result.AddError("No write permissions to backup folder - backup will fail");
            }
            catch (Exception ex)
            {
                result.AddWarning($"Permission check inconclusive: {ex.Message}");
            }
        }

        static void CheckMemoryUsage(HealthCheckResult result)
        {
            try
            {
                long memoryMB = GC.GetTotalMemory(false) / (1024 * 1024);
                result.MemoryUsageMB = memoryMB;
                
                if (memoryMB > 2048) // > 2GB
                {
                    result.AddWarning($"High memory usage: {memoryMB}MB - consider restarting Unity");
                }
                else
                {
                    result.AddInfo($"Memory usage normal: {memoryMB}MB");
                }
            }
            catch (Exception ex)
            {
                result.AddWarning($"Memory check failed: {ex.Message}");
            }
        }

        static HealthStatus DetermineOverallHealth(HealthCheckResult result)
        {
            if (result.Errors.Count > 0)
                return HealthStatus.Critical;
            if (result.Warnings.Count > 2)
                return HealthStatus.Warning;
            if (result.Warnings.Count > 0)
                return HealthStatus.Caution;
            return HealthStatus.Healthy;
        }
    }

    public class HealthCheckResult
    {
        public HealthStatus OverallHealth { get; set; } = HealthStatus.Unknown;
        public long DiskSpaceGB { get; set; }
        public long TotalDiskSpaceGB { get; set; }
        public int VersionCount { get; set; }
        public long MemoryUsageMB { get; set; }
        
        public System.Collections.Generic.List<string> Errors { get; } = new();
        public System.Collections.Generic.List<string> Warnings { get; } = new();
        public System.Collections.Generic.List<string> Infos { get; } = new();

        public void AddError(string message) => Errors.Add(message);
        public void AddWarning(string message) => Warnings.Add(message);
        public void AddInfo(string message) => Infos.Add(message);

        public bool IsHealthy => OverallHealth == HealthStatus.Healthy;
        public bool CanBackup => OverallHealth != HealthStatus.Critical;

        public string GetSummary()
        {
            return $"Health: {OverallHealth}, Disk: {DiskSpaceGB}GB, Versions: {VersionCount}, RAM: {MemoryUsageMB}MB";
        }

        public string GetDetailedReport()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine($"=== SYSTEM HEALTH REPORT ===");
            report.AppendLine($"Overall Status: {OverallHealth}");
            report.AppendLine($"Disk Space: {DiskSpaceGB}GB / {TotalDiskSpaceGB}GB available");
            report.AppendLine($"Version Count: {VersionCount}");
            report.AppendLine($"Memory Usage: {MemoryUsageMB}MB");
            report.AppendLine();

            if (Errors.Count > 0)
            {
                report.AppendLine("🚨 ERRORS:");
                foreach (var error in Errors)
                    report.AppendLine($"  • {error}");
                report.AppendLine();
            }

            if (Warnings.Count > 0)
            {
                report.AppendLine("⚠️ WARNINGS:");
                foreach (var warning in Warnings)
                    report.AppendLine($"  • {warning}");
                report.AppendLine();
            }

            if (Infos.Count > 0)
            {
                report.AppendLine("ℹ️ INFO:");
                foreach (var info in Infos)
                    report.AppendLine($"  • {info}");
            }

            return report.ToString();
        }
    }

    public enum HealthStatus
    {
        Unknown,
        Healthy,
        Caution,
        Warning,
        Critical
    }
}
#endif