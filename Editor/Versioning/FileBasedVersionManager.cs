#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AvatarSmartBackup.Versioning
{
    /// <summary>
    /// Simple file-based versioning system
    /// Much more stable than database approaches for Unity
    /// </summary>
    public class FileBasedVersionManager : IDisposable
    {
        private readonly string _versionsRoot;
        private readonly string _indexFile;
        private bool _disposed = false;

        public FileBasedVersionManager()
        {
            _versionsRoot = Path.Combine(FileUtilEx.BackupRoot, "Versions");
            _indexFile = Path.Combine(_versionsRoot, "versions.json");
            Directory.CreateDirectory(_versionsRoot);
            EnsureIndexFile();
        }

        private void EnsureIndexFile()
        {
            if (!File.Exists(_indexFile))
            {
                var index = new VersionIndex { versions = new List<VersionInfo>() };
                SaveIndex(index);
            }
        }

        private VersionIndex LoadIndex()
        {
            try
            {
                if (!File.Exists(_indexFile))
                    return new VersionIndex { versions = new List<VersionInfo>() };

                string json = File.ReadAllText(_indexFile);
                return JsonUtility.FromJson<VersionIndex>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ASB] Failed to load versions index: {ex.Message}");
                return new VersionIndex { versions = new List<VersionInfo>() };
            }
        }

        private void SaveIndex(VersionIndex index)
        {
            try
            {
                string json = JsonUtility.ToJson(index, true);
                File.WriteAllText(_indexFile, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Failed to save versions index: {ex.Message}");
            }
        }

        /// <summary>
        /// Create a new version from current backup
        /// </summary>
        public bool CreateVersion(string description, string currentBackupPath)
        {
            try
            {
                var index = LoadIndex();
                int nextId = index.versions.Count > 0 ? index.versions.Max(v => v.id) + 1 : 1;

                var version = new VersionInfo
                {
                    id = nextId,
                    description = description,
                    timestamp = DateTime.UtcNow,
                    backupPath = currentBackupPath
                };

                // Create version directory
                string versionDir = Path.Combine(_versionsRoot, $"v{nextId:D3}");
                Directory.CreateDirectory(versionDir);

                // Copy manifest for reference
                string manifestSrc = Path.Combine(currentBackupPath, "manifest.json");
                if (File.Exists(manifestSrc))
                {
                    File.Copy(manifestSrc, Path.Combine(versionDir, "manifest.json"), true);
                }

                // Add to index
                index.versions.Add(version);
                SaveIndex(index);

                Debug.Log($"[ASB] Created version {nextId}: {description}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Failed to create version: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get all versions
        /// </summary>
        public List<VersionInfo> GetVersions()
        {
            var index = LoadIndex();
            return index.versions.OrderByDescending(v => v.timestamp).ToList();
        }

        /// <summary>
        /// Get version by ID
        /// </summary>
        public VersionInfo GetVersion(int id)
        {
            var index = LoadIndex();
            return index.versions.FirstOrDefault(v => v.id == id);
        }

        /// <summary>
        /// Clean up old versions (keep only most recent)
        /// </summary>
        public void CleanupOldVersions(int keepCount = 10)
        {
            try
            {
                var index = LoadIndex();
                if (index.versions.Count <= keepCount) return;

                var versionsToKeep = index.versions
                    .OrderByDescending(v => v.timestamp)
                    .Take(keepCount)
                    .ToList();

                var versionsToDelete = index.versions
                    .Except(versionsToKeep)
                    .ToList();

                // Remove version directories
                foreach (var version in versionsToDelete)
                {
                    string versionDir = Path.Combine(_versionsRoot, $"v{version.id:D3}");
                    if (Directory.Exists(versionDir))
                    {
                        try
                        {
                            Directory.Delete(versionDir, true);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[ASB] Failed to delete version directory {versionDir}: {ex.Message}");
                        }
                    }
                }

                // Update index
                index.versions = versionsToKeep;
                SaveIndex(index);

                if (versionsToDelete.Count > 0)
                {
                    Debug.Log($"[ASB] Cleaned up {versionsToDelete.Count} old versions");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Failed to cleanup old versions: {ex.Message}");
            }
        }

        /// <summary>
        /// Get version count
        /// </summary>
        public int GetVersionCount()
        {
            var index = LoadIndex();
            return index.versions.Count;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    [Serializable]
    public class VersionIndex
    {
        public List<VersionInfo> versions;
    }

    [Serializable]
    public class VersionInfo
    {
        public int id;
        public string description;
        public DateTime timestamp;
        public string backupPath;
    }
}
#endif