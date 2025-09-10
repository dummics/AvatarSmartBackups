#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.IO.Compression;
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
            catch (Exception ex) { Log.Warn("Cannot save settings: " + ex.Message); }
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

            return (copyJobs, man, totalBytes, copied, skipped);
        }

        // Anti re-entrancy guards
        static readonly SemaphoreSlim _one = new SemaphoreSlim(1, 1);
        static int _busy;
        static int _pendingRun;
        static int _benchBusy;
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
                int scanId = showProgressUI ? ProgressUX.Start("Avatar Smart Backup", "Scanning project", false) : -1;
                var plan = await Task.Run(() => BuildCopyPlan(s, scanId));
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
                                        IOThrottle.CopyStreamThrottled(src, dst, bufferBytes: 2 * 1024 * 1024, maxMBps: EffectiveCopyMBps(s), ct: cts.Token);
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
                                    if (total > 0) ProgressUX.Report(progId, (float)d / total, $"Copying files {d}/{total}");
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
                bool shouldZip = forceZip || ShouldCreateZip(s, hadChanges: copied > 0);
                if (shouldZip)
                {
                    if (s.zipPolicy == ZipPolicy.Idle)
                        await WaitForIdleSeconds(s.idleDelaySeconds);
                    var ctsZip = new CancellationTokenSource();
                    try { await CreateZipAsync(s, ctsZip.Token, ctsZip, showProgressUI); }
                    catch (OperationCanceledException) { Log.Warn("ZIP canceled by user."); }
                }
                else
                {
                    Log.Info($"No ZIP created (policy: {s.zipPolicy}, changes: {copied}).");
                }

                if (showToast)
                {
                    string msg = (copied > 0)
                        ? $"Backup OK ({reason ?? "timer"})  • copied {copied}, skippati {skipped}"
                        : $"No changes ({reason ?? "timer"})  • 0 files copied";
                    MainThread.Invoke(() => { EditorWindow.focusedWindow?.ShowNotification(new GUIContent(msg)); });
                }
                Log.Info($"Backup completed. Copied {copied}, Skipped {skipped}, Total {man.entries.Count}.");

                s.lastBackupBytes = totalBytes;
                MainThread.Invoke(() =>
                {
                    SaveSettings(s);
                    TimerService.InvalidateSettingsCache();
                });
            }
            catch (OperationCanceledException)
            {
                Log.Warn("Operation canceled by user.");
            }
            catch (Exception ex)
            {
                Log.Err("Backup error: " + ex);
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
                else if (Session.IsRunning)
                {
                    MainThread.Invoke(() => TimerService.ScheduleNextRun(s));
                }
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
            foreach (var abs in all)
            {
                string rel = MakeRelTo(abs, CurrentDir).Replace("\\", "/");
                if (!keep.Contains(rel))
                {
                    try
                    {
                        System.Diagnostics.Debug.Assert(abs.StartsWith(CurrentDir, StringComparison.OrdinalIgnoreCase));
                        File.Delete(abs);
                    }
                    catch { }
                }
            }
            foreach (var d in Directory.GetDirectories(CurrentDir, "*", SearchOption.AllDirectories))
                if (!Directory.EnumerateFileSystemEntries(d).Any())
                    try { Directory.Delete(d, true); } catch { }
        }
        static string MakeRelTo(string p, string root)
        {
            var pu = new Uri(Path.GetFullPath(p));
            var ru = new Uri(Path.GetFullPath(root) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(ru.MakeRelativeUri(pu).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        static bool ShouldCreateZip(BackupSettings s, bool hadChanges)
        {
            if (s.keepSnapshots <= 1) return false;          // niente snapshot richiesti
            switch (s.zipPolicy)
            {
                case ZipPolicy.OnChange:
                    return hadChanges;                        // zip solo se ci sono state changes
                case ZipPolicy.Idle:
                    return !hadChanges;                       // zip quando non ci sono changes
                case ZipPolicy.OnPlay:
                default:
                    return false;
            }
        }

        static async Task WaitForIdleSeconds(int seconds)
        {
            while (EditorIdle.TimeSinceLastActivity < seconds)
                await Task.Delay(500);
        }

        static readonly TimeSpan BenchmarkEvery = TimeSpan.FromHours(24);

        static async Task EnsureBenchmarkAsync(BackupSettings s)
        {
            if (!s.autoThrottle) return;
            DateTime last = s.lastBenchmarkTicks > 0 ? new DateTime(s.lastBenchmarkTicks, DateTimeKind.Utc) : DateTime.MinValue;
            if (s.lastMeasuredMBps <= 0f || DateTime.UtcNow - last > BenchmarkEvery)
            {
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
                }
                finally { _benchBusy = 0; }
            }
        }

        static Task<float> RunBenchmarkAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    string dir = Path.Combine(Path.GetTempPath(), "ASB_Bench");
                    Directory.CreateDirectory(dir);
                    string a = Path.Combine(dir, "a.tmp");
                    string b = Path.Combine(dir, "b.tmp");
                    int sizeMB = 32;
                    byte[] buf = new byte[1024 * 1024];
                    using (var fs = new FileStream(a, FileMode.Create, FileAccess.Write, FileShare.None))
                        for (int i = 0; i < sizeMB; i++) fs.Write(buf, 0, buf.Length);
                    var sw = Stopwatch.StartNew();
                    File.Copy(a, b, true);
                    sw.Stop();
                    double copy = sizeMB / Math.Max(0.0001, sw.Elapsed.TotalSeconds);
                    sw.Restart();
                    using (var fr = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read))
                        while (fr.Read(buf, 0, buf.Length) > 0) { }
                    sw.Stop();
                    double read = sizeMB / Math.Max(0.0001, sw.Elapsed.TotalSeconds);
                    try { File.Delete(a); File.Delete(b); Directory.Delete(dir); } catch { }
                    return (float)Math.Min(copy, read);
                }
                catch (Exception ex)
                {
                    Log.Warn("Benchmark failed: " + ex.Message);
                    return 0f;
                }
            });
        }

        static int EffectiveCopyMBps(BackupSettings s)
        {
            if (s.autoThrottle && s.lastMeasuredMBps > 0f)
                return Math.Max(1, (int)(s.lastMeasuredMBps * 0.7f));
            return s.copyMaxMBps;
        }
        static int EffectiveZipMBps(BackupSettings s)
        {
            if (s.autoThrottle && s.lastMeasuredMBps > 0f)
                return Math.Max(1, (int)(s.lastMeasuredMBps * 0.5f));
            return s.zipMaxMBps;
        }

        static string HumanMB(long bytes)
        {
            if (bytes <= 0) return "0 MB";
            double mb = bytes / (1024.0 * 1024.0);
            if (mb < 1024.0) return mb.ToString("0.0") + " MB";
            double gb = mb / 1024.0;
            return gb.ToString("0.00") + " GB";
        }

        static Task CreateZipAsync(BackupSettings s, CancellationToken ct, CancellationTokenSource ctsForUi, bool showUI)
        {
            return Task.Run(() =>
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string zipPath = Path.Combine(ArchiveDir, $"{FileUtilEx.Sanitize(FileUtilEx.ProjectName)}_{stamp}.zip");
                Directory.CreateDirectory(ArchiveDir);
                int progId = showUI ? ProgressUX.Start("Avatar Smart Backup", "Creating archive…", cancellable: true, onCancel: () => { ctsForUi.Cancel(); return true; }) : -1;
                try
                {
                    // Zip manuale con progress + throttle, scrittura atomica
                    string tmp = zipPath + ".tmp";
                    if (File.Exists(tmp)) File.Delete(tmp);
                    var files = Directory.GetFiles(CurrentDir, "*", SearchOption.AllDirectories)
                                         .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                                         .ToArray();

                    // stima dimensione per progress
                    long totalBytes = files.Sum(f => new FileInfo(f).Length);
                    long written = 0;

                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
                    {
                        foreach (var abs in files)
                        {
                            ct.ThrowIfCancellationRequested();
                            string rel = MakeRelTo(abs, CurrentDir).Replace("\\", "/");
                            var entry = zip.CreateEntry(rel, s.zipFastest ? System.IO.Compression.CompressionLevel.Fastest : System.IO.Compression.CompressionLevel.Optimal);
                            using var entryStream = entry.Open();
                            using var src = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            IOThrottle.CopyStreamThrottled(src, entryStream, bufferBytes: 2 * 1024 * 1024, maxMBps: EffectiveZipMBps(s), ct: ct);
                            written += src.Length;
                            float p = totalBytes > 0 ? (float)written / totalBytes : 1f;
                            ProgressUX.Report(progId, p, $"Zip… {rel} ({HumanMB(written)}/{HumanMB(totalBytes)})");
                        }
                    }
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    File.Move(tmp, zipPath);

                    // retention
                    if (s.keepSnapshots > 1)
                    {
                        DateTime ParseZipStamp(string path)
                        {
                            try
                            {
                                string name = Path.GetFileNameWithoutExtension(path);
                                string prefix = FileUtilEx.Sanitize(FileUtilEx.ProjectName) + "_";
                                int idx = name.LastIndexOf('_');
                                if (idx >= 0 && name.StartsWith(prefix, StringComparison.Ordinal))
                                {
                                    string ts = name.Substring(prefix.Length);
                                    if (DateTime.TryParseExact(ts, "yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var dt))
                                        return dt.ToUniversalTime();
                                }
                            }
                            catch { }
                            return new FileInfo(path).LastWriteTimeUtc;
                        }

                        var zips = Directory.GetFiles(ArchiveDir, "*.zip", SearchOption.TopDirectoryOnly)
                                            .OrderByDescending(f => ParseZipStamp(f))
                                            .ToList();
                        for (int i = s.keepSnapshots; i < zips.Count; i++)
                            try { File.Delete(zips[i]); } catch { }
                    }
                    Log.Info($"Created snapshot: {Path.GetFileName(zipPath)}");
                }

                catch (Exception ex)
                {
                    Log.Err("ZIP error: " + ex.Message);
                }
                finally
                {
                    ProgressUX.Finish(progId);
                    try
                    {
                        string tmp = zipPath + ".tmp";
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                    catch { }
                }
            }, ct);
        }

        public static void CreateSnapshotNow(BackupSettings s)
        {
            try
            {
                string srcRoot = CurrentDir;
                if (!Directory.Exists(srcRoot)) { EditorUtility.DisplayDialog("Snapshot", "No Current/ backup found. Run a backup first.", "OK"); return; }
                var ok = Path.Combine(srcRoot, "backup.ok");
                if (!File.Exists(ok)) { EditorUtility.DisplayDialog("Snapshot", "Backup in progress or not complete. Try again after it finishes.", "OK"); return; }
                var cts = new CancellationTokenSource();
                _ = CreateZipAsync(s, cts.Token, cts, showUI: true);
            }
            catch (Exception ex)
            {
                Log.Err("Snapshot error: " + ex.Message);
            }
        }
    }
}
#endif
