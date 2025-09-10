// Unity 2022.3+ – Editor only
// Avatar Smart Backup – background copy+zip, non-blocking progress, IO throttle, zip-on-change
// Log tag: [ Avatar Backup System ]

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
    // Zip snapshot creation policy
    public enum ZipPolicy
    {
        OnChange, // create a snapshot only when files changed
        Idle,     // create a snapshot when no changes were detected
        OnPlay    // take a snapshot when entering Play Mode
    }

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



        [InitializeOnLoad]
        public static class TimerService
        {
            static readonly double UpdateEverySec = 0.5;
            static double _nextTick;
            static BackupSettings _cached;
            static double _nextReload;

            static TimerService()
            {
                EditorApplication.update += Update;
                var settings = BackupManager.LoadSettings();
                _ = BackupManager.EnsureBenchmarkAsync(settings);
                if (settings.autoRunOnLoad) StartTimerIfNeeded(settings);
                TryHookVRChat();
            }

            static BackupSettings GetSettingsCached()
            {
                if (_cached == null || EditorApplication.timeSinceStartup >= _nextReload)
                {
                    _cached = BackupManager.LoadSettings();
                    _nextReload = EditorApplication.timeSinceStartup + 10.0; // reload every 10s
                }
                return _cached;
            }
            public static void InvalidateSettingsCache() { _cached = null; _nextReload = 0; }

            static TimeSpan GetInterval(BackupSettings s)
            {
                double v = Math.Max(1, s.intervalMinutes);
                return s.debugMode ? TimeSpan.FromSeconds(v) : TimeSpan.FromMinutes(v);
            }

            public static void StartTimerIfNeeded(BackupSettings s)
            {
                if (!Session.IsRunning)
                {
                    Session.IsRunning = true;
                    ScheduleNext(DateTime.UtcNow + GetInterval(s));
                    Log.Info("Timer started.");
                }
                else if (Session.NextRunUtc == null)
                {
                    ScheduleNext(DateTime.UtcNow + GetInterval(s));
                }
            }
            public static void PauseTimer() { Session.IsRunning = false; Log.Info("Timer paused."); }
            static void ScheduleNext(DateTime utc) => Session.NextRunUtc = utc;
            public static void ScheduleNextRun(BackupSettings s) => ScheduleNext(DateTime.UtcNow + GetInterval(s));

            static void Update()
            {
                if (EditorApplication.timeSinceStartup < _nextTick) return;
                _nextTick = EditorApplication.timeSinceStartup + UpdateEverySec;

                var s = GetSettingsCached();
                if (!Session.IsRunning) return;
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;

                var now = DateTime.UtcNow;
                var due = Session.NextRunUtc ?? now;
                if (now >= due)
                {
                    if (_busy == 1)
                    {
                        Interlocked.Exchange(ref _pendingRun, 1);
                        ScheduleNext(now + TimeSpan.FromSeconds(1));
                    }
                    else
                    {
                        BackupManager.RunBackupNow(s, showToast: false, reason: "timer", showProgressUI: false);
                    }
                }
            }

            [InitializeOnLoadMethod]
            static void HookPlaymodeShot()
            {
                EditorApplication.playModeStateChanged += (state) =>
                {
                    var sNow = GetSettingsCached();
                    if (state == PlayModeStateChange.ExitingEditMode && sNow.backupOnPlayEnter)
                    {
                        bool forceZip = (sNow.zipPolicy == ZipPolicy.OnPlay);
                        BackupManager.RunBackupNow(sNow, showToast: false, reason: "play-enter", showProgressUI: false, forceZip: forceZip);
                    }
                };
            }

            static void TryHookVRChat()
            {
                if (Session.HookedVRC) return;
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type[] types;
                        try { types = asm.GetTypes(); } catch { continue; }
                        foreach (var t in types)
                        {
                            if (t.FullName == null || t.FullName.IndexOf("VRC", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var ev = t.GetEvent("OnPreprocessAvatar", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                            if (ev == null) continue;

                            Delegate del;
                            var et = ev.EventHandlerType;
                            var invoke = et.GetMethod("Invoke");
                            var pars = invoke.GetParameters();
                            if (pars.Length == 0)
                                del = Delegate.CreateDelegate(et, null, typeof(TimerService).GetMethod(nameof(OnVRC0), BindingFlags.NonPublic | BindingFlags.Static));
                            else if (pars.Length == 1)
                                del = Delegate.CreateDelegate(et, null, typeof(TimerService).GetMethod(nameof(OnVRC1), BindingFlags.NonPublic | BindingFlags.Static));
                            else
                                del = Delegate.CreateDelegate(et, null, typeof(TimerService).GetMethod(nameof(OnVRCPreprocessAvatar), BindingFlags.NonPublic | BindingFlags.Static));

                            ev.AddEventHandler(null, del);
                            Session.HookedVRC = true;
                            Log.Info("Hooked VRChat OnPreprocessAvatar on " + t.FullName);
                            return;
                        }
                    }
                }
                catch (Exception ex) { Log.Warn("VRChat hook failed (fallback to timer/Play). " + ex.Message); }
            }

            static void OnVRC0() => BackupManager.RunBackupNow(GetSettingsCached(), showToast: false, reason: "vrchat-preprocess", showProgressUI: false);
            static void OnVRC1(object _) => BackupManager.RunBackupNow(GetSettingsCached(), showToast: false, reason: "vrchat-preprocess", showProgressUI: false);
            static void OnVRCPreprocessAvatar(object _a, object _b) => BackupManager.RunBackupNow(GetSettingsCached(), showToast: false, reason: "vrchat-preprocess", showProgressUI: false);
        }

        public class AvatarSmartBackupWindow : EditorWindow
        {
            Vector2 _scroll;
            BackupSettings _settings;
            string _newIncludePattern = "";
            string _newExcludePattern = "";
            string _includePrefix = "";
            string _excludePrefix = "";
            UnityEngine.Object _includeFolderObj;
            UnityEngine.Object _excludeFolderObj;

            [MenuItem("Tools/Avatar Smart Backup")]
            public static void Open()
            {
                var w = GetWindow<AvatarSmartBackupWindow>(true, "Avatar Smart Backup");
                w.minSize = new Vector2(320, 320);
                w.Show();
            }

            void OnEnable() => _settings = BackupManager.LoadSettings();
            void OnDisable() { BackupManager.SaveSettings(_settings); BackupManager.TimerService.InvalidateSettingsCache(); }

            void OnGUI()
            {
                if (_settings == null) _settings = BackupManager.LoadSettings();
                _scroll = EditorGUILayout.BeginScrollView(_scroll);

                EditorGUILayout.LabelField("Avatar Smart Backup", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("Automatic safety copies in the background. Keeps Unity responsive. Creates compressed snapshots only when it’s helpful.", MessageType.Info);

                EditorGUILayout.BeginVertical("box");
                bool running = Session.IsRunning;
                if (GUILayout.Button(new GUIContent($"Automatic backups: {(running ? "On" : "Off")}", "Toggle the background timer that triggers backups.")))
                {
                    if (running) TimerService.PauseTimer(); else TimerService.StartTimerIfNeeded(_settings);
                }
                EditorGUILayout.BeginHorizontal();
                string lbl = _settings.debugMode ? "Interval (s)" : "Interval (min)";
                EditorGUILayout.LabelField(lbl, GUILayout.Width(110));
                int maxInt = _settings.debugMode ? 119 : 240;
                _settings.intervalMinutes = Mathf.Clamp(EditorGUILayout.IntField(_settings.intervalMinutes, GUILayout.Width(60)), 1, maxInt);
                EditorGUILayout.EndHorizontal();
                bool warnInterval = !_settings.debugMode && _settings.intervalMinutes < 5;
                int effCopy = BackupManager.EffectiveCopyMBps(_settings);
                bool warnSpeed = false;
                double secNeeded = 0;
                double intervalSec = _settings.debugMode ? _settings.intervalMinutes : _settings.intervalMinutes * 60;
                if (_settings.lastBackupBytes > 0 && effCopy > 0)
                {
                    secNeeded = _settings.lastBackupBytes / (effCopy * 1024.0 * 1024.0);
                    warnSpeed = secNeeded > intervalSec;
                }
                if (warnInterval)
                    EditorGUILayout.HelpBox("Intervals under 5 minutes may impact editor performance.", MessageType.Warning);
                if (warnSpeed)
                {
                    double minutesNeeded = secNeeded / 60.0;
                    double mb = _settings.lastBackupBytes / (1024.0 * 1024.0);
                    EditorGUILayout.HelpBox($"At {effCopy} MB/s, backing up {mb:0.0} MB takes ~{minutesNeeded:0.0} min, exceeding the interval.", MessageType.Warning);
                }
                EditorGUILayout.EndVertical();

                EditorGUILayout.LabelField($"Next: {Session.NextRunUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "--"}    Last: {Session.LastBackupUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "never"}");

                // Pulsanti verticali
                if (GUILayout.Button(new GUIContent("Backup Now", "Start a backup immediately (non-blocking)."))) BackupManager.RunBackupNow(_settings, showToast: true, reason: "manual", showProgressUI: true);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("Create Snapshot Now", "Create a .zip snapshot from the current backup content."))) BackupManager.CreateSnapshotNow(_settings);
                if (GUILayout.Button(new GUIContent("Open Backup Folder", "Open the folder where backups are stored."))) EditorUtility.RevealInFinder(FileUtilEx.BackupRoot);
                EditorGUILayout.EndHorizontal();

                _settings.showAdvanced = EditorGUILayout.Foldout(_settings.showAdvanced, "Advanced Settings");
                if (_settings.showAdvanced)
                {
                    // GENERAL
                    EditorGUILayout.BeginVertical("box");
                    EditorGUILayout.LabelField("General", EditorStyles.boldLabel);
                    EditorGUI.BeginChangeCheck();
                    bool newUseProject = EditorGUILayout.ToggleLeft(new GUIContent("Use project-local settings (override)", "Store settings in ProjectSettings so they travel with the project."), _settings.useProjectSettings);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _settings.useProjectSettings = newUseProject;
                        BackupManager.SaveSettings(_settings);
                        BackupManager.TimerService.InvalidateSettingsCache();
                        _settings = BackupManager.LoadSettings();
                    }
                    _settings.debugMode = EditorGUILayout.ToggleLeft(new GUIContent("Debug mode", "Enable second-based intervals and extra debug options."), _settings.debugMode);
                    EditorGUILayout.EndVertical();

                    // ZIP POLICY
                    EditorGUILayout.BeginVertical("box");
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Zip Policy", EditorStyles.boldLabel);
                    if (GUILayout.Button(new GUIContent("?", "What do these options mean?"), GUILayout.Width(22)))
                    {
                        EditorUtility.DisplayDialog("Zip Policy", "On Change: create a snapshot only when files changed.\nIdle: create a snapshot when no changes were detected.\nOn Play: take a snapshot when entering Play Mode.", "OK");
                    }
                    EditorGUILayout.EndHorizontal();
                    _settings.zipPolicy = (ZipPolicy)EditorGUILayout.EnumPopup(new GUIContent("Mode", "When to create zip snapshots."), _settings.zipPolicy);
                    using (new EditorGUI.DisabledScope(_settings.zipPolicy != ZipPolicy.Idle))
                    {
                        _settings.idleDelaySeconds = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Idle delay (s)", "Seconds of inactivity before creating a zip when policy is Idle."), _settings.idleDelaySeconds), 1, 3600);
                    }
                    _settings.keepSnapshots = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Keep last snapshots", "How many .zip snapshots to keep in the Archive folder (1 disables snapshots)."), _settings.keepSnapshots), 1, 50);
                    _settings.zipFastest = EditorGUILayout.ToggleLeft(new GUIContent("Compression level: Fastest (quicker)", "Fastest is quicker but larger archives. Untick for Optimal (smaller, slower)."), _settings.zipFastest);
                    EditorGUILayout.EndVertical();

                    // PERFORMANCE
                    EditorGUILayout.BeginVertical("box");
                    EditorGUILayout.LabelField("Performance", EditorStyles.boldLabel);
                    _settings.autoThrottle = EditorGUILayout.ToggleLeft(new GUIContent("Auto throttle (recommended)", "Automatically caps IO speed to keep the editor responsive."), _settings.autoThrottle);
                    _settings.maxParallelThreads = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Max parallel threads", "Number of concurrent copy/hash tasks."), _settings.maxParallelThreads), 1, Math.Max(1, System.Environment.ProcessorCount));
                    _settings.saveScenesBeforeBackup = EditorGUILayout.ToggleLeft(new GUIContent("Save open scenes before backup", "Saves scenes if dirty before backup. May block briefly."), _settings.saveScenesBeforeBackup);
                    if (_settings.lastMeasuredMBps > 0f)
                        EditorGUILayout.LabelField($"Measured throughput: {_settings.lastMeasuredMBps:F1} MB/s", EditorStyles.miniLabel);
                    if (GUILayout.Button(new GUIContent("Re-run benchmark", "Measure disk throughput again."), GUILayout.Width(150)))
                    {
                        _settings.lastMeasuredMBps = 0f;
                        _settings.lastBenchmarkTicks = 0;
                        BackupManager.SaveSettings(_settings);
                        BackupManager.TimerService.InvalidateSettingsCache();
                        _ = BackupManager.EnsureBenchmarkAsync(_settings);
                    }
                    EditorGUILayout.EndVertical();

                    // WHAT TO INCLUDE
                    EditorGUILayout.BeginVertical("box");
                    EditorGUILayout.LabelField("What to include", EditorStyles.boldLabel);
                    _settings.incVRCAssets = EditorGUILayout.ToggleLeft(new GUIContent("VRC Expressions (.asset)", "Common VRC expression assets and similarly named .asset files."), _settings.incVRCAssets);
                    _settings.incAnimControllers = EditorGUILayout.ToggleLeft(new GUIContent("Animator Controllers (.controller)", "Animator controller assets."), _settings.incAnimControllers);
                    _settings.incAnimationClips = EditorGUILayout.ToggleLeft(new GUIContent("Animation Clips (.anim)", "Animation clip assets."), _settings.incAnimationClips);
                    _settings.incScenes = EditorGUILayout.ToggleLeft(new GUIContent("Scenes (.unity)", "Unity scene files."), _settings.incScenes);
                    _settings.incMaterials = EditorGUILayout.ToggleLeft(new GUIContent("Materials (.mat) under size limit", "Small material files. Larger ones are skipped by threshold."), _settings.incMaterials);
                    using (new EditorGUI.DisabledScope(!_settings.incMaterials))
                    {
                        // Presets for materials
                        string[] names = new[] { "256 KB", "512 KB", "1 MB", "2 MB", "4 MB", "Custom" };
                        long[] values = new long[] { 256, 512, 1024, 2048, 4096, -1 };
                        _settings.materialsSizePresetIndex = EditorGUILayout.Popup(new GUIContent("Material size limit", "Skip materials larger than this size."), _settings.materialsSizePresetIndex, names);
                        int mi = Mathf.Clamp(_settings.materialsSizePresetIndex, 0, names.Length - 1);
                        if (mi < names.Length - 1)
                        {
                            _settings.materialsMaxKB = values[mi];
                            EditorGUILayout.LabelField($"= {_settings.materialsMaxKB} KB");
                        }
                        else
                        {
                            long v = EditorGUILayout.LongField(new GUIContent("Custom (KB)", "Custom max size in KB."), _settings.materialsMaxKB);
                            v = Math.Max(10L, Math.Min(v, 100L * 1024L));
                            _settings.materialsMaxKB = v;
                        }
                    }
                    _settings.incDlls = EditorGUILayout.ToggleLeft(new GUIContent("Plugin .dll in Assets (under size limit)", "Small .dll files under Assets/ (useful for simple plugins)."), _settings.incDlls);
                    using (new EditorGUI.DisabledScope(!_settings.incDlls))
                    {
                        string[] names = new[] { "512 KB", "1 MB", "2 MB", "4 MB", "Custom" };
                        long[] values = new long[] { 512, 1024, 2048, 4096, -1 };
                        _settings.dllSizePresetIndex = EditorGUILayout.Popup(new GUIContent("DLL size limit", "Skip DLLs larger than this size."), _settings.dllSizePresetIndex, names);
                        int di = Mathf.Clamp(_settings.dllSizePresetIndex, 0, names.Length - 1);
                        if (di < names.Length - 1)
                        {
                            _settings.dllsMaxKB = values[di];
                            EditorGUILayout.LabelField($"= {_settings.dllsMaxKB} KB");
                        }
                        else
                        {
                            long v = EditorGUILayout.LongField(new GUIContent("Custom (KB)", "Custom max size in KB."), _settings.dllsMaxKB);
                            v = Math.Max(128L, Math.Min(v, 1024L * 10L));
                            _settings.dllsMaxKB = v;
                        }
                    }
                    EditorGUILayout.EndVertical();

                    // FOLDERS & EXTENSIONS (semplificato, con selezione interna al Project)
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("Folders & Types", EditorStyles.boldLabel);
                // Small contained text (not a big helpbox)
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("Add folders from Project or simple extensions (e.g., .prefab).", EditorStyles.miniLabel);
                _settings.extWithinIncludeFolders = EditorGUILayout.ToggleLeft(new GUIContent("Apply extensions only within included folders", "When enabled, extensions like .prefab are searched only inside the folders you included."), _settings.extWithinIncludeFolders);
                EditorGUILayout.EndVertical();

                    DrawIncludeExcludeSection("Include", _settings.includeFolders, ref _newIncludePattern, ref _includePrefix, ref _includeFolderObj);
                    EditorGUILayout.Space(6);
                    DrawIncludeExcludeSection("Exclude", _settings.excludeFolders, ref _newExcludePattern, ref _excludePrefix, ref _excludeFolderObj);
                    EditorGUILayout.EndVertical();
                }

                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("Restore (safe)", EditorStyles.boldLabel);
                if (GUILayout.Button(new GUIContent("Preview & Restore latest backup", "Preview files and choose what to restore. A pre-restore backup of current Assets can be created."), GUILayout.Height(22)))
                {
                    RestorePreviewWindow.Open();
                }
               // if (GUILayout.Button("Open Current/ for manual restore"))
               //     EditorUtility.RevealInFinder(Path.Combine(FileUtilEx.BackupRoot, "Current"));

                EditorGUILayout.EndScrollView();

                if (GUI.changed) { BackupManager.SaveSettings(_settings); BackupManager.TimerService.InvalidateSettingsCache(); }
            }



            static void DrawStringListVertical(List<string> list, string addLabel)
            {
                int remove = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    list[i] = EditorGUILayout.TextField(list[i], GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("X", GUILayout.Width(20))) remove = i;
                    EditorGUILayout.EndHorizontal();
                }
                if (remove >= 0) list.RemoveAt(remove);
                if (GUILayout.Button(addLabel)) list.Add("Assets/");
            }

            static void DrawIncludeExcludeSection(string title, List<string> list, ref string newExt, ref string newPrefix, ref UnityEngine.Object folderObj)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(title + $":  (" + list.Count + ")", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(list.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent("Clear", "Remove all entries from this list"), GUILayout.Width(60)))
                        list.Clear();
                }
                EditorGUILayout.EndHorizontal();
                // Add-area contained in a small box
                EditorGUILayout.BeginVertical("box");
                // Add folder (Project picker)
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Folder", GUILayout.Width(60));
                var newObj = EditorGUILayout.ObjectField(folderObj, typeof(DefaultAsset), false);
                if (newObj != folderObj) folderObj = newObj;
                using (new EditorGUI.DisabledScope(folderObj == null))
                {
                    if (GUILayout.Button("Add", GUILayout.Width(60)))
                    {
                        string p = AssetDatabase.GetAssetPath(folderObj);
                        if (string.IsNullOrEmpty(p) || !AssetDatabase.IsValidFolder(p))
                        {
                            EditorUtility.DisplayDialog("Not a folder", "Please select a folder inside the Project window.", "OK");
                        }
                        else
                        {
                            if (!p.EndsWith("/")) p += "/";
                            if (!list.Contains(p)) list.Add(p);
                            folderObj = null; // clear selection after add
                        }
                    }
                }
                EditorGUILayout.EndHorizontal();

                // Add extension
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Extension", GUILayout.Width(60));
                newExt = EditorGUILayout.TextField(newExt, GUILayout.ExpandWidth(true));
                EditorGUILayout.LabelField("e.g. .prefab", EditorStyles.miniLabel, GUILayout.Width(90));
                if (GUILayout.Button("Add", GUILayout.Width(60)))
                {
                    var t = (newExt ?? string.Empty).Trim();
                    if (t.StartsWith("*")) t = t.Substring(1);
                    if (!string.IsNullOrEmpty(t))
                    {
                        if (!t.StartsWith(".")) t = "." + t;
                        if (!list.Contains(t)) list.Add(t);
                        newExt = string.Empty;
                    }
                }
                EditorGUILayout.EndHorizontal();

                // Add prefix (Assets/...)
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Prefix", GUILayout.Width(60));
                newPrefix = EditorGUILayout.TextField(newPrefix, GUILayout.ExpandWidth(true));
                EditorGUILayout.LabelField("e.g. Assets/SubFolder/", EditorStyles.miniLabel, GUILayout.Width(170));
                if (GUILayout.Button("Add", GUILayout.Width(60)))
                {
                    var t = (newPrefix ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(t))
                    {
                        if (!t.EndsWith("/")) t += "/";
                        if (!list.Contains(t)) list.Add(t);
                        newPrefix = string.Empty;
                    }
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical(); // end add-area box

                DrawThinSeparator();

                // Current entries (below)
                int remove = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("", GUILayout.Width(2));
                    list[i] = EditorGUILayout.TextField(list[i], GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("X", GUILayout.Width(20))) remove = i;
                    EditorGUILayout.EndHorizontal();
                }
                if (remove >= 0) list.RemoveAt(remove);
            }

            static void DrawThinSeparator()
            {
                var rect = EditorGUILayout.GetControlRect(false, 1);
                EditorGUI.DrawRect(rect, new Color(1f, 1f, 1f, 0.18f));
            }

            static void RestoreLatest()
            {
                string srcRoot = Path.Combine(FileUtilEx.BackupRoot, "Current");
                if (!Directory.Exists(srcRoot)) { EditorUtility.DisplayDialog("Restore", "No Current/ backup found.", "OK"); return; }
                var ok = Path.Combine(srcRoot, "backup.ok");
                if (!File.Exists(ok)) { EditorUtility.DisplayDialog("Restore", "Backup in progress or not complete.", "OK"); return; }
                foreach (var src in Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories))
                {
                    string rel = MakeRelTo(src, srcRoot).Replace("\\", "/");
                    if (!rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (rel.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || rel.Equals("backup.ok", StringComparison.OrdinalIgnoreCase)) continue;
                    string dst = Path.Combine(FileUtilEx.ProjectRoot, rel);
                    try { Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.Copy(src, dst, true); }
                    catch (Exception ex) { Log.Warn("Restore: failed to copy " + rel + " – " + ex.Message); }
                }
                MainThread.Invoke(() => AssetDatabase.Refresh());
                EditorUtility.DisplayDialog("Restore", "Restore completato.", "OK");
            }

            static string MakeRelTo(string p, string root)
            {
                var pu = new Uri(Path.GetFullPath(p));
                var ru = new Uri(Path.GetFullPath(root) + Path.DirectorySeparatorChar);
                return Uri.UnescapeDataString(ru.MakeRelativeUri(pu).ToString()).Replace('/', Path.DirectorySeparatorChar);
            }
        }

        // Restore preview window: evita sovrascritture accidentali e permette backup prima del restore
        public class RestorePreviewWindow : EditorWindow
        {
            Vector2 _scroll;
            List<string> _files = new List<string>();
            List<bool> _selected = new List<bool>();
            bool _selectAll = true;
            bool _backupBefore = true;
            bool _hideMeta = true;
            Dictionary<string,int> _extCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string,int> _assetTypeCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);

            public static void Open()
            {
                var w = GetWindow<RestorePreviewWindow>(true, "Restore Preview", true);
                w.minSize = new Vector2(480, 320);
                w.LoadFiles();
                w.Show();
            }

            void LoadFiles()
            {
                _files.Clear(); _selected.Clear();
                _extCounts.Clear(); _assetTypeCounts.Clear();
                string srcRoot = Path.Combine(FileUtilEx.BackupRoot, "Current");
                if (!Directory.Exists(srcRoot)) return;
                var ok = Path.Combine(srcRoot, "backup.ok");
                if (!File.Exists(ok)) { EditorUtility.DisplayDialog("Restore", "Backup in progress or not complete.", "OK"); return; }
                foreach (var src in Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories))
                {
                    string rel = MakeRelTo(src, srcRoot).Replace("\\", "/");
                    if (!rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (rel.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || rel.Equals("backup.ok", StringComparison.OrdinalIgnoreCase)) continue;
                    _files.Add(rel);
                    _selected.Add(true);

                    // estensione count
                    string ext = Path.GetExtension(rel);
                    if (string.IsNullOrEmpty(ext)) ext = "(no ext)";
                    if (!_extCounts.ContainsKey(ext)) _extCounts[ext] = 0;
                    _extCounts[ext]++;

                    // per .asset proviamo a riconoscere tipi comuni VRC
                    if (ext.Equals(".asset", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            // leggi una porzione iniziale (file di testo YAML) per cercare indizi
                            using var sr = new StreamReader(src, Encoding.UTF8);
                            char[] buffer = new char[32 * 1024];
                            int read = sr.Read(buffer, 0, buffer.Length);
                            string sample = new string(buffer, 0, Math.Max(0, read));
                            string typeLabel = "Unknown .asset";
                            if (sample.IndexOf("VRCExpressionsMenu", StringComparison.OrdinalIgnoreCase) >= 0) typeLabel = "VRCExpressionsMenu";
                            else if (sample.IndexOf("VRCExpressionParameters", StringComparison.OrdinalIgnoreCase) >= 0) typeLabel = "VRCExpressionParameters";
                            else if (sample.IndexOf("VRCExpression", StringComparison.OrdinalIgnoreCase) >= 0) typeLabel = "VRCExpression (other)";
                            else if (sample.IndexOf("m_Script", StringComparison.OrdinalIgnoreCase) >= 0 && sample.IndexOf("MonoBehaviour", StringComparison.OrdinalIgnoreCase) >= 0) typeLabel = "MonoBehaviour.asset";
                            if (!_assetTypeCounts.ContainsKey(typeLabel)) _assetTypeCounts[typeLabel] = 0;
                            _assetTypeCounts[typeLabel]++;
                        }
                        catch { if (!_assetTypeCounts.ContainsKey("Unknown .asset")) _assetTypeCounts["Unknown .asset"] = 0; _assetTypeCounts["Unknown .asset"]++; }
                    }
                }
            }

            void OnGUI()
            {
                EditorGUILayout.LabelField("Restore Preview", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("Review files to restore. Uncheck items to keep existing project files. You can create a pre-restore backup of current Assets/.", MessageType.Info);

                // Summary
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("Files in backup", EditorStyles.boldLabel);
                if (_extCounts.Count == 0) EditorGUILayout.LabelField("No files found.");
                else
                {
                    EditorGUILayout.BeginHorizontal();
                    // By extension
                    EditorGUILayout.BeginVertical(GUILayout.MaxWidth(220));
                    EditorGUILayout.LabelField("By extension", EditorStyles.miniBoldLabel);
                    foreach (var kv in _extCounts.OrderByDescending(k => k.Value))
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(kv.Key.PadRight(8), GUILayout.Width(80));
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.LabelField(kv.Value.ToString(), GUILayout.Width(40));
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUILayout.EndVertical();

                    // .asset types
                    EditorGUILayout.BeginVertical();
                    if (_assetTypeCounts.Count > 0)
                    {
                        EditorGUILayout.LabelField(".asset types", EditorStyles.miniBoldLabel);
                        foreach (var kv in _assetTypeCounts.OrderByDescending(k => k.Value))
                        {
                            EditorGUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(kv.Key, GUILayout.ExpandWidth(true));
                            EditorGUILayout.LabelField(kv.Value.ToString(), GUILayout.Width(40));
                            EditorGUILayout.EndHorizontal();
                        }
                    }
                    EditorGUILayout.EndVertical();
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndVertical();

                // Toolbar
                EditorGUILayout.BeginHorizontal();
                bool newSelectAll = EditorGUILayout.ToggleLeft("Select All", _selectAll, GUILayout.Width(100));
                if (newSelectAll != _selectAll)
                {
                    _selectAll = newSelectAll;
                    for (int i = 0; i < _selected.Count; i++) _selected[i] = _selectAll;
                }
                if (GUILayout.Button("Refresh", GUILayout.Width(80))) LoadFiles();
                GUILayout.FlexibleSpace();
                _hideMeta = EditorGUILayout.ToggleLeft("Hide .meta", _hideMeta, GUILayout.Width(100));
                _backupBefore = EditorGUILayout.ToggleLeft("Backup current Assets before restore", _backupBefore, GUILayout.Width(260));
                EditorGUILayout.EndHorizontal();

                // File list
                EditorGUILayout.Space(6);
                EditorGUILayout.BeginVertical("box");
                // Build filtered index map
                var visibleIdx = new List<int>(_files.Count);
                for (int i = 0; i < _files.Count; i++)
                {
                    if (_hideMeta && _files[i].EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    visibleIdx.Add(i);
                }
                int selCount = visibleIdx.Count(idx => _selected[idx]);
                EditorGUILayout.LabelField($"Items: {visibleIdx.Count}    Selected: {selCount}", EditorStyles.miniLabel);
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(220));
                if (visibleIdx.Count == 0) EditorGUILayout.LabelField("No files found in Current/ to restore.");
                foreach (var i in visibleIdx)
                {
                    EditorGUILayout.BeginHorizontal();
                    _selected[i] = EditorGUILayout.Toggle(_selected[i], GUILayout.Width(18));
                    EditorGUILayout.LabelField(_files[i], GUILayout.ExpandWidth(true));
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();

                // Bottom bar
                EditorGUILayout.Space(6);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Cancel", GUILayout.Height(22), GUILayout.Width(100))) { Close(); }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Restore selected", GUILayout.Height(22), GUILayout.Width(160))) { DoRestore(); }
                EditorGUILayout.EndHorizontal();
            }

            void DoRestore()
            {
                string srcRoot = Path.Combine(FileUtilEx.BackupRoot, "Current");
                if (!Directory.Exists(srcRoot)) { EditorUtility.DisplayDialog("Restore", "No Current/ backup found.", "OK"); return; }

                // Se richiesto, fai backup corrente degli Assets prima di sovrascrivere
                if (_backupBefore)
                {
                    string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    string safedir = Path.Combine(FileUtilEx.BackupRoot, "PreRestore", stamp);
                    try { foreach (var f in Directory.GetFiles(Path.Combine(FileUtilEx.ProjectRoot, "Assets"), "*", SearchOption.AllDirectories))
                        {
                            var rel = FileUtilEx.MakeRelToProject(f).Replace("\\", "/");
                            var dst = Path.Combine(safedir, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(dst));
                            File.Copy(f, dst, true);
                        }
                        Log.Info("Pre-restore backup created: " + safedir);
                    }
                    catch (Exception ex) { Log.Warn("Pre-restore backup failed: " + ex.Message); if (!EditorUtility.DisplayDialog("Backup failed", "Pre-restore backup failed. Continue restore anyway?", "Yes", "No")) return; }
                }

                int restored = 0;
                for (int i = 0; i < _files.Count; i++)
                {
                    if (!_selected[i]) continue;
                    string rel = _files[i].Replace("/", Path.DirectorySeparatorChar.ToString());
                    string src = Path.Combine(srcRoot, rel);
                    string dst = Path.Combine(FileUtilEx.ProjectRoot, rel);
                    try { Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.Copy(src, dst, true); restored++; }
                    catch (Exception ex) { Log.Warn("Restore: failed to copy " + rel + " – " + ex.Message); }
                }
                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("Restore", $"Restore completed. Files restored: {restored}", "OK");
                Close();
            }
        }
    }
}
#endif


