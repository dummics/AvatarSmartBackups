#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Automatic cleanup and maintenance system for backups
    /// Prevents disk space issues and keeps system performant
    /// </summary>
    public static class BackupMaintenance
    {
        const int DEFAULT_MAX_VERSIONS = 50;
        const int DEFAULT_MAX_AGE_DAYS = 30;
        const long MIN_FREE_SPACE_GB = 2;

        /// <summary>
        /// Perform comprehensive maintenance
        /// </summary>
        public static MaintenanceResult PerformMaintenance(MaintenanceOptions options = null)
        {
            options ??= new MaintenanceOptions();
            var result = new MaintenanceResult();

            try
            {
                // Step 1: Health check
                var health = SystemHealthMonitor.CheckSystemHealth();
                result.InitialHealth = health;

                if (!health.CanBackup)
                {
                    result.AddWarning("System health critical - performing emergency cleanup");
                    options.MaxVersions = Math.Min(options.MaxVersions, 20);
                    options.MaxAgeDays = Math.Min(options.MaxAgeDays, 7);
                }

                // Step 2: Clean old versions
                CleanOldVersions(options, result);

                // Step 3: Clean orphaned files
                CleanOrphanedFiles(result);

                // Step 4: Optimize database
                OptimizeDatabase(result);

                // Step 5: Final health check
                result.FinalHealth = SystemHealthMonitor.CheckSystemHealth();
                result.Success = true;

                Debug.Log($"[ASB] Maintenance complete: {result.GetSummary()}");
            }
            catch (Exception ex)
            {
                result.AddError($"Maintenance failed: {ex.Message}");
                ResilientErrorHandler.HandleError(ex, ResilientErrorHandler.Component.Versioning, ResilientErrorHandler.Severity.Warning);
            }

            return result;
        }

        static void CleanOldVersions(MaintenanceOptions options, MaintenanceResult result)
        {
            try
            {
                using var versionManager = new AvatarSmartBackup.Versioning.SimpleVersionManager();
                
                var allVersions = versionManager.GetAllVersions();
                var toDelete = new List<AvatarSmartBackup.Versioning.BackupVersion>();

                // Rule 1: Too many versions
                if (allVersions.Count > options.MaxVersions)
                {
                    var excess = allVersions.Count - options.MaxVersions;
                    var oldest = allVersions.GetRange(0, excess);
                    toDelete.AddRange(oldest);
                    result.AddInfo($"Marked {excess} excess versions for deletion");
                }

                // Rule 2: Too old
                var cutoffDate = DateTime.Now.AddDays(-options.MaxAgeDays);
                foreach (var version in allVersions)
                {
                    if (version.GetDateTime() < cutoffDate && !toDelete.Contains(version))
                    {
                        toDelete.Add(version);
                    }
                }
                if (toDelete.Count > 0)
                {
                    result.AddInfo($"Marked {toDelete.Count} old versions for deletion");
                }

                // Rule 3: Emergency space cleanup
                var health = SystemHealthMonitor.CheckSystemHealth();
                if (health.DiskSpaceGB < MIN_FREE_SPACE_GB)
                {
                    var emergency = allVersions.Count / 2; // Delete half
                    var emergencyList = allVersions.GetRange(0, Math.Min(emergency, allVersions.Count));
                    foreach (var version in emergencyList)
                    {
                        if (!toDelete.Contains(version))
                            toDelete.Add(version);
                    }
                    result.AddWarning($"Emergency cleanup: marked {toDelete.Count} versions for deletion");
                }

                // Actually delete
                foreach (var version in toDelete)
                {
                    try
                    {
                        DeleteVersionFiles(version);
                        versionManager.DeleteVersion(version.Id);
                        result.DeletedVersions++;
                        result.SpaceFreedMB += EstimateVersionSize(version);
                    }
                    catch (Exception ex)
                    {
                        result.AddError($"Failed to delete version {version.Id}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.AddError($"Version cleanup failed: {ex.Message}");
            }
        }

        static void CleanOrphanedFiles(MaintenanceResult result)
        {
            try
            {
                var backupRoot = FileUtilEx.BackupRoot;
                var versionsFolders = Directory.GetDirectories(Path.Combine(backupRoot, "Versions"));

                using var versionManager = new AvatarSmartBackup.Versioning.SimpleVersionManager();
                var validVersions = new HashSet<string>();
                
                foreach (var version in versionManager.GetAllVersions())
                {
                    validVersions.Add($"version_{version.Id:D6}");
                }

                foreach (var folder in versionsFolders)
                {
                    var folderName = Path.GetFileName(folder);
                    if (!validVersions.Contains(folderName))
                    {
                        try
                        {
                            Directory.Delete(folder, true);
                            result.OrphanedFoldersDeleted++;
                            result.AddInfo($"Deleted orphaned folder: {folderName}");
                        }
                        catch (Exception ex)
                        {
                            result.AddError($"Failed to delete orphaned folder {folderName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.AddError($"Orphaned file cleanup failed: {ex.Message}");
            }
        }

        static void OptimizeDatabase(MaintenanceResult result)
        {
            try
            {
                using var versionManager = new AvatarSmartBackup.Versioning.SimpleVersionManager();
                versionManager.OptimizeDatabase();
                result.AddInfo("Database optimized");
            }
            catch (Exception ex)
            {
                result.AddWarning($"Database optimization failed: {ex.Message}");
            }
        }

        static void DeleteVersionFiles(AvatarSmartBackup.Versioning.BackupVersion version)
        {
            var versionFolder = Path.Combine(FileUtilEx.BackupRoot, "Versions", $"version_{version.Id:D6}");
            if (Directory.Exists(versionFolder))
            {
                Directory.Delete(versionFolder, true);
            }
        }

        static long EstimateVersionSize(AvatarSmartBackup.Versioning.BackupVersion version)
        {
            try
            {
                var versionFolder = Path.Combine(FileUtilEx.BackupRoot, "Versions", $"version_{version.Id:D6}");
                if (!Directory.Exists(versionFolder)) return 0;

                long totalSize = 0;
                var files = Directory.GetFiles(versionFolder, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    totalSize += new FileInfo(file).Length;
                }
                return totalSize / (1024 * 1024); // MB
            }
            catch
            {
                return 100; // Estimate 100MB if calculation fails
            }
        }

        /// <summary>
        /// Quick emergency cleanup when system is critically low on space
        /// </summary>
        public static void EmergencyCleanup()
        {
            try
            {
                Debug.Log("[ASB] EMERGENCY CLEANUP STARTED");
                
                var options = new MaintenanceOptions
                {
                    MaxVersions = 10, // Keep only 10 newest
                    MaxAgeDays = 3,   // Keep only 3 days
                    AggressiveCleanup = true
                };

                var result = PerformMaintenance(options);
                
                if (result.Success)
                {
                    Debug.Log($"[ASB] Emergency cleanup completed: freed {result.SpaceFreedMB}MB");
                }
                else
                {
                    Debug.LogError("[ASB] Emergency cleanup failed - manual intervention required");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Emergency cleanup error: {ex.Message}");
            }
        }
    }

    public class MaintenanceOptions
    {
        public int MaxVersions { get; set; } = 50;
        public int MaxAgeDays { get; set; } = 30;
        public bool AggressiveCleanup { get; set; } = false;
    }

    public class MaintenanceResult
    {
        public bool Success { get; set; }
        public int DeletedVersions { get; set; }
        public int OrphanedFoldersDeleted { get; set; }
        public long SpaceFreedMB { get; set; }
        public HealthCheckResult InitialHealth { get; set; }
        public HealthCheckResult FinalHealth { get; set; }

        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Infos { get; } = new();

        public void AddError(string message) => Errors.Add(message);
        public void AddWarning(string message) => Warnings.Add(message);
        public void AddInfo(string message) => Infos.Add(message);

        public string GetSummary()
        {
            return $"Deleted: {DeletedVersions} versions, {OrphanedFoldersDeleted} folders. " +
                   $"Freed: {SpaceFreedMB}MB. Health: {InitialHealth?.OverallHealth} → {FinalHealth?.OverallHealth}";
        }
    }
}
#endif