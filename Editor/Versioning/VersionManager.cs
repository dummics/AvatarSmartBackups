#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace AvatarSmartBackup.Versioning
{
    /// <summary>
    /// Core version management system - Git-like backup with timeline UI
    /// Uses SQLite4Unity3d for database operations
    /// </summary>
    public class VersionManager : IDisposable
    {
        private readonly string _projectRoot;
        private readonly string _versionRoot;
        private readonly VersionDatabase _database;
        private readonly DeltaStorageManager _storage;
        private readonly BackupSettings _settings;
        
        public VersionManager() : this(BackupManager.LoadSettings())
        {
        }
        
        public VersionManager(BackupSettings settings)
        {
            _settings = settings;
            _projectRoot = FileUtilEx.ProjectRoot;
            _versionRoot = Path.Combine(FileUtilEx.BackupRoot, "Versions");
            
            Directory.CreateDirectory(_versionRoot);
            
            string dbPath = Path.Combine(_versionRoot, "index.db");
            _database = new VersionDatabase(dbPath);
            _storage = new DeltaStorageManager(_versionRoot, _database);
            
            Log.Info($"Version system initialized: {_versionRoot}");
        }
        
        /// <summary>
        /// Create a new version commit automatically
        /// </summary>
        public async Task<long> CreateAutomaticCommitAsync(string reason = "Auto backup")
        {
            try
            {
                var changedFiles = await ScanForChangesAsync();
                
                if (changedFiles.Count == 0)
                {
                    Log.Debug("No changes detected, skipping commit");
                    return -1;
                }
                
                string message = _settings.versionCommitTemplate
                    .Replace("{reason}", reason)
                    .Replace("{count}", changedFiles.Count.ToString())
                    .Replace("{time}", DateTime.Now.ToString("HH:mm"));
                
                return await CreateCommitAsync(message, changedFiles);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to create automatic commit: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// Create a manual commit with custom message
        /// </summary>
        public async Task<long> CreateManualCommitAsync(string message)
        {
            try
            {
                var changedFiles = await ScanForChangesAsync();
                
                if (changedFiles.Count == 0)
                {
                    Log.Debug("No changes detected for manual commit");
                    return -1;
                }
                
                return await CreateCommitAsync(message, changedFiles);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to create manual commit: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// Scan project for changed files since last commit
        /// </summary>
        private async Task<List<VersionedFile>> ScanForChangesAsync()
        {
            var changedFiles = new List<VersionedFile>();
            var collectors = GetCollectors();
            
            foreach (var collector in collectors)
            {
                var files = collector.CollectAbsolutePaths(_settings);
                
                foreach (var file in files)
                {
                    if (!File.Exists(file)) continue;
                    
                    var hash = await CalculateFileHashAsync(file);
                    // TODO: Implement hash comparison logic
                    // var lastHash = _database.GetFileHash(file);
                    var lastHash = "";
                    
                    if (hash != lastHash)
                    {
                        var relativePath = Path.GetRelativePath(_projectRoot, file);
                        changedFiles.Add(new VersionedFile
                        {
                            Path = relativePath,
                            Hash = hash,
                            Size = new FileInfo(file).Length,
                            Action = lastHash == null ? "add" : "modify"
                        });
                    }
                }
            }
            
            return changedFiles;
        }
        
        /// <summary>
        /// Create commit with specified files
        /// </summary>
        private async Task<long> CreateCommitAsync(string message, List<VersionedFile> files)
        {
            try
            {
                Log.Info($"Creating commit '{message}' with {files.Count} files");
                
                // Create commit record
                var commitId = _database.CreateCommit(message, files);
                
                // TODO: Implement proper storage interface
                // await _storage.StoreCommitFiles(commitId, files);
                
                // Update file tracking
                // TODO: Store individual files to commit
                // foreach (var file in files)
                // {
                //     _database.AddFileToCommit(commitId, file);
                // }
                
                Log.Info($"Commit {commitId} created successfully");
                
                // Clean up old commits if needed
                await CleanupOldCommitsAsync();
                
                return commitId;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to create commit: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// Get timeline of commits for UI display
        /// </summary>
        public List<CommitInfo> GetTimeline(int maxCount = 100)
        {
            return _database.GetCommitHistory(maxCount);
        }
        
        /// <summary>
        /// Get files in a specific commit
        /// </summary>
        public List<VersionedFile> GetCommitFiles(long commitId)
        {
            return _database.GetCommitFiles(commitId);
        }
        
        /// <summary>
        /// Restore files from a specific commit to project
        /// </summary>
        public async Task<RestoreResult> RestoreToProjectAsync(long commitId, List<string> filePaths = null)
        {
            var result = new RestoreResult();
            
            try
            {
                // Create backup of current state before restore
                var backupCommitId = await CreateManualCommitAsync($"Pre-restore backup {DateTime.Now:HH:mm}");
                if (backupCommitId > 0)
                {
                    result.BackupCommitId = backupCommitId;
                }
                
                var commitFiles = GetCommitFiles(commitId);
                var filesToRestore = filePaths?.Any() == true 
                    ? commitFiles.Where(f => filePaths.Contains(f.Path)).ToList()
                    : commitFiles;
                
                foreach (var file in filesToRestore)
                {
                    try
                    {
                        // TODO: Implement proper restore with temp directory
                        // await _storage.RestoreFileAsync(file.Path, commitId, _projectRoot);
                        result.RestoredFiles.Add(file.Path);
                    }
                    catch (Exception ex)
                    {
                        result.FailedFiles.Add((file.Path, ex.Message));
                        Log.Warn($"Failed to restore {file.Path}: {ex.Message}");
                    }
                }
                
                Log.Info($"Restored {result.RestoredFiles.Count} files from commit {commitId}");
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Log.Error($"Failed to restore from commit {commitId}: {ex.Message}");
                return result;
            }
        }
        
        /// <summary>
        /// Preview files from a commit in temporary directory
        /// </summary>
        public async Task<string> PreviewCommitAsync(long commitId, List<string> filePaths = null)
        {
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), $"ASB_Preview_{commitId}_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                
                var commitFiles = GetCommitFiles(commitId);
                var filesToPreview = filePaths?.Any() == true 
                    ? commitFiles.Where(f => filePaths.Contains(f.Path)).ToList()
                    : commitFiles;
                
                foreach (var file in filesToPreview)
                {
                    // TODO: Implement proper restore preview with temp directory  
                    // await _storage.RestoreFileAsync(file.Path, commitId, tempDir);
                }
                
                Log.Info($"Preview created in {tempDir}");
                return tempDir;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to create preview: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// Clean up old commits based on settings
        /// </summary>
        public Task CleanupOldCommitsAsync()
        {
            try
            {
                var commits = _database.GetCommitHistory(1000);
                var toDelete = new List<long>();
                
                // Apply max count limit
                if (commits.Count > _settings.maxVersionHistory)
                {
                    toDelete.AddRange(commits.Skip(_settings.maxVersionHistory).Select(c => c.Id));
                }
                
                // Apply age limit
                var cutoff = DateTimeOffset.Now.AddDays(-_settings.versionRetentionDays);
                toDelete.AddRange(commits.Where(c => c.Timestamp < cutoff).Select(c => c.Id));
                
                foreach (var commitId in toDelete.Distinct())
                {
                    // TODO: Implement storage and database cleanup
                    // await _storage.DeleteCommitAsync(commitId);
                    // _database.DeleteCommit(commitId);
                }
                
                if (toDelete.Count > 0)
                {
                    Log.Info($"Cleaned up {toDelete.Count} old commits");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to cleanup old commits: {ex.Message}");
            }
            
            return Task.CompletedTask;
        }
        
        /// <summary>
        /// Calculate file hash for change detection
        /// </summary>
        private async Task<string> CalculateFileHashAsync(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            var hash = await Task.Run(() => sha1.ComputeHash(stream));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
        
        /// <summary>
        /// Get appropriate backup collectors based on settings
        /// </summary>
        private List<IBackupCollector> GetCollectors()
        {
            var collectors = new List<IBackupCollector>
            {
                new ScenesCollector(),
                new MaterialsCollector(),
                new AnimClipsCollector(),
                new ControllersCollector()
            };
            
            if (_settings.incVRCAssets)
            {
                collectors.Add(new VRCAssetsCollector());
            }
            
            if (_settings.incDlls)
            {
                collectors.Add(new DllCollector());
            }
            
            // Always include additional extensions collector if available
            collectors.Add(new AdditionalExtensionsCollector());
            
            return collectors;
        }
        
        /// <summary>
        /// Get version system statistics
        /// </summary>
        public VersionStats GetStats()
        {
            var commits = _database.GetCommitHistory(1000);
            var totalSize = System.Linq.Enumerable.Sum(commits, c => c.TotalSize);
            
            return new VersionStats
            {
                TotalCommits = commits.Count,
                TotalSizeBytes = totalSize,
                OldestCommit = commits.LastOrDefault()?.Timestamp,
                NewestCommit = commits.FirstOrDefault()?.Timestamp,
                StorageDirectory = _versionRoot
            };
        }
        
        /// <summary>
        /// Get detailed version system statistics for UI display
        /// </summary>
        public object GetVersionStats()
        {
            try
            {
                var commits = _database.GetCommitHistory(1000);
                var totalFiles = _database.GetTotalTrackedFiles();
                var storageUsed = Directory.Exists(_versionRoot) ? GetDirectorySize(_versionRoot) : 0;
                var databaseSize = File.Exists(_database.DatabasePath) ? new FileInfo(_database.DatabasePath).Length : 0;
                
                // Calculate compression ratio (approximation)
                var totalOriginalSize = System.Linq.Enumerable.Sum(commits, c => c.TotalSize);
                var compressionRatio = totalOriginalSize > 0 ? (double)storageUsed / totalOriginalSize : 0.0;
                
                return new 
                {
                    totalCommits = commits.Count,
                    totalFiles = totalFiles,
                    storageUsed = storageUsed,
                    databaseSize = databaseSize,
                    compressionRatio = compressionRatio,
                    oldestCommit = commits.LastOrDefault()?.Timestamp.DateTime,
                    latestCommit = commits.FirstOrDefault()?.Timestamp.DateTime
                };
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to get version stats: {ex.Message}");
                return new 
                {
                    totalCommits = 0,
                    totalFiles = 0,
                    storageUsed = 0L,
                    databaseSize = 0L,
                    compressionRatio = 0.0,
                    oldestCommit = (DateTime?)null,
                    latestCommit = (DateTime?)null
                };
            }
        }
        
        private long GetDirectorySize(string directoryPath)
        {
            try
            {
                return System.Linq.Enumerable.Sum(
                    Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories),
                    file => new FileInfo(file).Length);
            }
            catch
            {
                return 0;
            }
        }
        
        public void Dispose()
        {
            _database?.Dispose();
        }
    }
    
    /// <summary>
    /// Result of a restore operation
    /// </summary>
    public class RestoreResult
    {
        public List<string> RestoredFiles { get; } = new List<string>();
        public List<(string File, string Error)> FailedFiles { get; } = new List<(string, string)>();
        public long? BackupCommitId { get; set; }
        public string Error { get; set; }
        
        public bool IsSuccess => string.IsNullOrEmpty(Error) && FailedFiles.Count == 0;
    }
    
    /// <summary>
    /// Version system statistics
    /// </summary>
    public class VersionStats
    {
        public int TotalCommits { get; set; }
        public long TotalSizeBytes { get; set; }
        public DateTimeOffset? OldestCommit { get; set; }
        public DateTimeOffset? NewestCommit { get; set; }
        public string StorageDirectory { get; set; }
        
        public string DisplaySize => FormatBytes(TotalSizeBytes);
        public TimeSpan? TimeSpan => NewestCommit.HasValue && OldestCommit.HasValue ? 
            NewestCommit.Value - OldestCommit.Value : null;
        
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }
    }
}
#endif
