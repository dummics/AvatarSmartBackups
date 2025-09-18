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
            if (s.forceFullCheckpointEveryN < 0) s.forceFullCheckpointEveryN = 0;
            s.EnsureVersioningDefaults();
            s.SyncLegacyCheckpointInterval();
            if (s.diskWarningFreeMB <= 0 && s.diskWarningFreePercent <= 0f && s.diskCriticalFreeMB <= 0 && s.diskCriticalFreePercent <= 0f)
            {
                s.diskWarningFreeMB = 2048;
                s.diskCriticalFreeMB = 1024;
                s.diskWarningFreePercent = 0.10f;
                s.diskCriticalFreePercent = 0.05f;
                s.diskPreBackupBufferMB = Math.Max(s.diskPreBackupBufferMB, 512);
                if (!s.diskSpaceProtection) s.diskSpaceProtection = true;
            }
            if (s.diskWarningFreeMB < 0) s.diskWarningFreeMB = 0;
            if (s.diskCriticalFreeMB < 0) s.diskCriticalFreeMB = 0;
            if (s.diskWarningFreePercent < 0f) s.diskWarningFreePercent = 0f;
            if (s.diskWarningFreePercent > 0.5f) s.diskWarningFreePercent = 0.5f;
            if (s.diskCriticalFreePercent < 0f) s.diskCriticalFreePercent = 0f;
            if (s.diskCriticalFreePercent > 0.3f) s.diskCriticalFreePercent = 0.3f;
            if (s.diskPreBackupBufferMB < 0) s.diskPreBackupBufferMB = 0;
            if (s.diskCriticalFreeMB > 0 && s.diskWarningFreeMB > 0 && s.diskCriticalFreeMB > s.diskWarningFreeMB)
                s.diskCriticalFreeMB = Math.Max(512, Math.Min(s.diskWarningFreeMB, s.diskCriticalFreeMB));
            if (s.diskCriticalFreePercent > 0 && s.diskWarningFreePercent > 0 && s.diskCriticalFreePercent > s.diskWarningFreePercent)
                s.diskCriticalFreePercent = Math.Min(s.diskWarningFreePercent, s.diskCriticalFreePercent);
            if (s.diskPreBackupBufferMB == 0) s.diskPreBackupBufferMB = 512;

            if (s.diskWarningFreeMB < 0) s.diskWarningFreeMB = 0;
            if (s.diskCriticalFreeMB < 0) s.diskCriticalFreeMB = 0;
            if (s.diskWarningFreePercent < 0f) s.diskWarningFreePercent = 0f;
            if (s.diskWarningFreePercent > 0.5f) s.diskWarningFreePercent = 0.5f;
            if (s.diskCriticalFreePercent < 0f) s.diskCriticalFreePercent = 0f;
            if (s.diskCriticalFreePercent > 0.3f) s.diskCriticalFreePercent = 0.3f;
            if (s.diskPreBackupBufferMB < 0) s.diskPreBackupBufferMB = 0;

            if (!s.AdvancedMode && !s.easyMode)
                s.easyMode = true;
            else if (s.AdvancedMode && s.easyMode)
                s.easyMode = false;

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
                s.EnsureVersioningDefaults();
                s.SyncLegacyCheckpointInterval();
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

        // Removed legacy manifest_v2 support

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
            public string DestinationPath;
            public bool NeedsCopy;
        }

        struct CopyPlan
        { 
            public List<(string src, string dst)> CopyJobs;
            public BackupManifest Manifest;
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

            // no prevV2

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

            // legacy v2 manifest removed
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

                // No v2 entry/hashing; still queue file item for MD5 if needed for manifest
                files.Add(new FilePlanItem
                {
                    Scan = scan,
                    ManifestEntry = entry,
                    DestinationPath = destination,
                    NeedsCopy = needsCopy
                });

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

                    string absPrev = FileUtilEx.AssetToAbs(prevEntry.relPath);
                    if (File.Exists(absPrev))
                    {
                        var scan = new FileScanResult(prevEntry.relPath, absPrev, prevEntry.size, prevEntry.lastWriteUtcTicks, File.Exists(absPrev + ".meta"));
                        files.Add(new FilePlanItem
                        {
                            Scan = scan,
                            ManifestEntry = mergedEntry,
                            DestinationPath = Path.Combine(CurrentDir, prevEntry.relPath),
                            NeedsCopy = false
                        });
                    }
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
            bool success = false;
            bool versionCreated = false;
            int? createdVersionId = null;
            bool createdVersionIsCheckpoint = false;
            long totalBytes = 0;
            int copied = 0;
            string normalizedReason = string.IsNullOrEmpty(reason) ? "timer" : reason;
            bool ShouldAbortForDisk(DiskSpaceReport report)
            {
                if (report.Status == DiskSpaceStatus.Critical && report.BlockBackup)
                {
                    string title = "Low Disk Space";
                    string msg = report.Message + "\n\nBackup aborted.";
                    MainThread.Invoke(() => EditorUtility.DisplayDialog(title, msg, "OK"));
                    Log.Warn(report.Message, title);
                    return true;
                }
                if (report.Status == DiskSpaceStatus.Warning)
                {
                    Log.Warn(report.Message, "Low disk space warning");
                }
                else if (report.Status == DiskSpaceStatus.Critical)
                {
                    Log.Warn(report.Message, "Low disk space");
                }
                return false;
            }
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

                long estimatedPreBytes = s.lastBackupBytes > 0 ? s.lastBackupBytes : 50L * 1024L * 1024L;
                var preReport = DiskSpaceMonitor.Check(s, estimatedPreBytes, DiskSpaceStage.PreCheck);
                if (ShouldAbortForDisk(preReport))
                {
                    Interlocked.Exchange(ref _busy, 0);
                    try { _one.Release(); } catch { }
                    return;
                }

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
                var plannedFiles = plan.Files;
                totalBytes = plan.TotalBytes;
                copied = plan.CopiedCount;
                int skipped = plan.SkippedCount;

                var planReport = DiskSpaceMonitor.Check(s, totalBytes, DiskSpaceStage.PlanEstimate);
                if (ShouldAbortForDisk(planReport))
                {
                    Interlocked.Exchange(ref _busy, 0);
                    try { _one.Release(); } catch { }
                    return;
                }

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

                // 3) Hashing stage removed (legacy manifest_v2 removed). Keep MD5 reuse via manifest entries only.

                // 4) Manifest + prune (nevern thread)
                PruneRemoved(man);
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
                if (copied > 0 || reason == "manual" || s.filtersDirty)
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

                        bool? forceCheckpoint = s.filtersDirty ? true : (bool?)null;
                        versionCreated = versionManager.CreateVersion(versionDescription, CurrentDir, s, forceCheckpoint: forceCheckpoint);
                        if (versionCreated)
                        {
                            Log.Info("Version created for this backup");
                            s.selectionLocked = true;
                            if (s.filtersDirty)
                            {
                                s.filtersDirty = false;
                                s.filtersChangedTicks = 0;
                            }

                            try
                            {
                                var versions = versionManager.GetVersions();
                                if (versions != null && versions.Count > 0)
                                {
                                    var created = versions.OrderByDescending(v => v.id).FirstOrDefault();
                                    if (created != null)
                                    {
                                        createdVersionId = created.id;
                                        createdVersionIsCheckpoint = created.isCheckpoint;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Debug("Failed to resolve created version info: " + ex.Message);
                            }
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
                DiskSpaceMonitor.Check(s, 0, DiskSpaceStage.PostBackup);
                success = true;
            }
            catch (OperationCanceledException)
            {
                Log.Warn("Backup operation was canceled by user", "Backup canceled");
                success = false;
            }
            catch (Exception ex)
            {
                // Use new centralized exception handling
                string detailedMsg = $"Backup operation failed: {ex.Message}";
                Log.Error(detailedMsg, "Backup failed (see log file for details)", ex);
                success = false;
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
                try { _one.Release(); } catch { }
                var summary = new BackupRunSummary(success, normalizedReason, copied > 0, versionCreated, createdVersionId, createdVersionIsCheckpoint, totalBytes, DateTime.UtcNow);
                MainThread.Invoke(() => BackupEvents.RaiseCompleted(summary));
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
            keep.Add("manifest.json"); keep.Add("backup.ok");
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
