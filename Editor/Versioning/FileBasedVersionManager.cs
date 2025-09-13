#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
// Force recompilation
#pragma warning disable 0414
namespace AvatarSmartBackup
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
                index.schemaVersion = VersionIndex.CurrentSchemaVersion;
                string json = JsonUtility.ToJson(index, true);
                string tmp = _indexFile + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_indexFile)) File.Delete(_indexFile);
                File.Move(tmp, _indexFile);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Failed to save versions index: {ex.Message}");
            }
        }

        private bool MigrateIndexIfNeeded(VersionIndex index)
        {
            bool changed = false;
            if (index.schemaVersion <= 0)
            {
                // Schema upgrade: add missing fields
                foreach (var v in index.versions)
                {
                    if (string.IsNullOrEmpty(v.guid)) { v.guid = System.Guid.NewGuid().ToString("N"); changed = true; }
                    if (string.IsNullOrEmpty(v.createdUtc)) { v.createdUtc = v.timestamp == default ? DateTime.UtcNow.ToString("o") : v.timestamp.ToUniversalTime().ToString("o"); changed = true; }
                    // size/fileCount left 0 until lazy computed
                    if (string.IsNullOrEmpty(v.manifestFile)) { v.manifestFile = $"v{v.id:D3}/manifest.json"; changed = true; }
                }
                index.schemaVersion = VersionIndex.CurrentSchemaVersion;
                changed = true;
            }
            return changed;
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
                    description = string.IsNullOrWhiteSpace(description) ? $"Version {nextId}" : description.Trim(),
                    timestamp = DateTime.UtcNow,
                    backupPath = currentBackupPath,
                    guid = Guid.NewGuid().ToString("N"),
                    createdUtc = DateTime.UtcNow.ToString("o"),
                    pinned = false,
                    incomplete = true, // mark incomplete until fully written
                    manifestFile = $"v{nextId:D3}/manifest.json",
                    toolVersion = 1
                };

                // Create version directory
                string versionDir = Path.Combine(_versionsRoot, $"v{nextId:D3}");
                Directory.CreateDirectory(versionDir);

                long totalSize = 0;
                int fileCount = 0;

                // Copy manifest for reference (also collect size metrics lazily from file if present)
                string manifestSrc = Path.Combine(currentBackupPath, "manifest.json");
                if (File.Exists(manifestSrc))
                {
                    File.Copy(manifestSrc, Path.Combine(versionDir, "manifest.json"), true);
                    try
                    {
                        var json = File.ReadAllText(manifestSrc);
                        // Lightweight parse with JsonUtility requires wrapper; fallback quick scan
                        // We'll do a naive size accumulation from Current directory instead (safer & cheap for version creation)
                        var files = Directory.GetFiles(currentBackupPath, "*", SearchOption.AllDirectories)
                            .Where(p => !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && !p.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) && !p.EndsWith("backup.ok", StringComparison.OrdinalIgnoreCase));
                        foreach (var f in files)
                        {
                            try { var fi = new FileInfo(f); totalSize += fi.Length; fileCount++; } catch { }
                        }
                    }
                    catch { }
                }

                version.totalSizeBytes = totalSize;
                version.fileCount = fileCount;
                version.incomplete = false; // mark complete

                // Add to index
                index.versions.Add(version);
                MigrateIndexIfNeeded(index); // ensure schemaVersion set
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
            if (MigrateIndexIfNeeded(index)) SaveIndex(index);
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

        public bool SetPinned(int id, bool pinned)
        {
            try
            {
                var index = LoadIndex();
                var v = index.versions.FirstOrDefault(x => x.id == id);
                if (v == null) return false;
                if (v.pinned == pinned) return true;
                v.pinned = pinned;
                SaveIndex(index);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Failed to set pin for version {id}: {ex.Message}");
                return false;
            }
        }

        public bool UpdateDescription(int id, string newDescription)
        {
            try
            {
                newDescription = string.IsNullOrWhiteSpace(newDescription) ? null : newDescription.Trim();
                var index = LoadIndex();
                var v = index.versions.FirstOrDefault(x => x.id == id);
                if (v == null) return false;
                if (newDescription == null) return false;
                if (v.description == newDescription) return true;
                v.description = newDescription;
                SaveIndex(index);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Failed to update description for version {id}: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    [Serializable]
    public class VersionInfo
    {
        public int id;
        public string description;
        public DateTime timestamp;
        public string backupPath;
        public string guid;
        public string createdUtc; // ISO 8601
        public long totalSizeBytes;
        public int fileCount;
        public bool pinned;
        public string manifestFile; // relative path inside Versions root
        public bool incomplete;
        public int toolVersion;
    }

    [Serializable]
    public class VersionIndex
    {
        public static int CurrentSchemaVersion = 1;
        public int schemaVersion;
        public List<VersionInfo> versions;
    }

}
#endif