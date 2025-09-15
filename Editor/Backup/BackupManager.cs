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
        static readonly IBackupCollector[] Collectors = new IBackupCollector[]
        {
            new VRCAssetsCollector(), new ControllersCollector(), new AnimClipsCollector(), new ScenesCollector(),
            new MaterialsCollector(), new DllCollector(), new AdditionalExtensionsCollector()
        };

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

        static (List<(string src, string dst)> jobs, BackupManifest manifest, long totalBytes, int copied, int skipped) BuildCopyPlan(BackupSettings s, int progId)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inc = IncrementalCollector.ConsumeChanges()?.ToArray() ?? Array.Empty<string>();
            if (inc.Length > 0 && s?.debugMode == true)
            {
                Log.Info($"IncrementalCollector: {inc.Length} changes consumed");
            }
            bool haveInc = inc.Length > 0;
            if (haveInc)
            {
                foreach (var abs0 in inc)
                {
                    string abs = abs0;
                    if (abs.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    {
                        string baseP = abs.Substring(0, abs.Length - 5);
                        if (File.Exists(baseP)) abs = baseP;
                    }
                    if (!File.Exists(abs)) continue;
                    string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\", "/");
                    if (CollectHelpers.PassesFolderFilters(rel, s)) set.Add(abs);
                }
            }
            if (!haveInc)
            {
                foreach (var c in Collectors)
                    foreach (var abs in c.CollectAbsolutePaths(s))
                        if (File.Exists(abs)) set.Add(abs);
            }

            if (s?.debugMode == true)
            {
                Log.Info($"BuildCopyPlan: initial candidate count: {set.Count}");
            }

            set.RemoveWhere(p => !FileUtilEx.MakeRelToProject(p).Replace("\\", "/").StartsWith("Assets/", StringComparison.OrdinalIgnoreCase));
            set.RemoveWhere(p => p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
                                 p.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ||
                                 p.EndsWith(".blend", StringComparison.OrdinalIgnoreCase));

            Directory.CreateDirectory(CurrentDir);
            Directory.CreateDirectory(ArchiveDir);

            var prev = LoadManifest();
            var man = new BackupManifest
            {
                projectName = FileUtilEx.ProjectName,
                unityVersion = Application.unityVersion,
                createdUtc = DateTime.UtcNow.ToString("o"),
                entries = new List<ManifestEntry>()
            };

            var copyJobs = new List<(string src, string dst)>(set.Count);
            int copied = 0, skipped = 0;
            long totalBytes = 0;
            int i = 0;

            foreach (var abs in set)
            {
                i++;
                string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\", "/");
                string guid = FileUtilEx.TryReadGuidFromMeta(abs) ?? string.Empty;
                var fi = new FileInfo(abs);
                totalBytes += fi.Length;

                bool needsCopy = true;
                string md5 = null;
                ManifestEntry prevEntry = null;
                if (prev != null)
                {
                    prevEntry = (!string.IsNullOrEmpty(guid))
                        ? prev.entries.FirstOrDefault(x => x.guid == guid)
                        : prev.entries.FirstOrDefault(x => x.relPath == rel);
                    if (prevEntry != null && prevEntry.size == fi.Length && prevEntry.lastWriteUtcTicks == fi.LastWriteTimeUtc.Ticks)
                    {
                        needsCopy = false;
                        md5 = prevEntry.md5;
                    }
                }

                man.entries.Add(new ManifestEntry
                {
                    guid = guid,
                    relPath = rel,
                    size = fi.Length,
                    lastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                    md5 = md5
                });

                string dst = Path.Combine(CurrentDir, rel);
                if (needsCopy)
                {
                    copyJobs.Add((abs, dst));
                    copied++;
                }
                else
                {
                    bool moved = true;
                    if (prevEntry != null && !string.Equals(prevEntry.relPath, rel, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string oldAbs = Path.Combine(CurrentDir, prevEntry.relPath);
                            if (File.Exists(oldAbs))
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                                if (File.Exists(dst)) File.Delete(dst);
                                File.Move(oldAbs, dst);
                            }
                            else moved = false;
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"Move-in-cache failed: {prevEntry.relPath} -> {rel} – {ex.Message}. Falling back to copy.");
                            moved = false;
                        }
                    }
                    if (!moved)
                    {
                        copyJobs.Add((abs, dst));
                        copied++;
                    }
                    else
                    {
                        skipped++;
                    }
                }

                var meta = abs + ".meta";
                if (File.Exists(meta)) copyJobs.Add((meta, Path.Combine(CurrentDir, rel + ".meta")));

                if (progId >= 0 && set.Count > 0 && i % 20 == 0)
                    ProgressUX.Report(progId, (float)i / set.Count, $"Planning {i}/{set.Count}");
            }

            if (s?.debugMode == true)
            {
                Log.Info($"BuildCopyPlan: planned copyJobs={copyJobs.Count} copied={copied} skipped={skipped} totalEntries={man.entries.Count} totalBytes={totalBytes}");
            }
            return (copyJobs, man, totalBytes, copied, skipped);
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
                    Log.Info("Backup already running – queued another run.");
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
                double intervalSec = s.debugMode ? s.intervalMinutes : s.intervalMinutes * 60;
                if (!s.debugMode && s.intervalMinutes < 5)
                    Log.Warn("Backup interval under 5 minutes may affect editor performance.");
                if (s.lastBackupBytes > 0 && copyCap > 0)
                {
                    double secNeeded = s.lastBackupBytes / (copyCap * 1024.0 * 1024.0);
                    if (secNeeded > intervalSec)
                    {
                        string unit = s.debugMode ? "sec" : "min";
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
                var copyJobs = plan.jobs;
                var man = plan.manifest;
                long totalBytes = plan.totalBytes;
                int copied = plan.copied;
                int skipped = plan.skipped;

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
                                    Log.Warn($"Copy failed: {job.src} – {ex.Message}");
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

                // 3) Compute MD5 for changed files (background)
                var md5Missing = man.entries.Where(e => string.IsNullOrEmpty(e.md5)).ToList();
                if (md5Missing.Count > 0)
                {
                    var throttler2 = new SemaphoreSlim(Math.Max(1, s.maxParallelThreads));
                    var tasks2 = new List<Task>(md5Missing.Count);
                    foreach (var e in md5Missing)
                    {
                        tasks2.Add(Task.Run(async () =>
                        {
                            await throttler2.WaitAsync();
                            Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal;
                            try
                            {
                                string abs = Path.Combine(CurrentDir, e.relPath);
                                if (File.Exists(abs)) e.md5 = FileUtilEx.MD5Of(abs);
                            }
                            finally { throttler2.Release(); }
                        }));
                    }
                    await Task.WhenAll(tasks2);
                }

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
                        ? $"Backup completed ({reason ?? "timer"}) • {copied} files copied"
                        : $"Backup completed ({reason ?? "timer"}) • no changes";
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
            // Log pruning activity in debug mode
            // Debug log solo sul main thread per evitare EditorPrefs/Json in background
            MainThread.Invoke(() =>
            {
                try
                {
                    var s = LoadSettings();
                    if (s?.debugMode == true)
                        Log.Info($"PruneRemoved: pruned files={pruned}");
                }
                catch { }
            });
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
                    // Mutazioni ed Editor API sul main thread
                    MainThread.Invoke(() =>
                    {
                        s.lastMeasuredMBps = mbps;
                        s.lastBenchmarkTicks = DateTime.UtcNow.Ticks;
                        SaveSettings(s);
                        TimerService.InvalidateSettingsCache();
                        Log.Info($"Disk throughput benchmark: {mbps:0.0} MB/s");
                    });
                }
                MainThread.Invoke(() => { try { UnityEditor.SessionState.SetBool(SessionKey, true); } catch { } });
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
                    // Usa path unico per run per evitare sharing violation se benchmark paralleli o precedente non pulito
                    string baseDir = Path.Combine(Path.GetTempPath(), "ASB_Bench");
                    string dir = Path.Combine(baseDir, Guid.NewGuid().ToString("N"));
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

                        try { if (File.Exists(a)) File.Delete(a); Directory.Delete(dir, true); } catch { }

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
