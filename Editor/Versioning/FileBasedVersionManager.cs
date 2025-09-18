#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// File-based versioning manager with incremental/ checkpoint support.
    /// </summary>
    public class FileBasedVersionManager : IDisposable
    {
        private readonly string _versionsRoot;
        private readonly string _indexFile;
        private bool _disposed;

        const string ManifestFileName = "manifest.json";
        const string DeltaFileName = "delta.json";
        const string SnapshotMarkerName = "version.ok";

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
                var index = new VersionIndex { versions = new List<VersionInfo>(), schemaVersion = VersionIndex.CurrentSchemaVersion };
                SaveIndex(index);
            }
        }

        private VersionIndex LoadIndex()
        {
            try
            {
                if (!File.Exists(_indexFile))
                    return new VersionIndex { versions = new List<VersionInfo>(), schemaVersion = VersionIndex.CurrentSchemaVersion };

                string json = File.ReadAllText(_indexFile);
                var index = JsonUtility.FromJson<VersionIndex>(json) ?? new VersionIndex { versions = new List<VersionInfo>() };
                if (index.versions == null)
                    index.versions = new List<VersionInfo>();
                if (index.schemaVersion <= 0)
                    index.schemaVersion = 1;
                return index;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ASB] Failed to load versions index: {ex.Message}");
                return new VersionIndex { versions = new List<VersionInfo>(), schemaVersion = VersionIndex.CurrentSchemaVersion };
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
            if (index == null) return false;

            if (index.schemaVersion < 3)
            {
                foreach (var v in index.versions)
                {
                    if (string.IsNullOrEmpty(v.guid)) { v.guid = Guid.NewGuid().ToString("N"); changed = true; }
                    if (v.timestamp == default) { v.timestamp = DateTime.UtcNow; changed = true; }
                    if (string.IsNullOrEmpty(v.createdUtc) || v.createdUtc.StartsWith("0001-01-01", StringComparison.Ordinal))
                    {
                        v.createdUtc = v.timestamp.ToUniversalTime().ToString("o");
                        changed = true;
                    }
                    if (string.IsNullOrEmpty(v.manifestFile)) { v.manifestFile = $"v{v.id:D3}/{ManifestFileName}"; changed = true; }
                    if (!v.corrupt) v.corrupt = false;
                }
                index.schemaVersion = 3;
                changed = true;
            }

            if (index.schemaVersion < VersionIndex.CurrentSchemaVersion)
            {
                foreach (var v in index.versions)
                {
                    if (v.timestamp == default) { v.timestamp = DateTime.UtcNow; changed = true; }
                    if (string.IsNullOrEmpty(v.createdUtc) || v.createdUtc.StartsWith("0001-01-01", StringComparison.Ordinal))
                    {
                        v.createdUtc = v.timestamp.ToUniversalTime().ToString("o");
                        changed = true;
                    }
                    if (string.IsNullOrEmpty(v.manifestFile))
                    {
                        v.manifestFile = $"v{v.id:D3}/{ManifestFileName}";
                        changed = true;
                    }
                    if (v.categoryStats == null)
                    {
                        v.categoryStats = new List<VersionCategoryStat>();
                        changed = true;
                    }
                    if (string.IsNullOrEmpty(v.deltaFile))
                    {
                        string deltaPath = Path.Combine(_versionsRoot, $"v{v.id:D3}", DeltaFileName);
                        v.deltaFile = File.Exists(deltaPath) ? $"v{v.id:D3}/{DeltaFileName}" : string.Empty;
                        changed = true;
                    }
                    if (!v.hasAdvancedMetadata)
                    {
                        v.isCheckpoint = true;
                        v.parentId = 0;
                        v.checkpointId = v.id;
                        v.incrementalDepth = 0;
                        v.changedFileCount = v.fileCount;
                        v.removedFileCount = 0;
                        v.changedBytes = v.totalSizeBytes;
                        v.removedBytes = 0;
                        v.hasAdvancedMetadata = true;
                        changed = true;
                    }
                    if (string.IsNullOrEmpty(v.backupPath))
                    {
                        v.backupPath = Path.Combine(_versionsRoot, $"v{v.id:D3}");
                        changed = true;
                    }
                }
                index.schemaVersion = VersionIndex.CurrentSchemaVersion;
                changed = true;
            }

            return changed;
        }

        public bool CreateVersion(string description, string currentBackupPath, BackupSettings settings = null, bool? forceCheckpoint = null)
        {
            try
            {
                if (string.IsNullOrEmpty(currentBackupPath) || !Directory.Exists(currentBackupPath))
                {
                    Debug.LogError("[ASB] CreateVersion failed – current backup path not found.");
                    return false;
                }

                string manifestPath = Path.Combine(currentBackupPath, ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    Debug.LogError("[ASB] CreateVersion failed – manifest.json missing. Run a backup first.");
                    return false;
                }

                var manifest = JsonUtility.FromJson<BackupManifest>(File.ReadAllText(manifestPath));
                if (manifest?.entries == null)
                {
                    Debug.LogError("[ASB] CreateVersion failed – manifest unreadable.");
                    return false;
                }

                var index = LoadIndex();
                var latest = GetLatestVersion(index);
                BackupManifest previousManifest = null;
                if (latest != null)
                    previousManifest = LoadManifestForVersion(latest.id);

                var diff = ComputeDiff(previousManifest, manifest);

                bool isCheckpoint = DetermineIfCheckpoint(latest, settings, forceCheckpoint, diff);

                int nextId = index.versions.Count > 0 ? index.versions.Max(v => v.id) + 1 : 1;
                string versionDir = Path.Combine(_versionsRoot, $"v{nextId:D3}");
                Directory.CreateDirectory(versionDir);

                if (isCheckpoint)
                {
                    CopyFullSnapshot(currentBackupPath, versionDir);
                }
                else
                {
                    CopyDeltaFiles(currentBackupPath, versionDir, diff);
                }

                File.Copy(manifestPath, Path.Combine(versionDir, ManifestFileName), true);
                File.WriteAllText(Path.Combine(versionDir, SnapshotMarkerName), "ok");

                int parentId = latest?.id ?? 0;
                int checkpointId = isCheckpoint ? nextId : ResolveCheckpointId(latest);
                int depth = isCheckpoint ? 0 : ((latest != null) ? latest.incrementalDepth + 1 : 1);

                var deltaMetadata = BuildDeltaMetadata(diff, nextId, isCheckpoint, parentId, checkpointId);
                string deltaPath = Path.Combine(versionDir, DeltaFileName);
                File.WriteAllText(deltaPath, JsonUtility.ToJson(deltaMetadata, true));

                long totalSize = manifest.entries.Sum(e => Math.Max(0, e.size));
                int totalCount = manifest.entries.Count;

                var version = new VersionInfo
                {
                    id = nextId,
                    description = string.IsNullOrWhiteSpace(description) ? $"Version {nextId}" : description.Trim(),
                    timestamp = DateTime.UtcNow,
                    backupPath = versionDir,
                    guid = Guid.NewGuid().ToString("N"),
                    createdUtc = DateTime.UtcNow.ToString("o"),
                    totalSizeBytes = totalSize,
                    fileCount = totalCount,
                    pinned = false,
                    manifestFile = $"v{nextId:D3}/{ManifestFileName}",
                    incomplete = false,
                    toolVersion = 1,
                    corrupt = false,
                    isCheckpoint = isCheckpoint,
                    parentId = parentId,
                    checkpointId = checkpointId,
                    incrementalDepth = depth,
                    changedFileCount = deltaMetadata.changedFileCount,
                    removedFileCount = deltaMetadata.removedFileCount,
                    changedBytes = deltaMetadata.changedBytes,
                    removedBytes = deltaMetadata.removedBytes,
                    categoryStats = deltaMetadata.categoryBreakdown ?? new List<VersionCategoryStat>(),
                    deltaFile = $"v{nextId:D3}/{DeltaFileName}",
                    hasAdvancedMetadata = true
                };

                index.versions.Add(version);
                if (MigrateIndexIfNeeded(index))
                    SaveIndex(index);
                else
                    SaveIndex(index);

                Debug.Log($"[ASB] Created {(isCheckpoint ? "checkpoint" : "incremental")} version {nextId}: {version.description} (changes: {version.changedFileCount}, removed: {version.removedFileCount}).");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Failed to create version: {ex.Message}");
                return false;
            }
        }

        private VersionInfo GetLatestVersion(VersionIndex index)
        {
            if (index?.versions == null || index.versions.Count == 0) return null;
            VersionInfo latest = null;
            foreach (var v in index.versions)
            {
                if (latest == null || v.id > latest.id)
                    latest = v;
            }
            return latest;
        }

        private BackupManifest LoadManifestForVersion(int versionId)
        {
            try
            {
                string versionDir = Path.Combine(_versionsRoot, $"v{versionId:D3}");
                string manifestPath = Path.Combine(versionDir, ManifestFileName);
                if (!File.Exists(manifestPath)) return null;
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonUtility.FromJson<BackupManifest>(json);
                return manifest;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ASB] Failed to load manifest for version {versionId}: {ex.Message}");
                return null;
            }
        }

        private static bool DetermineIfCheckpoint(VersionInfo latest, BackupSettings settings, bool? forceCheckpoint, ManifestDiff diff)
        {
            if (forceCheckpoint.HasValue)
                return forceCheckpoint.Value;

            if (latest == null)
                return true;

            int configured = 0;
            if (settings != null)
            {
                settings.EnsureVersioningDefaults();
                settings.SyncLegacyCheckpointInterval();
                configured = settings.GetCheckpointInterval();
            }
            if (configured > 0 && latest.incrementalDepth >= configured - 1)
                return true;

            if (latest.fileCount > 0)
            {
                int totalChanges = diff.Added.Count + diff.Modified.Count + diff.Removed.Count;
                if (totalChanges >= Math.Max(10, latest.fileCount / 2))
                    return true;
            }

            return false;
        }

        private static int ResolveCheckpointId(VersionInfo latest)
        {
            if (latest == null) return 0;
            if (latest.isCheckpoint) return latest.id;
            if (latest.checkpointId > 0) return latest.checkpointId;
            return latest.id;
        }

        private static void CopyFullSnapshot(string sourceRoot, string destinationRoot)
        {
            foreach (var src in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(src);
                if (string.Equals(name, "backup.ok", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "manifest_v2.json", StringComparison.OrdinalIgnoreCase)) continue;
                string rel = MakeRelative(src, sourceRoot);
                string dst = Path.Combine(destinationRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);
            }
        }

        private static void CopyDeltaFiles(string sourceRoot, string versionRoot, ManifestDiff diff)
        {
            if (diff == null) return;
            string deltaRoot = Path.Combine(versionRoot, "delta");
            foreach (var entry in diff.Added.Concat(diff.Modified))
            {
                string src = Path.Combine(sourceRoot, entry.relPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(src)) continue;
                string dst = Path.Combine(deltaRoot, entry.relPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);

                string srcMeta = src + ".meta";
                if (File.Exists(srcMeta))
                {
                    string dstMeta = dst + ".meta";
                    Directory.CreateDirectory(Path.GetDirectoryName(dstMeta));
                    File.Copy(srcMeta, dstMeta, true);
                }
            }
        }

        private static ManifestDiff ComputeDiff(BackupManifest previous, BackupManifest current)
        {
            var diff = new ManifestDiff();
            if (current?.entries == null || current.entries.Count == 0)
                return diff;

            var prevByRel = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
            var prevByGuid = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
            var consumedPrev = new HashSet<ManifestEntry>();

            if (previous?.entries != null)
            {
                foreach (var p in previous.entries)
                {
                    if (string.IsNullOrEmpty(p?.relPath)) continue;
                    if (!prevByRel.ContainsKey(p.relPath))
                        prevByRel[p.relPath] = p;
                    if (!string.IsNullOrEmpty(p.guid) && !prevByGuid.ContainsKey(p.guid))
                        prevByGuid[p.guid] = p;
                }
            }

            foreach (var entry in current.entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.relPath))
                    continue;

                if (prevByRel.TryGetValue(entry.relPath, out var prevExact))
                {
                    consumedPrev.Add(prevExact);
                    if (HasChanged(entry, prevExact))
                        diff.Modified.Add(entry);
                    continue;
                }

                if (!string.IsNullOrEmpty(entry.guid) && prevByGuid.TryGetValue(entry.guid, out var prevGuid) && !consumedPrev.Contains(prevGuid))
                {
                    consumedPrev.Add(prevGuid);
                    diff.Modified.Add(entry);
                    if (!string.Equals(prevGuid.relPath, entry.relPath, StringComparison.OrdinalIgnoreCase))
                        diff.Removed.Add(prevGuid);
                    continue;
                }

                diff.Added.Add(entry);
            }

            if (previous?.entries != null)
            {
                foreach (var prev in previous.entries)
                {
                    if (prev == null || string.IsNullOrEmpty(prev.relPath)) continue;
                    if (consumedPrev.Contains(prev)) continue;
                    diff.Removed.Add(prev);
                }
            }

            return diff;
        }

        private static bool HasChanged(ManifestEntry current, ManifestEntry previous)
        {
            if (current == null || previous == null) return true;
            if (!string.IsNullOrEmpty(current.md5) && !string.IsNullOrEmpty(previous.md5))
                return !string.Equals(current.md5, previous.md5, StringComparison.OrdinalIgnoreCase);
            if (current.size != previous.size)
                return true;
            if (current.lastWriteUtcTicks != previous.lastWriteUtcTicks)
                return true;
            if (!string.Equals(current.guid, previous.guid, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static VersionDeltaMetadata BuildDeltaMetadata(ManifestDiff diff, int versionId, bool isCheckpoint, int parentId, int checkpointId)
        {
            var metadata = new VersionDeltaMetadata
            {
                versionId = versionId,
                isCheckpoint = isCheckpoint,
                parentVersionId = parentId,
                checkpointVersionId = checkpointId
            };

            long changedBytes = 0;
            var categoryMap = new Dictionary<string, (int count, long bytes)>(StringComparer.OrdinalIgnoreCase);

            void AddChange(ManifestEntry entry, bool isNew)
            {
                if (entry == null || string.IsNullOrEmpty(entry.relPath)) return;
                string category = Categorize(entry.relPath);
                var deltaEntry = new VersionDeltaEntry
                {
                    relPath = entry.relPath,
                    size = Math.Max(0, entry.size),
                    hash = entry.md5,
                    category = category,
                    isNew = isNew
                };
                metadata.changedEntries.Add(deltaEntry);
                changedBytes += Math.Max(0, entry.size);

                if (!categoryMap.TryGetValue(category, out var agg))
                    categoryMap[category] = (1, Math.Max(0, entry.size));
                else
                    categoryMap[category] = (agg.count + 1, agg.bytes + Math.Max(0, entry.size));
            }

            foreach (var entry in diff.Added) AddChange(entry, true);
            foreach (var entry in diff.Modified) AddChange(entry, false);

            long removedBytes = 0;
            foreach (var removed in diff.Removed)
            {
                if (removed == null || string.IsNullOrEmpty(removed.relPath)) continue;
                metadata.removedEntries.Add(removed.relPath);
                removedBytes += Math.Max(0, removed.size);
            }

            metadata.changedBytes = changedBytes;
            metadata.removedBytes = removedBytes;
            metadata.changedFileCount = metadata.changedEntries.Count;
            metadata.removedFileCount = metadata.removedEntries.Count;

            foreach (var pair in categoryMap)
            {
                metadata.categoryBreakdown.Add(new VersionCategoryStat(pair.Key, pair.Value.count, pair.Value.bytes));
            }

            metadata.categoryBreakdown.Sort((a, b) => string.CompareOrdinal(a.category, b.category));
            return metadata;
        }

        private static string Categorize(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return "Other";
            string ext = Path.GetExtension(relPath).ToLowerInvariant();
            return ext switch
            {
                ".controller" => "Controller",
                ".anim" => "Anim",
                ".animator" => "Anim",
                ".playable" => "Controller",
                ".mat" => "Material",
                ".shader" => "Shader",
                ".prefab" => "Prefab",
                ".unity" => "Scene",
                ".asset" => relPath.IndexOf("VRCExpression", StringComparison.OrdinalIgnoreCase) >= 0 ? "VRC Assets" : "Asset",
                _ => "Other"
            };
        }

        public List<VersionInfo> GetVersions()
        {
            var index = LoadIndex();
            if (MigrateIndexIfNeeded(index)) SaveIndex(index);

            bool changed = false;
            foreach (var v in index.versions)
            {
                if (v.categoryStats == null)
                {
                    v.categoryStats = new List<VersionCategoryStat>();
                    changed = true;
                }
                if (!v.hasAdvancedMetadata)
                {
                    v.isCheckpoint = true;
                    v.parentId = 0;
                    v.checkpointId = v.id;
                    v.incrementalDepth = 0;
                    v.changedFileCount = v.fileCount;
                    v.removedFileCount = 0;
                    v.changedBytes = v.totalSizeBytes;
                    v.removedBytes = 0;
                    v.hasAdvancedMetadata = true;
                    changed = true;
                }

                try
                {
                    var manifest = LoadManifestForVersion(v.id);
                    if (manifest?.entries != null && manifest.entries.Count > 0)
                    {
                        long size = manifest.entries.Sum(e => Math.Max(0, e.size));
                        int count = manifest.entries.Count;
                        if (v.totalSizeBytes != size || v.fileCount != count)
                        {
                            v.totalSizeBytes = size;
                            v.fileCount = count;
                            changed = true;
                        }
                    }
                }
                catch
                {
                    v.corrupt = true;
                    changed = true;
                }
            }

            if (changed) SaveIndex(index);

            DateTime ParseCreated(VersionInfo vi)
            {
                if (!string.IsNullOrEmpty(vi.createdUtc) && DateTime.TryParse(vi.createdUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var c))
                    return c;
                return vi.timestamp;
            }

            return index.versions.OrderByDescending(v => ParseCreated(v)).ToList();
        }

        public VersionInfo GetVersion(int id)
        {
            var index = LoadIndex();
            return index.versions.FirstOrDefault(v => v.id == id);
        }

        public void CleanupOldVersions(int keepCount = 10)
        {
            try
            {
                var index = LoadIndex();
                if (index.versions.Count <= keepCount) return;

                var ordered = index.versions
                    .OrderByDescending(v => v.timestamp)
                    .ThenByDescending(v => v.id)
                    .Take(keepCount)
                    .ToList();

                var keepIds = new HashSet<int>(ordered.Select(v => v.id));
                bool added;
                do
                {
                    added = false;
                    foreach (var v in index.versions)
                    {
                        if (!keepIds.Contains(v.id)) continue;
                        if (!v.isCheckpoint)
                        {
                            if (v.parentId > 0 && keepIds.Add(v.parentId)) added = true;
                            if (v.checkpointId > 0 && keepIds.Add(v.checkpointId)) added = true;
                        }
                    }
                } while (added);

                var versionsToKeep = index.versions.Where(v => keepIds.Contains(v.id)).ToList();
                var versionsToDelete = index.versions.Where(v => !keepIds.Contains(v.id)).ToList();

                foreach (var version in versionsToDelete)
                {
                    string versionDir = Path.Combine(_versionsRoot, $"v{version.id:D3}");
                    if (Directory.Exists(versionDir))
                    {
                        try { Directory.Delete(versionDir, true); }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[ASB] Failed to delete version directory {versionDir}: {ex.Message}");
                        }
                    }
                }

                index.versions = versionsToKeep;
                SaveIndex(index);

                if (versionsToDelete.Count > 0)
                    Debug.Log($"[ASB] Cleaned up {versionsToDelete.Count} old versions");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Failed to cleanup old versions: {ex.Message}");
            }
        }

        public int GetVersionCount()
        {
            var index = LoadIndex();
            return index.versions.Count;
        }

        public VersionIndexRebuildReport RebuildIndex(bool applyChanges)
        {
            var report = new VersionIndexRebuildReport();
            var index = LoadIndex();
            foreach (var version in index.versions)
            {
                try
                {
                    var manifest = LoadManifestForVersion(version.id);
                    if (manifest?.entries == null || manifest.entries.Count == 0)
                    {
                        report.issues.Add(new VersionIndexIssueEntry { versionId = version.id, message = "Manifest mancante o vuoto." });
                        continue;
                    }
                    int newCount = manifest.entries.Count;
                    long newSize = manifest.entries.Sum(e => Math.Max(0, e.size));
                    if (version.fileCount != newCount || version.totalSizeBytes != newSize)
                    {
                        report.changes.Add(new VersionIndexRebuildEntry
                        {
                            versionId = version.id,
                            oldCount = version.fileCount,
                            newCount = newCount,
                            oldSize = version.totalSizeBytes,
                            newSize = newSize
                        });
                        if (applyChanges)
                        {
                            version.fileCount = newCount;
                            version.totalSizeBytes = newSize;
                            version.corrupt = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    report.issues.Add(new VersionIndexIssueEntry { versionId = version.id, message = ex.Message });
                }
            }
            if (applyChanges && report.HasChanges)
            {
                SaveIndex(index);
            }
            return report;
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

        public bool DeleteVersion(int id)
        {
            try
            {
                var index = LoadIndex();
                var v = index.versions.FirstOrDefault(x => x.id == id);
                if (v == null) return false;
                if (v.pinned)
                {
                    Debug.LogWarning($"[ASB] Refusing to delete pinned version #{id}");
                    return false;
                }

                bool hasDependents = index.versions.Any(other => other.parentId == id || (other.checkpointId == id && other.id != id));
                if (hasDependents)
                {
                    Debug.LogWarning($"[ASB] Cannot delete version #{id} because other versions depend on it.");
                    return false;
                }

                index.versions.Remove(v);
                SaveIndex(index);

                string versionDir = Path.Combine(_versionsRoot, $"v{id:D3}");
                try
                {
                    if (Directory.Exists(versionDir)) Directory.Delete(versionDir, true);
                }
                catch (Exception exDir)
                {
                    Debug.LogWarning($"[ASB] Deleted index entry but failed to remove directory {versionDir}: {exDir.Message}");
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ASB] Failed to delete version {id}: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }

        private static string MakeRelative(string file, string root)
        {
            var ru = new Uri(Path.GetFullPath(root) + Path.DirectorySeparatorChar);
            var fu = new Uri(Path.GetFullPath(file));
            return Uri.UnescapeDataString(ru.MakeRelativeUri(fu).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private sealed class ManifestDiff
        {
            public List<ManifestEntry> Added { get; } = new List<ManifestEntry>();
            public List<ManifestEntry> Modified { get; } = new List<ManifestEntry>();
            public List<ManifestEntry> Removed { get; } = new List<ManifestEntry>();
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
        public string createdUtc;
        public long totalSizeBytes;
        public int fileCount;
        public bool pinned;
        public string manifestFile;
        public bool incomplete;
        public int toolVersion;
        public bool corrupt;
        public bool isCheckpoint;
        public int parentId;
        public int checkpointId;
        public int incrementalDepth;
        public int changedFileCount;
        public int removedFileCount;
        public long changedBytes;
        public long removedBytes;
        public string deltaFile;
        public bool hasAdvancedMetadata;
        public List<VersionCategoryStat> categoryStats = new List<VersionCategoryStat>();
    }

    [Serializable]
    public class VersionIndex
    {
        public static int CurrentSchemaVersion = 4;
        public int schemaVersion;
        public List<VersionInfo> versions;
    }
}
#endif


