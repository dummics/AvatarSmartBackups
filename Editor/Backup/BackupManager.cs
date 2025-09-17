#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AvatarSmartBackup
{
    internal static class BackupManager
    {

        const string ProjectSettingsRel = "ProjectSettings/AvatarBackupSettings.json";
        const string EditorPrefsKey_UseProject = "ASB/UseProjectSettings";

        static string CurrentDir => Path.Combine(FileUtilEx.BackupRoot, "Current");
        static string ArchiveDir => Path.Combine(FileUtilEx.BackupRoot, "Archive");
        static string ManifestPath => Path.Combine(CurrentDir, "manifest.json");
        static string ManifestV2Path => Path.Combine(CurrentDir, "manifest_v2.json");

        static string GlobalSettingsPath
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(root))
                    root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(root, "AvatarSmartBackup/GlobalSettings.json");
            }
        }
        static string ProjectSettingsPath => Path.Combine(FileUtilEx.ProjectRoot, ProjectSettingsRel);

        public static bool UseProjectSettings
        {
            get => EditorPrefs.GetBool(EditorPrefsKey_UseProject, false);
            set => EditorPrefs.SetBool(EditorPrefsKey_UseProject, value);
        }

        public static BackupSettings LoadSettings()
        {
            BackupSettings s;
            if (UseProjectSettings && File.Exists(ProjectSettingsPath))
                s = JsonUtility.FromJson<BackupSettings>(File.ReadAllText(ProjectSettingsPath, Encoding.UTF8));
            else if (File.Exists(GlobalSettingsPath))
                s = JsonUtility.FromJson<BackupSettings>(File.ReadAllText(GlobalSettingsPath, Encoding.UTF8));
            else s = new BackupSettings();
            s.useProjectSettings = UseProjectSettings;
            if (s.debugMode && !s.advancedMode) s.advancedMode = true;
            if (!s.advancedMode) s.debugMode = false;
            if (s.idleDelaySeconds <= 0) s.idleDelaySeconds = 10;
            if (s.lastMeasuredMBps < 0f) s.lastMeasuredMBps = 0f;
            if (s.lastBackupBytes < 0) s.lastBackupBytes = 0;

            // Sync dropdown presets with stored numeric limits (for backward compatibility)
            long[] presetVals = new long[] { 256, 512, 1024, 2048, 4096 };
            int CustomIdx = 5;
            int idx = Array.IndexOf(presetVals, Math.Max(1, (int)s.materialsMaxKB));
            s.materialsSizePresetIndex = idx >= 0 ? idx : CustomIdx;
            idx = Array.IndexOf(presetVals, Math.Max(1, (int)s.dllsMaxKB));
            s.dllSizePresetIndex = idx >= 0 ? idx : CustomIdx;
            return s;
        }
        public static void SaveSettings(BackupSettings s)
        {
            try
            {
                UseProjectSettings = s.useProjectSettings;
                if (!s.advancedMode) s.debugMode = false;
                string p = s.useProjectSettings ? ProjectSettingsPath : GlobalSettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, JsonUtility.ToJson(s, true), Encoding.UTF8);
            }
            catch (Exception ex) 
            { 
                Log.Error($"Failed to save settings to {(s.useProjectSettings ? "project" : "global")} location: {ex.Message}", 
                         "Settings could not be saved", ex); 
            }
        }

        public static bool TryUpdateTrackedSelection(IEnumerable<string> entries, bool lockSelection, out string error)
        {
            try
            {
                var settings = LoadSettings();
                var normalized = SelectionFilter.NormalizeForStorage(entries);
                if (normalized.Any(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
                {
                    error = "Script files (.cs) are excluded from backups.";
                    return false;
                }
                bool hasVersions = HasExistingVersions();
                bool frozen = settings.selectionLocked || hasVersions;

                if (frozen)
                {
                    var existing = new HashSet<string>(settings.trackedRoots ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                    foreach (var candidate in normalized)
                    {
                        if (!existing.Contains(candidate))
                        {
                            error = "Cannot add new tracked paths after versions have been created.";
                            return false;
                        }
                    }
                }

                settings.trackedRoots = normalized;
                if (lockSelection || hasVersions)
                    settings.selectionLocked = true;

                SaveSettings(settings);
                TimerService.InvalidateSettingsCache();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool IsSelectionFrozen(BackupSettings settings)
        {
            if (settings == null) return false;
            if (settings.selectionLocked) return true;
            return HasExistingVersions();
        }

        static bool HasExistingVersions()
        {
            try
            {
                using var vm = new FileBasedVersionManager();
                return vm.GetVersionCount() > 0;
            }
            catch
            {
                return false;
            }
        }

        static BackupManifest LoadManifest()
        {
            try
            {
                if (!File.Exists(ManifestPath)) return null;
                return JsonUtility.FromJson<BackupManifest>(File.ReadAllText(ManifestPath, Encoding.UTF8));
            }
            catch { return null; }
        }
        static void WriteManifest(BackupManifest m)
        {
            string tmp = ManifestPath + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(m, true), Encoding.UTF8);
            FileUtilEx.AtomicReplace(tmp, ManifestPath);
        }

        static VersionManifestV2 LoadManifestV2()
        {
            try
            {
                if (!File.Exists(ManifestV2Path)) return null;
                return JsonUtility.FromJson<VersionManifestV2>(File.ReadAllText(ManifestV2Path, Encoding.UTF8));
            }
            catch { return null; }
        }

        static void WriteManifestV2(VersionManifestV2 manifest)
        {
            if (manifest == null) return;
            string tmp = ManifestV2Path + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(manifest, true), Encoding.UTF8);
            FileUtilEx.AtomicReplace(tmp, ManifestV2Path);
        }

        static void SaveOpenScenesIfDirty()
        {
            bool anyDirty = false;
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scn = EditorSceneManager.GetSceneAt(i);
                if (scn.isDirty) { anyDirty = true; break; }
            }
            if (anyDirty) EditorSceneManager.SaveOpenScenes();
        }

        sealed class FilePlanItem
        {
            public FileScanResult Scan;
            public ManifestEntry ManifestEntry;
            public VersionManifestEntryV2 ManifestEntryV2;
            public string DestinationPath;
            public bool NeedsCopy;
        }

        struct CopyPlan
        {
            public List<(string src, string dst)> CopyJobs;
            public BackupManifest Manifest;
            public VersionManifestV2 ManifestV2;
            public List<FilePlanItem> Files;
            public long TotalBytes;
            public int CopiedCount;
            public int SkippedCount;
        }

        static CopyPlan BuildCopyPlan(BackupSettings s, int progId)
        {
            var copyJobs = new List<(string src, string dst)>();
            var files = new List<FilePlanItem>();
            long totalBytes = 0;
            int copied = 0;
            int skipped = 0;

            var inc = IncrementalCollector.ConsumeChanges()?.ToArray() ?? Array.Empty<string>();
            bool haveIncremental = inc.Length > 0;
            if (haveIncremental && s?.DiagnosticsEnabled == true)
            {
                Log.Info($"IncrementalCollector: {inc.Length} changes consumed");
            }

            Directory.CreateDirectory(CurrentDir);
            Directory.CreateDirectory(ArchiveDir);

            var prev = LoadManifest();
            var prevV2 = LoadManifestV2();

            Dictionary<string, ManifestEntry> prevByGuid = null;
            Dictionary<string, ManifestEntry> prevByRel = null;
            if (prev?.entries != null && prev.entries.Count > 0)
            {
                prevByGuid = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
                prevByRel = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in prev.entries)
                {
                    if (!string.IsNullOrEmpty(entry.relPath) && !prevByRel.ContainsKey(entry.relPath))
                        prevByRel[entry.relPath] = entry;
                    if (!string.IsNullOrEmpty(entry.guid) && !prevByGuid.ContainsKey(entry.guid))
                        prevByGuid[entry.guid] = entry;
                }
            }

            Dictionary<string, VersionManifestEntryV2> prevV2ByRel = null;
            if (prevV2?.entries != null && prevV2.entries.Count > 0)
            {
                prevV2ByRel = new Dictionary<string, VersionManifestEntryV2>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in prevV2.entries)
                {
                    if (!string.IsNullOrEmpty(entry.relPath) && !prevV2ByRel.ContainsKey(entry.relPath))
                        prevV2ByRel[entry.relPath] = entry;
                }
            }

            var scanner = new FileScanner();
            IReadOnlyList<FileScanResult> scanResults;
            try
            {
                scanResults = scanner.Scan(s, haveIncremental && prev != null ? inc : null);
            }
            catch (Exception ex)
            {
                Log.Warn($"FileScanner failed ({ex.Message}), falling back to full scan.");
                scanResults = scanner.Scan(s, null);
            }

            if (haveIncremental && prev == null)
                haveIncremental = false;

            if (s?.DiagnosticsEnabled == true)
            {
                Log.Info($"BuildCopyPlan: scanner produced {scanResults.Count} entries");
            }

            var selectionRules = SelectionFilter.BuildRules(s);

            var manifest = new BackupManifest
            {
                projectName = FileUtilEx.ProjectName,
                unityVersion = Application.unityVersion,
                createdUtc = DateTime.UtcNow.ToString("o"),
                entries = new List<ManifestEntry>(scanResults.Count + (prev?.entries?.Count ?? 0))
            };

            var manifestV2 = new VersionManifestV2
            {
                createdUtc = manifest.createdUtc,
                unityVersion = manifest.unityVersion,
                checkpoint = true
            };
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int i = 0;
            foreach (var scan in scanResults)
            {
                i++;
                string rel = scan.AssetPath;
                if (!SelectionFilter.Allows(selectionRules, rel))
                    continue;
                string abs = scan.AbsolutePath;
                string guid = FileUtilEx.TryReadGuidFromMeta(abs) ?? string.Empty;

                var entry = new ManifestEntry
                {
                    guid = guid,
                    relPath = rel,
                    size = scan.Size,
                    lastWriteUtcTicks = scan.LastWriteUtcTicks,
                    md5 = null
                };

                ManifestEntry prevEntry = null;
                if (!string.IsNullOrEmpty(guid) && prevByGuid != null)
                    prevByGuid.TryGetValue(guid, out prevEntry);
                if (prevEntry == null && prevByRel != null)
                    prevByRel.TryGetValue(rel, out prevEntry);

                string prevRelPath = prevEntry?.relPath;
                if (prevEntry != null && prevEntry.size == scan.Size && prevEntry.lastWriteUtcTicks == scan.LastWriteUtcTicks)
                {
                    entry.md5 = prevEntry.md5;
                }

                if (prevEntry != null && !string.IsNullOrEmpty(prevEntry.relPath))
                {
                    if (SelectionFilter.Allows(selectionRules, prevEntry.relPath) && !FileScanner.IsDisallowedExtension(Path.GetExtension(prevEntry.relPath)))
                        added.Add(prevEntry.relPath);
                }

                bool needsCopy = string.IsNullOrEmpty(entry.md5);
                string destination = Path.Combine(CurrentDir, rel);
                if (!needsCopy && prevEntry != null && !string.Equals(prevEntry.relPath, rel, StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryMoveCachedFile(prevEntry.relPath, rel))
                    {
                        needsCopy = true;
                    }
                }

                if (needsCopy)
                {
                    copyJobs.Add((abs, destination));
                    copied++;
                }
                else
                {
                    skipped++;
                }

                totalBytes += scan.Size;
                manifest.entries.Add(entry);
                added.Add(rel);

                var entryV2 = new VersionManifestEntryV2
                {
                    relPath = rel,
                    size = scan.Size,
                    ticks = scan.LastWriteUtcTicks,
                    legacyMd5 = entry.md5
                };

                VersionManifestEntryV2 prevV2Entry = null;
                if (prevV2ByRel != null)
                {
                    if (!prevV2ByRel.TryGetValue(rel, out prevV2Entry) && !string.IsNullOrEmpty(prevRelPath))
                        prevV2ByRel.TryGetValue(prevRelPath, out prevV2Entry);
                }

                bool shouldHash = needsCopy;
                if (!needsCopy && prevV2Entry != null)
                {
                    entryV2.hash = prevV2Entry.hash;
                    entryV2.metaHash = prevV2Entry.metaHash;
                    if (string.IsNullOrEmpty(entryV2.legacyMd5) && !string.IsNullOrEmpty(prevV2Entry.legacyMd5))
                        entryV2.legacyMd5 = prevV2Entry.legacyMd5;
                }
                else
                {
                    shouldHash = true;
                }

                manifestV2.entries.Add(entryV2);

                if (shouldHash)
                {
                    files.Add(new FilePlanItem
                    {
                        Scan = scan,
                        ManifestEntry = entry,
                        ManifestEntryV2 = entryV2,
                        DestinationPath = destination,
                        NeedsCopy = needsCopy
                    });
                }

                string metaSrc = abs + ".meta";
                if (File.Exists(metaSrc))
                {
                    string metaDst = destination + ".meta";
                    copyJobs.Add((metaSrc, metaDst));
                }

                if (progId >= 0 && scanResults.Count > 0 && i % 20 == 0)
                    ProgressUX.Report(progId, (float)i / scanResults.Count, $"Planning {i}/{scanResults.Count}");
            }

            if (prev?.entries != null && prev.entries.Count > 0)
            {
                foreach (var prevEntry in prev.entries)
                {
                    if (string.IsNullOrEmpty(prevEntry.relPath)) continue;
                    if (FileScanner.IsDisallowedExtension(Path.GetExtension(prevEntry.relPath))) continue;
                    if (!SelectionFilter.Allows(selectionRules, prevEntry.relPath)) continue;
                    if (!added.Add(prevEntry.relPath)) continue;

                    var mergedEntry = new ManifestEntry
                    {
                        guid = prevEntry.guid,
                        relPath = prevEntry.relPath,
                        size = prevEntry.size,
                        lastWriteUtcTicks = prevEntry.lastWriteUtcTicks,
                        md5 = prevEntry.md5
                    };
                    manifest.entries.Add(mergedEntry);
                    totalBytes += mergedEntry.size;

                    var mergedV2 = new VersionManifestEntryV2
                    {
                        relPath = prevEntry.relPath,
                        size = prevEntry.size,
                        ticks = prevEntry.lastWriteUtcTicks,
                        legacyMd5 = prevEntry.md5
                    };

                    if (prevV2ByRel != null && prevV2ByRel.TryGetValue(prevEntry.relPath, out var prevV2Entry))
                    {
                        mergedV2.hash = prevV2Entry.hash;
                        mergedV2.metaHash = prevV2Entry.metaHash;
                        if (string.IsNullOrEmpty(mergedV2.legacyMd5) && !string.IsNullOrEmpty(prevV2Entry.legacyMd5))
                            mergedV2.legacyMd5 = prevV2Entry.legacyMd5;
                    }
                    else
                    {
                        string absPrev = FileUtilEx.AssetToAbs(prevEntry.relPath);
                        if (File.Exists(absPrev))
                        {
                            var scan = new FileScanResult(prevEntry.relPath, absPrev, prevEntry.size, prevEntry.lastWriteUtcTicks, File.Exists(absPrev + ".meta"));
                            files.Add(new FilePlanItem
                            {
                                Scan = scan,
                                ManifestEntry = mergedEntry,
                                ManifestEntryV2 = mergedV2,
                                DestinationPath = Path.Combine(CurrentDir, prevEntry.relPath),
                                NeedsCopy = false
                            });
                        }
                    }

                    manifestV2.entries.Add(mergedV2);
                }
            }

            if (s?.DiagnosticsEnabled == true)
            {
                Log.Info($"BuildCopyPlan: planned copyJobs={copyJobs.Count} copied={copied} skipped={skipped} totalEntries={manifest.entries.Count} totalBytes={totalBytes}");
            }

            return new CopyPlan
            {
                CopyJobs = copyJobs,
                Manifest = manifest,
                ManifestV2 = manifestV2,
                Files = files,
                TotalBytes = totalBytes,
                CopiedCount = copied,
                SkippedCount = skipped
            };
        }

        static bool TryMoveCachedFile(string previousRelPath, string newRelPath)
        {
            try
            {
                string oldAbs = Path.Combine(CurrentDir, previousRelPath);
                if (!File.Exists(oldAbs)) return false;
                string newAbs = Path.Combine(CurrentDir, newRelPath);
                Directory.CreateDirectory(Path.GetDirectoryName(newAbs));
                if (File.Exists(newAbs)) File.Delete(newAbs);
                File.Move(oldAbs, newAbs);

                string oldMeta = oldAbs + ".meta";
                if (File.Exists(oldMeta))
                {
                    string newMeta = newAbs + ".meta";
                    Directory.CreateDirectory(Path.GetDirectoryName(newMeta));
                    if (File.Exists(newMeta)) File.Delete(newMeta);
                    File.Move(oldMeta, newMeta);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Move-in-cache failed: {previousRelPath} -> {newRelPath} - {ex.Message}. Falling back to copy.");
                return false;
            }
        }

        // Anti re-entrancy guards
        static readonly SemaphoreSlim _one = new SemaphoreSlim(1, 1);
        static int _busy;
        static int _pendingRun;
        static int _benchBusy;
        static long _lastManualBenchmarkTicks = 0; // Anti-spam for manual benchmark
    static long _lastManualSnapshotTicks = 0; // Anti-spam for manual snapshot

        // Attempt to claim a manual snapshot slot. Returns true if allowed (and marks the cooldown), false if cooldown active.
        internal static bool TryClaimManualSnapshot(TimeSpan cooldown)
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastManualSnapshotTicks);
            if (now - last < cooldown.Ticks) return false;
            Interlocked.Exchange(ref _lastManualSnapshotTicks, now);
            return true;
        }
        internal static bool IsBusy => _busy == 1;
        internal static void QueuePendingRun() => Interlocked.Exchange(ref _pendingRun, 1);


        // API pubblica
        public static void RunBackupNow(BackupSettings s, bool showToast = true, string reason = null, bool showProgressUI = true, bool forceZip = false)
            => _ = RunBackupNowAsync(s, showToast, reason, showProgressUI, forceZip);

        public static async Task RunBackupNowAsync(BackupSettings s, bool showToast, string reason, bool showProgressUI, bool forceZip)
        {
            try
            {
                if (Interlocked.Exchange(ref _busy, 1) == 1)
                {
                    Log.Info("Backup already running - queued another run.");
                    Interlocked.Exchange(ref _pendingRun, 1);
                    return;
                }

                // Anti-spam protection: prevent backup runs closer than 3 seconds apart unless forced
                if (reason != "timer" && !forceZip)
                {
                    var lastBackup = Session.LastBackupUtc;
                    if (lastBackup.HasValue && (DateTime.UtcNow - lastBackup.Value).TotalSeconds < s.minManualBackupIntervalSeconds)
                    {
                        Log.Warn($"Backup anti-spam: ignoring {reason} request (last backup {(DateTime.UtcNow - lastBackup.Value).TotalSeconds:0.1}s ago)");
                        Interlocked.Exchange(ref _busy, 0);
                        return;
                    }
                }

                await _one.WaitAsync();

                await EnsureBenchmarkAsync(s);
                int copyCap = EffectiveCopyMBps(s);
                int zipCap = EffectiveZipMBps(s);
                if (copyCap > 0) Log.Info($"Copy throttle: {copyCap} MB/s{(s.autoThrottle ? " (auto)" : string.Empty)}");
                if (zipCap > 0) Log.Info($"Zip throttle: {zipCap} MB/s{(s.autoThrottle ? " (auto)" : string.Empty)}");
                double intervalSec = s.intervalInSeconds ? s.intervalMinutes : s.intervalMinutes * 60;
                if (!s.intervalInSeconds && s.intervalMinutes < 5)
                    Log.Warn("Backup interval under 5 minutes may affect editor performance.");
                if (s.lastBackupBytes > 0 && copyCap > 0)
                {
                    double secNeeded = s.lastBackupBytes / (copyCap * 1024.0 * 1024.0);
                    if (secNeeded > intervalSec)
                    {
                        string unit = s.intervalInSeconds ? "sec" : "min";
                        Log.Warn($"Estimated throughput may not finish backup ({HumanMB(s.lastBackupBytes)}) within {s.intervalMinutes} {unit}.");
                    }
                }

                if (s.saveScenesBeforeBackup) SaveOpenScenesIfDirty();

                // 1) Scan e costruzione job (background)
                // Clone settings for the scan to avoid races if the user edits UI while scanning.
                var scanSettings = JsonUtility.FromJson<BackupSettings>(JsonUtility.ToJson(s));
                int scanId = showProgressUI ? ProgressUX.Start("Avatar Smart Backup", "Scanning project", false) : -1;
                var plan = await Task.Run(() => BuildCopyPlan(scanSettings, scanId));
                ProgressUX.Finish(scanId);
                var copyJobs = plan.CopyJobs;
                var man = plan.Manifest;
                var manV2 = plan.ManifestV2;
                var plannedFiles = plan.Files;
                long totalBytes = plan.TotalBytes;
                int copied = plan.CopiedCount;
                int skipped = plan.SkippedCount;

                // 2) Copie (background, non modale, throttled)
                if (copyJobs.Count > 0)
                {
                    using var cts = new CancellationTokenSource();
                    int progId = showProgressUI ? ProgressUX.Start("Avatar Smart Backup", "Copying files", cancellable: true, onCancel: () => { cts.Cancel(); return true; }) : -1;
                    try
                    {
                        int done = 0, total = copyJobs.Count;
                        var throttler = new SemaphoreSlim(Math.Max(1, s.maxParallelThreads));
                        var tasks = new List<Task>(total);

                        foreach (var job in copyJobs)
                        {
                            tasks.Add(Task.Run(async () =>
                            {
                                await throttler.WaitAsync(cts.Token);
                                Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal;
                                try
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(job.dst));
                                    // Throttled copy
                                    using var src = new FileStream(job.src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                    string tmp = job.dst + ".tmp";
                                    using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                                        IOThrottle.CopyStreamThrottled(src, dst, bufferBytes: 2 * 1024 * 1024, maxMBps: copyCap, ct: cts.Token);
                                    FileUtilEx.AtomicReplace(tmp, job.dst);
                                }
                                catch (Exception ex)
                                {
                                    Log.Warn($"Copy failed: {job.src} - {ex.Message}");
                                }
}
                                finally
                                {
                                    throttler.Release();
                                    int d = Interlocked.Increment(ref done);
                                    // Throttle progress updates to max 10/sec for better UI performance
                                    if (total > 0 && (d % Math.Max(1, total / 50) == 0 || d == total))
                                        ProgressUX.Report(progId, (float)d / total, $"Copying files {d}/{total}");
                                }
                            }, cts.Token));
                        }

                        await Task.WhenAll(tasks);
                    }
                    finally
                    {
                        ProgressUX.Finish(progId);
                    }
                }

                // 3) Hashing + CAS population (background)
                if (plannedFiles != null && plannedFiles.Count > 0)
                {
                    var store = new ContentAddressableStore();
                    var throttler2 = new SemaphoreSlim(Math.Max(1, s.maxParallelThreads));
                    var tasks2 = new List<Task>(plannedFiles.Count);
                    foreach (var item in plannedFiles)
                    {
                        tasks2.Add(Task.Run(async () =>
                        {
                            await throttler2.WaitAsync();
                            Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal;
                            try
                            {
                                string currentPath = item.DestinationPath;
                                if (string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath)) return;

                                try
                                {
                                    var blob = store.StoreFile(currentPath);
                                    item.ManifestEntryV2.hash = blob.Hash;
                                }
                                catch (Exception ex)
                                {
                                    Log.Warn($"CAS store failed for {item.ManifestEntry.relPath}: {ex.Message}");
                                }

                                if (string.IsNullOrEmpty(item.ManifestEntryV2.hash))
                                {
                                    try
                                    {
                                        item.ManifestEntryV2.hash = FileUtilEx.SHA256Of(currentPath);
                                    }
                                    catch (Exception exHash)
                                    {
                                        Log.Warn($"SHA-256 compute failed for {item.ManifestEntry.relPath}: {exHash.Message}");
                                    }
                                }

                                if (string.IsNullOrEmpty(item.ManifestEntry.md5))
                                    item.ManifestEntry.md5 = HashCache.GetOrCompute(currentPath, HashKind.MD5);

                                if (string.IsNullOrEmpty(item.ManifestEntryV2.legacyMd5))
                                    item.ManifestEntryV2.legacyMd5 = item.ManifestEntry.md5;

                                if (item.Scan.HasMeta)
                                {
                                    string metaPath = currentPath + ".meta";
                                    if (File.Exists(metaPath))
                                    {
                                        try
                                        {
                                            var metaBlob = store.StoreFile(metaPath);
                                            item.ManifestEntryV2.metaHash = metaBlob.Hash;
                                        }
                                        catch (Exception ex)
                                        {
                                            Log.Warn($"CAS store failed for meta {item.ManifestEntry.relPath}: {ex.Message}");
                                        }

                                        if (string.IsNullOrEmpty(item.ManifestEntryV2.metaHash))
                                        {
                                            try
                                            {
                                                item.ManifestEntryV2.metaHash = FileUtilEx.SHA256Of(metaPath);
                                            }
                                            catch (Exception exHash)
                                            {
                                                Log.Warn($"SHA-256 compute failed for meta {item.ManifestEntry.relPath}: {exHash.Message}");
                                            }
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                throttler2.Release();
                            }
                        }));
                    }
                    await Task.WhenAll(tasks2);
                }

                // 4) Manifest + prune (nevern thread)
                PruneRemoved(man);
                WriteManifestV2(manV2);
                WriteManifest(man);
                File.WriteAllText(Path.Combine(CurrentDir, "backup.ok"), DateTime.UtcNow.ToString("o"));

                MainThread.Invoke(() =>
                {
                    Session.LastBackupUtc = DateTime.UtcNow;
                    Session.RunsCount = Session.RunsCount + 1;
                });

                // 4) Zip Policy
                bool shouldZip = forceZip || SnapshotCreator.ShouldCreateZip(s, hadChanges: copied > 0);
                if (shouldZip)
                {
                    if (s.zipPolicy == ZipPolicy.Idle)
                        await WaitForIdleSeconds(s.idleDelaySeconds);
                    var ctsZip = new CancellationTokenSource();
                    try { await SnapshotCreator.CreateZipAsync(s, ctsZip.Token, ctsZip, showProgressUI); }
                    catch (OperationCanceledException) { Log.Warn("ZIP canceled by user."); }
                }
                else
                {
                    Log.Info($"No ZIP created (policy: {s.zipPolicy}, changes: {copied}).");
                }

                if (showToast)
                {
                    string msg = (copied > 0)
                        ? $"Backup completed ({reason ?? "timer"})"
                        : "Backup completed (no changes)";
                    MainThread.Invoke(() => { EditorWindow.focusedWindow?.ShowNotification(new GUIContent(msg)); });
                }
                
                // Detailed logging vs simple console message
                string detailedMsg = $"Backup completed. Copied {copied}, Skipped {skipped}, Total {man.entries.Count} files.";
                Log.Info(detailedMsg, "Backup completed successfully");

                // Create version for this backup (only if there were changes or it's a manual backup)
                if (copied > 0 || reason == "manual")
                {
                    try
                    {
                        using var versionManager = new FileBasedVersionManager();
                        string versionDescription = reason switch
                        {
                            "manual" => "Manual backup",
                            "play-enter" => "Before Play Mode",
                            "vrchat-preprocess" => "Before VRChat Build",
                            _ => "Auto backup"
                        };
                        
                        bool versionCreated = versionManager.CreateVersion(versionDescription, CurrentDir);
                        if (versionCreated)
                        {
                            Log.Info("Version created for this backup");
                            s.selectionLocked = true;
                        }
                        
                        // Cleanup old versions (keep last 10)
                        versionManager.CleanupOldVersions(10);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Version creation failed (backup still successful): {ex.Message}");
                    }
                }

                s.lastBackupBytes = totalBytes;
                MainThread.Invoke(() =>
                {
                    SaveSettings(s);
                    TimerService.InvalidateSettingsCache();
                });
            }
            catch (OperationCanceledException)
            {
                Log.Warn("Backup operation was canceled by user", "Backup canceled");
            }
            catch (Exception ex)
            {
                // Use new centralized exception handling
                string detailedMsg = $"Backup operation failed: {ex.Message}";
                Log.Error(detailedMsg, "Backup failed (see log file for details)", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
                try { _one.Release(); } catch { }
                bool again = Interlocked.Exchange(ref _pendingRun, 0) == 1;
                if (again)
                {
                    RunBackupNow(s, showToast, reason, showProgressUI, forceZip);
                }
                // Next run already scheduled by TimerService before starting this backup
            }
        }

        static void PruneRemoved(BackupManifest newMan)
        {
            var keep = new HashSet<string>(newMan.entries.Select(e => e.relPath), StringComparer.OrdinalIgnoreCase);
            // Keep associated .meta files too
            foreach (var e in newMan.entries)
            {
                string meta = e.relPath + ".meta";
                if (!keep.Contains(meta)) keep.Add(meta);
            }
            keep.Add("manifest.json"); keep.Add("manifest_v2.json"); keep.Add("backup.ok");
            var all = Directory.Exists(CurrentDir) ? Directory.GetFiles(CurrentDir, "*", SearchOption.AllDirectories) : Array.Empty<string>();
            int pruned = 0;
            foreach (var abs in all)
            {
                string rel = MakeRelTo(abs, CurrentDir).Replace("\\", "/");
                if (!keep.Contains(rel))
                {
                    try
                    {
                        System.Diagnostics.Debug.Assert(abs.StartsWith(CurrentDir, StringComparison.OrdinalIgnoreCase));
                        File.Delete(abs);
                        pruned++;
                    }
                    catch { }
                }
            }
            foreach (var d in Directory.GetDirectories(CurrentDir, "*", SearchOption.AllDirectories))
                if (!Directory.EnumerateFileSystemEntries(d).Any())
                    try { Directory.Delete(d, true); } catch { }

            if (newMan != null && newMan.entries != null && newMan.entries.Count > 0)
            {
                // Find an example entry for debug reporting
                if (newMan.entries.Count > 0 && newMan.entries[0] != null && newMan.entries[0].relPath != null)
                {
                    // no-op placeholder to access newMan in debug
                }
            }
            // Log pruning activity in advanced mode
            if (newMan != null && newMan.entries != null && newMan.entries.Count >= 0)
            {
                // If advanced mode is enabled in settings, try to read it and log
                try
                {
                    var s = LoadSettings();
                    if (s?.DiagnosticsEnabled == true)
                    {
                        Log.Info($"PruneRemoved: pruned files={pruned}");
                    }
                }
                catch { }
            }
        }
        public static string MakeRelTo(string p, string root)
        {
            var pu = new Uri(Path.GetFullPath(p));
            var ru = new Uri(Path.GetFullPath(root) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(ru.MakeRelativeUri(pu).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        public static int EffectiveCopyMBps(BackupSettings s)
        {
            if (s.autoThrottle && s.lastMeasuredMBps > 0f)
            {
                // Conservative throttling for reliability - use 30% of measured speed with reasonable minimum
                // This ensures editor stays responsive even during continuous operations
                int throttled = Math.Max(10, (int)(s.lastMeasuredMBps * 0.3f));
                
                // Cap extremely high values that are likely unrealistic for sustained operations
                if (s.lastMeasuredMBps > 1000f)
                    throttled = Math.Min(throttled, 200); // Conservative cap for high-speed drives
                    
                return throttled;
            }
            return s.copyMaxMBps;
        }

        public static int EffectiveZipMBps(BackupSettings s)
        {
            if (s.autoThrottle && s.lastMeasuredMBps > 0f)
            {
                // Even more conservative for compression (CPU + IO intensive)
                int throttled = Math.Max(5, (int)(s.lastMeasuredMBps * 0.2f));
                
                // Cap for sustained compression operations
                if (s.lastMeasuredMBps > 1000f)
                    throttled = Math.Min(throttled, 100);
                    
                return throttled;
            }
            return s.zipMaxMBps;
        }

        static async Task WaitForIdleSeconds(int seconds)
        {
            while (EditorIdle.TimeSinceLastActivity < seconds)
                await Task.Delay(500);
        }

        static readonly TimeSpan BenchmarkEvery = TimeSpan.FromHours(24);

        public static async Task EnsureBenchmarkAsync(BackupSettings s)
        {
            // Run benchmark only if auto-throttle is enabled and not already run this Editor session.
            if (!s.autoThrottle) return;
            const string SessionKey = "ASB_BenchDoneThisSession";
            try
            {
                if (UnityEditor.SessionState.GetBool(SessionKey, false)) return;
            }
            catch { /* ignore if SessionState unavailable */ }

            if (Interlocked.Exchange(ref _benchBusy, 1) == 1) return;
            try
            {
                float mbps = await RunBenchmarkAsync();
                if (mbps > 0f)
                {
                    s.lastMeasuredMBps = mbps;
                    s.lastBenchmarkTicks = DateTime.UtcNow.Ticks;
                    SaveSettings(s);
                    TimerService.InvalidateSettingsCache();
                    Log.Info($"Disk throughput benchmark: {mbps:0.0} MB/s");
                }
                try { UnityEditor.SessionState.SetBool(SessionKey, true); } catch { }
            }
            finally { _benchBusy = 0; }
        }

        public static async Task RunManualBenchmarkAsync(BackupSettings s)
        {
            // Anti-spam: limit manual benchmark according to settings  
            long now = DateTime.UtcNow.Ticks;
            long lastTicks = Interlocked.Read(ref _lastManualBenchmarkTicks);
            if (now - lastTicks < TimeSpan.FromSeconds(s.manualBenchmarkCooldownSeconds).Ticks)
            {
                Log.Warn($"Manual benchmark cooldown active ({s.manualBenchmarkCooldownSeconds}s). Please wait.");
                return;
            }
            Interlocked.Exchange(ref _lastManualBenchmarkTicks, now);

            if (!Session.TryStartBenchmark())
            {
                Log.Warn("Benchmark already running.");
                return;
            }
            
            try
            {
                Log.Info("Running manual benchmark...");
                float mbps = await RunBenchmarkAsync();
                if (mbps > 0f)
                {
                    s.lastMeasuredMBps = mbps;
                    s.lastBenchmarkTicks = DateTime.UtcNow.Ticks;
                    SaveSettings(s);
                    TimerService.InvalidateSettingsCache();
                    Log.Info($"Manual benchmark result: {mbps:0.0} MB/s");
                }
                else
                {
                    Log.Warn("Manual benchmark failed to produce valid result.");
                }
            }
            finally 
            { 
                Session.EndBenchmark();
            }
        }

        static Task<float> RunBenchmarkAsync()
        {
            // Longer, more realistic benchmark for sustained operations (5-8s instead of 2s)
            return Task.Run(() =>
            {
                try
                {
                    string dir = Path.Combine(Path.GetTempPath(), "ASB_Bench");
                    Directory.CreateDirectory(dir);
                    string a = Path.Combine(dir, "a.tmp");
                    int sizeMB = 64; // Larger test for more realistic sustained performance
                    byte[] buf = new byte[1024 * 1024];

                    using (var cts = new CancellationTokenSource(8000)) // 8000 ms timeout for more realistic test
                    {
                        var sw = Stopwatch.StartNew();
                        long writtenBytes = 0;
                        try
                        {
                            using (var fs = new FileStream(a, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, FileOptions.SequentialScan))
                            {
                                for (int i = 0; i < sizeMB; i++)
                                {
                                    if (cts.IsCancellationRequested) break;
                                    fs.Write(buf, 0, buf.Length);
                                    writtenBytes += buf.Length;
                                    
                                    // Force periodic flush to simulate real backup conditions
                                    if (i % 8 == 0) fs.Flush();
                                }
                                fs.Flush();
                            }
                        }
                        catch (OperationCanceledException) { }
                        sw.Stop();

                        double writeSec = Math.Max(0.001, sw.Elapsed.TotalSeconds); // Avoid divide by zero
                        double writtenMB = writtenBytes / (1024.0 * 1024.0);
                        double writeMbps = writtenMB / writeSec;

                        // Read back test with sequential access pattern
                        sw.Restart();
                        long readBytes = 0;
                        try
                        {
                            using (var fr = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, FileOptions.SequentialScan))
                            {
                                int r;
                                while ((r = fr.Read(buf, 0, buf.Length)) > 0)
                                {
                                    if (cts.IsCancellationRequested) break;
                                    readBytes += r;
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                        sw.Stop();

                        double readSec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                        double readMB = readBytes / (1024.0 * 1024.0);
                        double readMbps = readMB / readSec;

                        try { File.Delete(a); Directory.Delete(dir); } catch { }

                        // Conservative result calculation for reliability
                        if (writtenMB <= 1.0 || readMB <= 1.0) return 0f; // Need meaningful test size
                        
                        float result = (float)Math.Min(writeMbps, readMbps);
                        
                        // Sanity check: if result seems unrealistically high, apply conservative cap
                        if (result > 2000f)
                        {
                            Log.Debug($"Benchmark result {result:0.0} MB/s seems high, applying conservative interpretation");
                            result = Math.Min(result, 1000f); // Cap unrealistic values
                        }
                        
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Benchmark failed: {ex.Message}", "Disk benchmark failed", ex);
                    return 0f;
                }
            });
        }

        static string HumanMB(long bytes)
        {
            try
            {
                double mb = bytes / (1024.0 * 1024.0);
                if (mb >= 1000) return (mb / 1024.0).ToString("0.0") + " GB";
                return mb.ToString("0.0") + " MB";
            }
            catch { return bytes + " bytes"; }
        }

    }
}
#endif