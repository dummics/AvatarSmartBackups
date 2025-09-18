#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AvatarSmartBackup
{
    internal static class VersionRestoreService
    {
        static readonly object RebuildLock = new object();
        static string _lastWarning;

        public static string LastWarning => _lastWarning;

        static string RebuildRoot => Path.Combine(FileUtilEx.ProjectRoot, "Temp", "ASB_Rebuilds");

        public static string PrepareSnapshot(int versionId, bool forceRebuild = false)
        {
            _lastWarning = null;
            using var vm = new FileBasedVersionManager();
            var version = vm.GetVersion(versionId);
            if (version == null)
                throw new InvalidOperationException($"Version #{versionId} not found.");

            if (version.isCheckpoint)
            {
                string checkpointDir = Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{version.id:D3}");
                if (!Directory.Exists(checkpointDir))
                    throw new InvalidOperationException($"Checkpoint directory missing: {checkpointDir}");
                return checkpointDir;
            }

            lock (RebuildLock)
            {
                string rebuildDir = Path.Combine(RebuildRoot, $"v{version.id:D3}");

                if (forceRebuild && Directory.Exists(rebuildDir))
                {
                    try { Directory.Delete(rebuildDir, true); } catch (Exception ex) { Debug.LogWarning($"[ASB] Failed to clear rebuild dir: {ex.Message}"); }
                }

                if (!forceRebuild && Directory.Exists(rebuildDir))
                    return rebuildDir;

                Directory.CreateDirectory(RebuildRoot);
                BuildSnapshot(vm, version, rebuildDir);
                return rebuildDir;
            }
        }

        public static VersionDeltaMetadata LoadDeltaMetadata(VersionInfo info)
        {
            if (info == null) return null;
            string versionDir = Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{info.id:D3}");
            return LoadDeltaMetadata(versionDir);
        }

        static void BuildSnapshot(FileBasedVersionManager vm, VersionInfo target, string rebuildDir)
        {
            var chain = BuildChain(vm, target);
            if (chain.Count == 0)
                throw new InvalidOperationException("Version chain is empty.");

            var checkpoint = chain[0];
            if (!checkpoint.isCheckpoint)
                throw new InvalidOperationException($"Version chain for #{target.id} does not start with a checkpoint.");

            string checkpointDir = Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{checkpoint.id:D3}");
            if (!Directory.Exists(checkpointDir))
                throw new InvalidOperationException($"Checkpoint directory missing: {checkpointDir}");

            CopyDirectory(checkpointDir, rebuildDir);

            for (int i = 1; i < chain.Count; i++)
            {
                var current = chain[i];
                string versionDir = Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{current.id:D3}");
                string deltaRoot = Path.Combine(versionDir, "delta");
                string deltaMetadataPath = Path.Combine(versionDir, "delta.json");
                if (!File.Exists(deltaMetadataPath))
                {
                    FailDeltaRebuild(vm, target, current, $"Delta metadata missing for version #{current.id}.");
                }
                var delta = LoadDeltaMetadata(versionDir);

                foreach (var entry in delta.changedEntries)
                {
                    string src = Path.Combine(deltaRoot, entry.relPath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(src))
                    {
                        FailDeltaRebuild(vm, target, current, $"Delta file missing for version #{current.id}: {entry.relPath}");
                    }
                    string dst = Path.Combine(rebuildDir, entry.relPath.Replace('/', Path.DirectorySeparatorChar));
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

                if (delta.removedEntries != null)
                {
                    foreach (var removed in delta.removedEntries)
                    {
                        if (string.IsNullOrEmpty(removed)) continue;
                        string dst = Path.Combine(rebuildDir, removed.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(dst)) File.Delete(dst);
                        string dstMeta = dst + ".meta";
                        if (File.Exists(dstMeta)) File.Delete(dstMeta);
                    }
                }
            }

            ValidateRebuiltSnapshot(vm, target, rebuildDir);
        }

        static List<VersionInfo> BuildChain(FileBasedVersionManager vm, VersionInfo target)
        {
            var chain = new List<VersionInfo>();
            var current = target;
            int guard = 0;
            while (current != null && guard++ < 256)
            {
                chain.Add(current);
                if (current.isCheckpoint || current.parentId <= 0)
                    break;
                current = vm.GetVersion(current.parentId);
            }
            chain.Reverse();
            return chain;
        }

        static VersionDeltaMetadata LoadDeltaMetadata(string versionDir)
        {
            string deltaPath = Path.Combine(versionDir, "delta.json");
            if (!File.Exists(deltaPath))
                return new VersionDeltaMetadata();
            try
            {
                var json = File.ReadAllText(deltaPath);
                var meta = JsonUtility.FromJson<VersionDeltaMetadata>(json) ?? new VersionDeltaMetadata();
                if (meta.changedEntries == null) meta.changedEntries = new List<VersionDeltaEntry>();
                if (meta.removedEntries == null) meta.removedEntries = new List<string>();
                if (meta.categoryBreakdown == null) meta.categoryBreakdown = new List<VersionCategoryStat>();
                return meta;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ASB] Failed to load delta metadata from {deltaPath}: {ex.Message}");
                return new VersionDeltaMetadata();
            }
        }

        static void CopyDirectory(string sourceDir, string destinationDir)
        {
            if (Directory.Exists(destinationDir))
            {
                try { Directory.Delete(destinationDir, true); }
                catch (Exception ex) { Debug.LogWarning($"[ASB] Could not reset rebuild dir {destinationDir}: {ex.Message}"); }
            }
            Directory.CreateDirectory(destinationDir);

            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                string rel = MakeRelative(dir, sourceDir);
                if (IsDeltaSegment(rel)) continue;
                Directory.CreateDirectory(Path.Combine(destinationDir, rel));
            }

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string rel = MakeRelative(file, sourceDir);
                if (IsDeltaSegment(rel)) continue;
                string fileName = Path.GetFileName(rel);
                if (string.Equals(fileName, "manifest_v2.json", StringComparison.OrdinalIgnoreCase)) continue;
                string dst = Path.Combine(destinationDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(file, dst, true);
            }
        }

        static bool IsDeltaSegment(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return false;
            var segments = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return false;
            return string.Equals(segments[0], "delta", StringComparison.OrdinalIgnoreCase);
        }

        static void FailDeltaRebuild(FileBasedVersionManager vm, VersionInfo target, VersionInfo failingVersion, string reason)
        {
            _lastWarning = reason;
            vm?.MarkVersionCorrupt(failingVersion.id, markIncomplete: true, reason: reason);
            if (target != null && target.id != failingVersion.id)
                vm?.MarkVersionCorrupt(target.id, markIncomplete: true, reason: reason);
            Log.Error(reason);
            Debug.LogError($"[ASB] {reason}");
            throw new InvalidOperationException(reason);
        }

        static void ValidateRebuiltSnapshot(FileBasedVersionManager vm, VersionInfo target, string rebuildDir)
        {
            if (target == null)
                return;

            string versionDir = Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{target.id:D3}");
            string manifestPath = Path.Combine(versionDir, "manifest.json");
            BackupManifest manifest = null;
            try
            {
                if (!File.Exists(manifestPath))
                    throw new FileNotFoundException("Manifest not found", manifestPath);
                string json = File.ReadAllText(manifestPath);
                manifest = JsonUtility.FromJson<BackupManifest>(json);
            }
            catch (Exception ex)
            {
                string reason = $"Manifest unavailable for version #{target.id}: {ex.Message}";
                vm?.MarkVersionCorrupt(target.id, markIncomplete: true, reason: reason);
                Log.Error(reason);
                Debug.LogError($"[ASB] {reason}");
                throw new InvalidOperationException(reason, ex);
            }

            if (manifest?.entries == null)
            {
                string reason = $"Manifest for version #{target.id} is empty or invalid.";
                vm?.MarkVersionCorrupt(target.id, markIncomplete: true, reason: reason);
                Log.Error(reason);
                Debug.LogError($"[ASB] {reason}");
                throw new InvalidOperationException(reason);
            }

            foreach (var entry in manifest.entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.relPath))
                    continue;

                string dst = Path.Combine(rebuildDir, entry.relPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(dst))
                {
                    string reason = $"Missing file {entry.relPath} while rebuilding version #{target.id}.";
                    vm?.MarkVersionCorrupt(target.id, markIncomplete: true, reason: reason);
                    Log.Error(reason);
                    Debug.LogError($"[ASB] {reason}");
                    throw new InvalidOperationException(reason);
                }

                if (string.IsNullOrEmpty(entry.md5))
                    continue;

                try
                {
                    string computed = FileUtilEx.MD5Of(dst);
                    if (!string.Equals(computed, entry.md5, StringComparison.OrdinalIgnoreCase))
                    {
                        string reason = $"Hash mismatch for {entry.relPath} while rebuilding version #{target.id}.";
                        vm?.MarkVersionCorrupt(target.id, markIncomplete: true, reason: reason);
                        Log.Error(reason);
                        Debug.LogError($"[ASB] {reason}");
                        throw new InvalidOperationException(reason);
                    }
                }
                catch (Exception ex)
                {
                    string reason = $"Failed to verify hash for {entry.relPath} in version #{target.id}: {ex.Message}";
                    vm?.MarkVersionCorrupt(target.id, markIncomplete: true, reason: reason);
                    Log.Error(reason);
                    Debug.LogError($"[ASB] {reason}");
                    throw new InvalidOperationException(reason, ex);
                }
            }
        }

        static string MakeRelative(string path, string root)
        {
            var ru = new Uri(Path.GetFullPath(root) + Path.DirectorySeparatorChar);
            var pu = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(ru.MakeRelativeUri(pu).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
#endif
