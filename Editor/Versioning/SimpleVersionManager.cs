#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using SQLite;

namespace AvatarSmartBackup.Versioning
{
    /// <summary>
    /// Simple version management using SQLite-net
    /// Tracks backup versions without complex dependency management
    /// </summary>
    public class SimpleVersionManager : IDisposable
    {
        private readonly string _dbPath;
        private SQLiteConnection _connection;
        private bool _disposed = false;

        public SimpleVersionManager()
        {
            var dbFolder = Path.Combine(FileUtilEx.BackupRoot, "Versions");
            Directory.CreateDirectory(dbFolder);
            _dbPath = Path.Combine(dbFolder, "versions.db");
            
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            try
            {
                _connection = new SQLiteConnection(_dbPath);
                _connection.CreateTable<BackupVersion>();
                _connection.CreateTable<BackupFile>();
                Debug.Log($"[ASB] Version database initialized: {_dbPath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Failed to initialize version database: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Record a new backup as a version
        /// </summary>
        public int RecordBackupAsVersion(string description, string backupPath, int fileCount, long totalBytes)
        {
            try
            {
                var version = new BackupVersion
                {
                    BackupPath = MakeRelativePath(backupPath),
                    Description = description ?? $"Backup created at {DateTime.Now:yyyy-MM-dd HH:mm}",
                    Timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
                    HasSnapshot = false,
                    TotalSizeBytes = totalBytes,
                    FileCount = fileCount
                };

                _connection.Insert(version);

                Debug.Log($"[ASB] Recorded backup version {version.Id}: {description}");
                return version.Id;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Failed to record backup version: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Record a new backup as a version (legacy method)
        /// </summary>
        public int RecordBackupAsVersion(string backupPath, string description = null, List<string> changedFiles = null)
        {
            try
            {
                var version = new BackupVersion
                {
                    BackupPath = MakeRelativePath(backupPath),
                    Description = description ?? $"Backup created at {DateTime.Now:yyyy-MM-dd HH:mm}",
                    Timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
                    HasSnapshot = false,
                    TotalSizeBytes = CalculateDirectorySize(backupPath),
                    FileCount = changedFiles?.Count ?? 0
                };

                _connection.Insert(version);

                // Record individual files if provided
                if (changedFiles != null)
                {
                    foreach (var file in changedFiles)
                    {
                        var backupFile = new BackupFile
                        {
                            VersionId = version.Id,
                            RelativePath = MakeRelativePath(file),
                            FileSizeBytes = File.Exists(file) ? new System.IO.FileInfo(file).Length : 0,
                            LastModified = File.Exists(file) ? File.GetLastWriteTime(file).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") : DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
                        };
                        _connection.Insert(backupFile);
                    }
                }

                Debug.Log($"[ASB] Recorded backup version {version.Id}: {description}");
                return version.Id;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Failed to record backup version: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Get recent versions for UI display
        /// </summary>
        public List<BackupVersion> GetRecentVersions(int maxCount = 20)
        {
            try
            {
                return _connection.Table<BackupVersion>()
                    .OrderByDescending(v => v.Timestamp)
                    .Take(maxCount)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to get recent versions: {ex.Message}");
                return new List<BackupVersion>();
            }
        }

        /// <summary>
        /// Get simple statistics for UI
        /// </summary>
        public SimpleVersionStats GetStats()
        {
            try
            {
                var versions = _connection.Table<BackupVersion>().ToList();
                
                return new SimpleVersionStats
                {
                    TotalVersions = versions.Count,
                    TotalStorageBytes = versions.Sum(v => v.TotalSizeBytes),
                    OldestVersion = versions.Any() ? versions.Min(v => v.GetDateTime()) : null,
                    NewestVersion = versions.Any() ? versions.Max(v => v.GetDateTime()) : null,
                    StorageDirectory = FileUtilEx.BackupRoot
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to get version stats: {ex.Message}");
                return new SimpleVersionStats
                {
                    StorageDirectory = FileUtilEx.BackupRoot
                };
            }
        }

        /// <summary>
        /// Update a version to mark it as having a snapshot
        /// </summary>
        public void UpdateVersionSnapshot(int versionId, string snapshotPath)
        {
            try
            {
                var version = _connection.Find<BackupVersion>(versionId);
                if (version != null)
                {
                    version.HasSnapshot = true;
                    version.SnapshotPath = MakeRelativePath(snapshotPath);
                    _connection.Update(version);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to update version snapshot: {ex.Message}");
            }
        }

        /// <summary>
        /// Clean up old versions (keep last N versions)
        /// </summary>
        public int CleanupOldVersions(int keepCount = 50)
        {
            try
            {
                var versions = _connection.Table<BackupVersion>()
                    .OrderByDescending(v => v.Timestamp)
                    .ToList();

                if (versions.Count <= keepCount)
                    return 0;

                var toDelete = versions.Skip(keepCount).ToList();
                int deletedCount = 0;

                foreach (var version in toDelete)
                {
                    try
                    {
                        // Delete version files
                        var versionPath = Path.Combine(FileUtilEx.BackupRoot, "Versions", $"version_{version.Id:D6}");
                        if (Directory.Exists(versionPath))
                        {
                            Directory.Delete(versionPath, true);
                        }

                        // Delete from database
                        _connection.Delete<BackupVersion>(version.Id);
                        _connection.Execute("DELETE FROM BackupFiles WHERE VersionId = ?", version.Id);
                        deletedCount++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to delete version {version.Id}: {ex.Message}");
                    }
                }

                Debug.Log($"[ASB] Cleaned up {deletedCount} old versions");
                return deletedCount;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Version cleanup failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Get files for a specific version
        /// </summary>
        public List<BackupFile> GetVersionFiles(int versionId)
        {
            try
            {
                return _connection.Table<BackupFile>()
                    .Where(f => f.VersionId == versionId)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to get version files: {ex.Message}");
                return new List<BackupFile>();
            }
        }

        /// <summary>
        /// Test database connection
        /// </summary>
        public bool TestConnection()
        {
            try
            {
                _connection.Execute("SELECT COUNT(*) FROM BackupVersions");
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Get all versions (for maintenance purposes)
        /// </summary>
        public List<BackupVersion> GetAllVersions()
        {
            try
            {
                return _connection.Table<BackupVersion>()
                    .OrderBy(v => v.Timestamp)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Failed to get all versions: {ex.Message}");
                return new List<BackupVersion>();
            }
        }

        /// <summary>
        /// Delete a specific version
        /// </summary>
        public void DeleteVersion(int versionId)
        {
            try
            {
                _connection.Delete<BackupVersion>(versionId);
                
                // Also delete associated files
                _connection.Execute("DELETE FROM BackupFiles WHERE VersionId = ?", versionId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Failed to delete version {versionId}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Optimize database (vacuum, reindex)
        /// </summary>
        public void OptimizeDatabase()
        {
            try
            {
                _connection.Execute("VACUUM");
                _connection.Execute("REINDEX");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ASB] Database optimization failed: {ex.Message}");
                throw;
            }
        }

        private string MakeRelativePath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return null;

            var backupRoot = FileUtilEx.BackupRoot;
            if (absolutePath.StartsWith(backupRoot))
            {
                return absolutePath.Substring(backupRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return absolutePath;
        }

        private long CalculateDirectorySize(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    return 0;

                return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(file => new System.IO.FileInfo(file).Length);
            }
            catch
            {
                return 0;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _connection?.Close();
                _connection?.Dispose();
                _disposed = true;
            }
        }
    }
}
#endif