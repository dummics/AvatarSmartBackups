// Unity 2022.3+ – Editor only
// Avatar Smart Backup – background copy+zip, non-blocking progress, IO throttle, zip-on-change
// Log tag: [ Avatar Backup System ]

#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Reflection;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AvatarSmartBackup
{
    [InitializeOnLoad]
    internal static class MainThread
    {
        static readonly int MainId;
        static MainThread()
        {
            MainId = Thread.CurrentThread.ManagedThreadId;
        }
        public static void Invoke(Action a)
        {
            if (Thread.CurrentThread.ManagedThreadId == MainId) a();
            else EditorApplication.delayCall += () => a();
        }
        public static T InvokeBlocking<T>(Func<T> f)
        {
            if (Thread.CurrentThread.ManagedThreadId == MainId) return f();
            T result = default;
            var ev = new ManualResetEventSlim();
            EditorApplication.delayCall += () => { result = f(); ev.Set(); };
            ev.Wait();
            return result;
        }
    }

    internal static class Log
    {
        const string Tag = "[ Avatar Backup System ] ";
        public static void Info(string msg) => UnityEngine.Debug.Log(Tag + msg);
        public static void Warn(string msg) => UnityEngine.Debug.LogWarning(Tag + msg);
        public static void Err(string msg) => UnityEngine.Debug.LogError(Tag + msg);
    }

    internal static class ProgressUX
    {
        // Wrapper sullo UnityEditor.Progress (non modale). Fallback a nessuna UI se assente.
        static Type _progressType;
        static MethodInfo _start, _report, _finish, _registerCancel, _remove;
        static bool _checked;

        static void Ensure()
        {
            if (_checked) return;
            _checked = true;
            _progressType = Type.GetType("UnityEditor.Progress, UnityEditor");
            if (_progressType == null) return;
            _start = _progressType.GetMethod("Start", new[] { typeof(string), typeof(string), typeof(UnityEditor.Progress.Options) });
            if (_start == null) _start = _progressType.GetMethod("Start", new[] { typeof(string), typeof(string) });
            _report = _progressType.GetMethod("Report", new[] { typeof(int), typeof(float), typeof(string) });
            _finish = _progressType.GetMethod("Finish", new[] { typeof(int) });
            // Try both signatures found across Unity versions: Func<bool> and Action
            _registerCancel = _progressType.GetMethod("RegisterCancelCallback", new[] { typeof(int), typeof(Func<bool>) })
                                ?? _progressType.GetMethod("RegisterCancelCallback", new[] { typeof(int), typeof(Action) });
            _remove = _progressType.GetMethod("Remove", new[] { typeof(int) });
        }

        public static int Start(string title, string desc, bool cancellable, Func<bool> onCancel = null)
        {
            Ensure();
            if (_progressType == null) return -1;
            try
            {
                return MainThread.InvokeBlocking(() =>
                {
                    int id;
                    if (_start != null && _start.GetParameters().Length == 3)
                    {
                        var opts = (UnityEditor.Progress.Options)Enum.Parse(typeof(UnityEditor.Progress.Options),
                            cancellable ? "Managed" : "None");
                        id = (int)_start.Invoke(null, new object[] { title, desc, opts });
                    }
                    else
                    {
                        id = (int)_start.Invoke(null, new object[] { title, desc });
                    }
                    if (onCancel != null && _registerCancel != null)
                    {
                        var pars = _registerCancel.GetParameters();
                        if (pars.Length == 2 && pars[1].ParameterType == typeof(Action))
                        {
                            Action act = () => onCancel();
                            _registerCancel.Invoke(null, new object[] { id, act });
                        }
                        else
                        {
                            _registerCancel.Invoke(null, new object[] { id, onCancel });
                        }
                    }
                    return id;
                });
            }
            catch { return -1; }
        }

        public static void Report(int id, float p, string desc)
        {
            if (id < 0) return;
            Ensure();
            try { MainThread.Invoke(() => _report?.Invoke(null, new object[] { id, Mathf.Clamp01(p), desc })); }
            catch { /* ignore */ }
        }

        public static void Finish(int id)
        {
            if (id < 0) return;
            Ensure();
            try { MainThread.Invoke(() => _finish?.Invoke(null, new object[] { id })); }
            catch { /* ignore */ }
        }
        public static void Remove(int id)
        {
            if (id < 0) return;
            Ensure();
            try { MainThread.Invoke(() => _remove?.Invoke(null, new object[] { id })); }
            catch { /* ignore */ }
        }
    }

    public enum ZipPolicy { OnChange, EveryN, Manual, DailyHHMM, OnPlay }

    [Serializable]
    public class BackupSettings
    {
        public bool autoRunOnLoad = true;
        public int intervalMinutes = 10;
        public int keepSnapshots = 3;
        public bool backupOnPlayEnter = true;

        public bool incVRCAssets = true;
        public bool incAnimControllers = true;
        public bool incAnimationClips = true;
        public bool incScenes = true;
        public bool incMaterials = true;
        public long materialsMaxKB = 1024;
        public bool incDlls = true;
        public long dllsMaxKB = 2048;

        // Folders
        public List<string> includeFolders = new List<string>() { "Assets/" };
        public List<string> excludeFolders = new List<string>() { "Assets/StreamingAssets", "Packages" };

        // ZIP / PERFORMANCE
        public ZipPolicy zipPolicy = ZipPolicy.OnChange;
        public int zipEveryN = 3;
        public int zipDailyHour = 19, zipDailyMinute = 0;
        public bool zipFastest = true;                 // Fastest vs Optimal
        public bool autoThrottle = true;               // Automatic throttling (recommended)
        public int copyMaxMBps = 250;                  // Manual cap (MB/s). 0 = unlimited
        public int zipMaxMBps = 150;                   // Manual cap (MB/s). 0 = unlimited
        public int maxParallelThreads = Math.Max(1, Environment.ProcessorCount);

        public bool saveScenesBeforeBackup = false;    // Avoid blocking by default

        public bool showAdvanced = false;
        public bool useProjectSettings = false;
    }

    internal static class SessionKeys
    {
        public const string IsRunning = "ASB/IsRunning";
        public const string NextRunUtc = "ASB/NextRunUtcTicks";
        public const string LastBackupUtc = "ASB/LastBackupUtcTicks";
        public const string RunsCount = "ASB/RunsCount";
        public const string HookedVRC = "ASB/HookedVRC";
        public const string ZipCounter = "ASB/ZipCounter";
        public const string LastZipDay = "ASB/LastZipDay";
    }

    internal static class Session
    {
        public static bool IsRunning { get => SessionState.GetBool(SessionKeys.IsRunning, false); set => SessionState.SetBool(SessionKeys.IsRunning, value); }
        public static DateTime? NextRunUtc { get => GetDT(SessionKeys.NextRunUtc); set => SetDT(SessionKeys.NextRunUtc, value); }
        public static DateTime? LastBackupUtc { get => GetDT(SessionKeys.LastBackupUtc); set => SetDT(SessionKeys.LastBackupUtc, value); }
        public static int RunsCount { get => SessionState.GetInt(SessionKeys.RunsCount, 0); set => SessionState.SetInt(SessionKeys.RunsCount, value); }
        public static bool HookedVRC { get => SessionState.GetBool(SessionKeys.HookedVRC, false); set => SessionState.SetBool(SessionKeys.HookedVRC, value); }
        public static int ZipCounter { get => SessionState.GetInt(SessionKeys.ZipCounter, 0); set => SessionState.SetInt(SessionKeys.ZipCounter, value); }
        public static int LastZipDay { get => SessionState.GetInt(SessionKeys.LastZipDay, -1); set => SessionState.SetInt(SessionKeys.LastZipDay, value); }

        static DateTime? GetDT(string k)
        {
            var s = SessionState.GetString(k, "");
            if (string.IsNullOrEmpty(s)) return null;
            if (long.TryParse(s, out var ticks) && ticks > 0) return new DateTime(ticks, DateTimeKind.Utc);
            if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            return null;
        }
        static void SetDT(string k, DateTime? v) => SessionState.SetString(k, v.HasValue ? v.Value.ToString("o") : "");
    }

    [Serializable]
    public class ManifestEntry
    {
        public string guid;
        public string relPath;  // Assets/...
        public string md5;
        public long size;
        public long lastWriteUtcTicks;
    }

    [Serializable]
    public class BackupManifest
    {
        public string projectName;
        public string unityVersion;
        public string createdUtc;
        public List<ManifestEntry> entries = new List<ManifestEntry>();
    }

    internal static class FileUtilEx
    {
        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        public static string ProjectName => new DirectoryInfo(ProjectRoot).Name;
        public static string BackupRoot => Path.Combine(ProjectRoot, $"{Sanitize(ProjectName)} Backups");
        public static string Sanitize(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s.Trim(); }
        public static string MakeRelToProject(string abs) { var r = ProjectRoot.Replace('\\', '/'); var p = abs.Replace('\\', '/'); return p.StartsWith(r + "/") ? p.Substring(r.Length + 1) : p; }
        public static string AssetToAbs(string ap) => Path.Combine(ProjectRoot, ap);

        public static string MD5Of(string file)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(file);
            var hash = md5.ComputeHash(stream);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string TryReadGuidFromMeta(string assetAbs)
        {
            try
            {
                string meta = assetAbs + ".meta";
                if (!File.Exists(meta)) return null;
                foreach (var line in File.ReadLines(meta))
                {
                    if (line.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = line.Substring(5).Trim().Trim(':').Trim();
                        return val;
                    }
                }
            }
            catch { }
            return null;
        }

        public static void AtomicReplace(string tmp, string finalPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath));
            if (File.Exists(finalPath))
            {
                try { File.Replace(tmp, finalPath, null, true); }
                catch { File.Delete(finalPath); File.Move(tmp, finalPath); }
            }
            else File.Move(tmp, finalPath);
        }
    }

    internal interface IBackupCollector { IEnumerable<string> CollectAbsolutePaths(BackupSettings s); }

    internal static class CollectHelpers
    {
        public static bool PassesFolderFilters(string assetPath, BackupSettings s)
        {
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return false;
            if (s.includeFolders != null && s.includeFolders.Count > 0)
            {
                bool any = s.includeFolders.Any(f => assetPath.StartsWith(f, StringComparison.OrdinalIgnoreCase));
                if (!any) return false;
            }
            if (s.excludeFolders != null && s.excludeFolders.Any(f => assetPath.StartsWith(f, StringComparison.OrdinalIgnoreCase))) return false;
            return true;
        }
    }

    // === Collectors (come prima) ===
    internal class VRCAssetsCollector : IBackupCollector
    {
        static readonly string[] CommonNames =
        { "VRCExpressionsMenu.asset", "VRCExpressionParameters.asset", "Expressions Menu.asset", "Expression Parameters.asset" };

        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incVRCAssets) yield break;
            string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            foreach (var cand in CommonNames)
            {
                foreach (var p in Directory.GetFiles(root, cand, SearchOption.AllDirectories))
                {
                    var rel = FileUtilEx.MakeRelToProject(p).Replace("\\", "/");
                    if (CollectHelpers.PassesFolderFilters(rel, s)) yield return p;
                }
            }

            foreach (var p in Directory.GetFiles(root, "*.asset", SearchOption.AllDirectories))
            {
                string fn = Path.GetFileName(p);
                if (fn.IndexOf("VRCExpression", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var rel = FileUtilEx.MakeRelToProject(p).Replace("\\", "/");
                    if (CollectHelpers.PassesFolderFilters(rel, s)) yield return p;
                }
            }
        }
    }
    internal class ControllersCollector : IBackupCollector
    {
        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incAnimControllers) yield break;
            string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            foreach (var abs in Directory.GetFiles(root, "*.controller", SearchOption.AllDirectories))
            {
                string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\", "/");
                if (CollectHelpers.PassesFolderFilters(rel, s)) yield return abs;
            }
        }
    }
    internal class AnimClipsCollector : IBackupCollector
    {
        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incAnimationClips) yield break;
            string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            foreach (var abs in Directory.GetFiles(root, "*.anim", SearchOption.AllDirectories))
            {
                string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\", "/");
                if (CollectHelpers.PassesFolderFilters(rel, s)) yield return abs;
            }
        }
    }
    internal class ScenesCollector : IBackupCollector
    {
        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incScenes) yield break;
            string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            foreach (var abs in Directory.GetFiles(root, "*.unity", SearchOption.AllDirectories))
            {
                string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\", "/");
                if (CollectHelpers.PassesFolderFilters(rel, s)) yield return abs;
            }
        }
    }
    internal class MaterialsCollector : IBackupCollector
    {
        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incMaterials) yield break;
            long maxBytes = Math.Max(10, s.materialsMaxKB) * 1024L;
            string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            foreach (var abs in Directory.GetFiles(root, "*.mat", SearchOption.AllDirectories))
            {
                string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\", "/");
                if (!CollectHelpers.PassesFolderFilters(rel, s)) continue;
                if (new FileInfo(abs).Length <= maxBytes) yield return abs;
            }
        }
    }
    internal class DllCollector : IBackupCollector
    {
        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incDlls) yield break;
            long maxBytes = Math.Max(128, s.dllsMaxKB) * 1024L;
            string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            foreach (var p in Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories))
            {
                string rel = FileUtilEx.MakeRelToProject(p).Replace("\\", "/");
                if (!CollectHelpers.PassesFolderFilters(rel, s)) continue;
                var fi = new FileInfo(p);
                if (fi.Length <= maxBytes) yield return p;
            }
        }
    }

    internal static class IOThrottle
    {
        // Copia stream -> stream con throttle (MB/s). 0 = illimitato.
        public static void CopyStreamThrottled(Stream src, Stream dst, int bufferBytes, int maxMBps, CancellationToken ct)
        {
            byte[] buffer = new byte[bufferBytes];
            var sw = Stopwatch.StartNew();
            long total = 0;
            double maxBps = maxMBps <= 0 ? double.PositiveInfinity : maxMBps * 1024.0 * 1024.0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int r = src.Read(buffer, 0, buffer.Length);
                if (r <= 0) break;
                dst.Write(buffer, 0, r);
                total += r;

                if (maxMBps > 0)
                {
                    double elapsed = sw.Elapsed.TotalSeconds;
                    if (elapsed > 0)
                    {
                        double bps = total / elapsed;
                        if (bps > maxBps)
                        {
                            // tempo desiderato per scrivere 'total' a maxBps
                            double desired = total / maxBps;
                            int sleepMs = (int)Math.Max(1, (desired - elapsed) * 1000.0);
                            Thread.Sleep(sleepMs);
                        }
                    }
                }
            }
        }
    }

    internal static class BackupManager
    {
        static readonly IBackupCollector[] Collectors = new IBackupCollector[]
        { new VRCAssetsCollector(), new ControllersCollector(), new AnimClipsCollector(), new ScenesCollector(), new MaterialsCollector(), new DllCollector() };

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

        // Anti re-entrancy guards
        static readonly SemaphoreSlim _one = new SemaphoreSlim(1, 1);
        static int _busy;

        // API pubblica
        public static void RunBackupNow(BackupSettings s, bool showToast = true, string reason = null) => _ = RunBackupNowAsync(s, showToast, reason);

        public static async Task RunBackupNowAsync(BackupSettings s, bool showToast, string reason)
        {
            try
            {
                if (Interlocked.Exchange(ref _busy, 1) == 1)
                {
                    Log.Warn("Backup already running – skipped.");
                    return;
                }
                await _one.WaitAsync();

                if (s.saveScenesBeforeBackup) SaveOpenScenesIfDirty();

                // 1) Scan e decisione (no UI)
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in Collectors)
                    foreach (var abs in c.CollectAbsolutePaths(s))
                        if (File.Exists(abs)) set.Add(abs);

                // solo Assets/
                set.RemoveWhere(p => !FileUtilEx.MakeRelToProject(p).Replace("\\", "/").StartsWith("Assets/", StringComparison.OrdinalIgnoreCase));
                // escludi formati pesanti/externals
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

                foreach (var abs in set)
                {
                    string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\", "/");
                    string guid = FileUtilEx.TryReadGuidFromMeta(abs) ?? string.Empty;
                    var fi = new FileInfo(abs);

                    bool needsCopy = true;
                    string md5 = null;
                    if (prev != null)
                    {
                        var prevEntry = (!string.IsNullOrEmpty(guid))
                            ? prev.entries.FirstOrDefault(x => x.guid == guid)
                            : prev.entries.FirstOrDefault(x => x.relPath == rel);
                        if (prevEntry != null && prevEntry.size == fi.Length && prevEntry.lastWriteUtcTicks == fi.LastWriteTimeUtc.Ticks)
                        {
                            needsCopy = false;
                            md5 = prevEntry.md5;
                        }
                    }
                    // MD5 deferred to background step

                    man.entries.Add(new ManifestEntry
                    {
                        guid = guid,
                        relPath = rel,
                        size = fi.Length,
                        lastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                        md5 = md5
                    });

                    string dst = Path.Combine(CurrentDir, rel);
                    if (needsCopy) { copyJobs.Add((abs, dst)); copied++; } else skipped++;

                    // copia .meta sempre (leggero)
                    var meta = abs + ".meta";
                    if (File.Exists(meta)) copyJobs.Add((meta, Path.Combine(CurrentDir, rel + ".meta")));
                }

                // 2) Copie (background, non modale, throttled)
                if (copyJobs.Count > 0)
                {
                    using var cts = new CancellationTokenSource();
                    int progId = ProgressUX.Start("Avatar Smart Backup", "Copying files", cancellable: true, onCancel: () => { cts.Cancel(); return true; });
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
                bool shouldZip = ShouldCreateZip(s, hadChanges: copied > 0);
                if (shouldZip)
                {
                    var ctsZip = new CancellationTokenSource();
                    try { await CreateZipAsync(s, ctsZip.Token, ctsZip); }
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
            }
        }

        static void PruneRemoved(BackupManifest newMan)
        {
            var keep = new HashSet<string>(newMan.entries.Select(e => e.relPath), StringComparer.OrdinalIgnoreCase);
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
                case ZipPolicy.EveryN:
                    if (hadChanges) { Session.ZipCounter = Session.ZipCounter + 1; }
                    if (Session.ZipCounter >= Mathf.Max(1, s.zipEveryN))
                    { Session.ZipCounter = 0; return true; }
                    return false;
                case ZipPolicy.DailyHHMM:
                    if (!hadChanges) return false;
                    var now = DateTime.Now;
                    bool newDay = Session.LastZipDay != now.DayOfYear;
                    bool pastTime = now.TimeOfDay >= new TimeSpan(s.zipDailyHour, s.zipDailyMinute, 0);
                    if (newDay && pastTime) { Session.LastZipDay = now.DayOfYear; return true; }
                    return false;
                case ZipPolicy.OnPlay:
                case ZipPolicy.Manual:
                default:
                    return false;
            }
        }

        static int EffectiveCopyMBps(BackupSettings s)
        {
            // Heuristic: NVMe can handle high throughput; keep reasonable cap when auto
            return s.autoThrottle ? 250 : s.copyMaxMBps;
        }
        static int EffectiveZipMBps(BackupSettings s)
        {
            return s.autoThrottle ? 150 : s.zipMaxMBps;
        }

        static string HumanMB(long bytes)
        {
            if (bytes <= 0) return "0 MB";
            double mb = bytes / (1024.0 * 1024.0);
            if (mb < 1024.0) return mb.ToString("0.0") + " MB";
            double gb = mb / 1024.0;
            return gb.ToString("0.00") + " GB";
        }

        static Task CreateZipAsync(BackupSettings s, CancellationToken ct, CancellationTokenSource ctsForUi)
        {
            return Task.Run(() =>
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string zipPath = Path.Combine(ArchiveDir, $"{FileUtilEx.Sanitize(FileUtilEx.ProjectName)}_{stamp}.zip");
                Directory.CreateDirectory(ArchiveDir);
                int progId = ProgressUX.Start("Avatar Smart Backup", "Creating archive…", cancellable: true, onCancel: () => { ctsForUi.Cancel(); return true; });
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

            public static void StartTimerIfNeeded(BackupSettings s)
            {
                if (!Session.IsRunning)
                {
                    Session.IsRunning = true;
                    ScheduleNext(DateTime.UtcNow.AddMinutes(Math.Max(1, s.intervalMinutes)));
                    Log.Info("Timer started.");
                }
                else if (Session.NextRunUtc == null)
                {
                    ScheduleNext(DateTime.UtcNow.AddMinutes(Math.Max(1, s.intervalMinutes)));
                }
            }
            public static void PauseTimer() { Session.IsRunning = false; Log.Info("Timer paused."); }
            static void ScheduleNext(DateTime utc) => Session.NextRunUtc = utc;

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
                    BackupManager.RunBackupNow(s, showToast: true, reason: "timer");
                    ScheduleNext(now.AddMinutes(Math.Max(1, s.intervalMinutes)));
                }
            }

            [InitializeOnLoadMethod]
            static void HookPlaymodeShot()
            {
                var s = BackupManager.LoadSettings();
                EditorApplication.playModeStateChanged += (state) =>
                {
                    if (state == PlayModeStateChange.ExitingEditMode && s.backupOnPlayEnter)
                        BackupManager.RunBackupNow(s, showToast: false, reason: "play-enter");
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

            static void OnVRC0() => BackupManager.RunBackupNow(GetSettingsCached(), reason: "vrchat-preprocess");
            static void OnVRC1(object _) => BackupManager.RunBackupNow(GetSettingsCached(), reason: "vrchat-preprocess");
            static void OnVRCPreprocessAvatar(object _a, object _b) => BackupManager.RunBackupNow(GetSettingsCached(), reason: "vrchat-preprocess");
        }

        public class AvatarSmartBackupWindow : EditorWindow
        {
            Vector2 _scroll;
            BackupSettings _settings;

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
                EditorGUILayout.HelpBox("Automatic background backups. Non-blocking progress. Zips only when needed.", MessageType.Info);

                EditorGUILayout.BeginVertical("box");
                bool running = Session.IsRunning;
                if (GUILayout.Button(new GUIContent($"Automatic backups: {(running ? "On" : "Off")}", "Toggle the background timer that triggers backups.")))
                {
                    if (running) TimerService.PauseTimer(); else TimerService.StartTimerIfNeeded(_settings);
                }
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Interval (min)", GUILayout.Width(110));
                _settings.intervalMinutes = Mathf.Clamp(EditorGUILayout.IntField(_settings.intervalMinutes, GUILayout.Width(60)), 1, 240);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();

                EditorGUILayout.LabelField($"Next: {Session.NextRunUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "--"}    Last: {Session.LastBackupUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "never"}");

                // Pulsanti verticali
                if (GUILayout.Button(new GUIContent("Backup Now", "Start a backup immediately (non-blocking)."))) BackupManager.RunBackupNow(_settings, showToast: true, reason: "manual");
                if (GUILayout.Button(new GUIContent("Open Backup Folder", "Open the folder where backups are stored."))) EditorUtility.RevealInFinder(FileUtilEx.BackupRoot);

                _settings.showAdvanced = EditorGUILayout.Foldout(_settings.showAdvanced, "Advanced Settings");
                if (_settings.showAdvanced)
                {
                    EditorGUILayout.BeginVertical("box");

                    _settings.useProjectSettings = EditorGUILayout.ToggleLeft(new GUIContent("Use project-local settings (override)", "Store settings in ProjectSettings so they travel with the project."), _settings.useProjectSettings);
                    BackupManager.SaveSettings(_settings); BackupManager.TimerService.InvalidateSettingsCache(); _settings = BackupManager.LoadSettings();

                    EditorGUILayout.Space(6);
                    EditorGUILayout.LabelField("Zip Policy", EditorStyles.boldLabel);
                    _settings.zipPolicy = (ZipPolicy)EditorGUILayout.EnumPopup(new GUIContent("Mode", "When to create zip snapshots."), _settings.zipPolicy);
                    if (_settings.zipPolicy == ZipPolicy.EveryN)
                        _settings.zipEveryN = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Every N backups", "Create a zip after this many backups with changes."), _settings.zipEveryN), 1, 50);
                    if (_settings.zipPolicy == ZipPolicy.DailyHHMM)
                    {
                        _settings.zipDailyHour = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Hour (0-23)", "Daily zip time (hour)."), _settings.zipDailyHour), 0, 23);
                        _settings.zipDailyMinute = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Minutes", "Daily zip time (minutes)."), _settings.zipDailyMinute), 0, 59);
                    }
                    _settings.zipFastest = EditorGUILayout.ToggleLeft(new GUIContent("Compression level: Fastest (faster)", "Fastest is quicker but larger archives. Untick for Optimal (smaller, slower)."), _settings.zipFastest);

                    EditorGUILayout.Space(6);
                    EditorGUILayout.LabelField("Performance", EditorStyles.boldLabel);
                    _settings.autoThrottle = EditorGUILayout.ToggleLeft(new GUIContent("Auto throttle (recommended)", "Automatically caps IO speed to keep the editor responsive."), _settings.autoThrottle);
                    _settings.maxParallelThreads = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Max parallel threads", "Number of concurrent copy/hash tasks."), _settings.maxParallelThreads), 1, Math.Max(1, System.Environment.ProcessorCount));
                    _settings.saveScenesBeforeBackup = EditorGUILayout.ToggleLeft(new GUIContent("Save open scenes before backup", "Saves scenes if dirty before backup. May block briefly."), _settings.saveScenesBeforeBackup);

                    EditorGUILayout.Space(6);
                    EditorGUILayout.LabelField("What to include", EditorStyles.boldLabel);
                    _settings.incVRCAssets = EditorGUILayout.ToggleLeft(new GUIContent("VRC Expressions (.asset)", "Common VRC expression assets and similarly named .asset files."), _settings.incVRCAssets);
                    _settings.incAnimControllers = EditorGUILayout.ToggleLeft(new GUIContent("Animator Controllers (.controller)", "Animator controller assets."), _settings.incAnimControllers);
                    _settings.incAnimationClips = EditorGUILayout.ToggleLeft(new GUIContent("Animation Clips (.anim)", "Animation clip assets."), _settings.incAnimationClips);
                    _settings.incScenes = EditorGUILayout.ToggleLeft(new GUIContent("Scenes (.unity)", "Unity scene files."), _settings.incScenes);
                    _settings.incMaterials = EditorGUILayout.ToggleLeft(new GUIContent("Materials (.mat) under size limit", "Small material files. Larger ones are skipped by threshold."), _settings.incMaterials);
                    using (new EditorGUI.DisabledScope(!_settings.incMaterials))
                    {
                        long v = EditorGUILayout.LongField(new GUIContent("Material size limit (KB)", "Skip materials larger than this size."), _settings.materialsMaxKB);
                        v = Math.Max(10L, Math.Min(v, 100L * 1024L));
                        _settings.materialsMaxKB = v;
                    }
                    _settings.incDlls = EditorGUILayout.ToggleLeft(new GUIContent("Plugin .dll in Assets (under size limit)", "Small .dll files under Assets/ (useful for simple plugins)."), _settings.incDlls);
                    using (new EditorGUI.DisabledScope(!_settings.incDlls))
                    {
                        long v = EditorGUILayout.LongField(new GUIContent("DLL size limit (KB)", "Skip DLLs larger than this size."), _settings.dllsMaxKB);
                        v = Math.Max(128L, Math.Min(v, 1024L * 10L));
                        _settings.dllsMaxKB = v;
                    }

                    EditorGUILayout.Space(6);
                    EditorGUILayout.LabelField("Folders", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("Include (prefixes, e.g., 'Assets/Avatars/'):");
                    DrawStringListVertical(_settings.includeFolders, "Add folder");
                    EditorGUILayout.LabelField("Exclude:");
                    DrawStringListVertical(_settings.excludeFolders, "Add exclusion");

                    EditorGUILayout.EndVertical();
                }

                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("Restore (safe)", EditorStyles.boldLabel);
                if (GUILayout.Button("Preview & Restore latest backup", GUILayout.Height(22)))
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
                // --- summary box: counts per extension and .asset types
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("Files in backup", EditorStyles.boldLabel);
                if (_extCounts.Count == 0) EditorGUILayout.LabelField("No files found.");
                else
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.BeginVertical(GUILayout.MaxWidth(200));
                    foreach (var kv in _extCounts.OrderByDescending(k => k.Value))
                    {
                        EditorGUILayout.LabelField($"{kv.Key}", GUILayout.Width(80));
                        EditorGUILayout.LabelField(kv.Value.ToString(), GUILayout.Width(40));
                    }
                    EditorGUILayout.EndVertical();

                    // dettagli per .asset
                    EditorGUILayout.BeginVertical();
                    if (_assetTypeCounts.Count > 0)
                    {
                        EditorGUILayout.LabelField(".asset types:", EditorStyles.boldLabel);
                        foreach (var kv in _assetTypeCounts.OrderByDescending(k => k.Value))
                            EditorGUILayout.LabelField($"{kv.Key}: {kv.Value}");
                    }
                    EditorGUILayout.EndVertical();
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndVertical();

                EditorGUILayout.BeginHorizontal();
                bool newSelectAll = EditorGUILayout.ToggleLeft("Select All", _selectAll, GUILayout.Width(100));
                if (newSelectAll != _selectAll)
                {
                    _selectAll = newSelectAll;
                    for (int i = 0; i < _selected.Count; i++) _selected[i] = _selectAll;
                }
                if (GUILayout.Button("Refresh", GUILayout.Width(80))) LoadFiles();
                GUILayout.FlexibleSpace();
                _backupBefore = EditorGUILayout.ToggleLeft("Backup current Assets before restore", _backupBefore, GUILayout.Width(240));
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(6);
                EditorGUILayout.BeginVertical("box");
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                if (_files.Count == 0) EditorGUILayout.LabelField("No files found in Current/ to restore.");
                for (int i = 0; i < _files.Count; i++)
                {
                    // Honor select-all only when toggled above (no per-frame forcing)
                    EditorGUILayout.BeginHorizontal();
                    _selected[i] = EditorGUILayout.Toggle(_selected[i], GUILayout.Width(18));
                    EditorGUILayout.LabelField(_files[i], GUILayout.ExpandWidth(true));
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();

                EditorGUILayout.Space(6);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Cancel", GUILayout.Height(22))) { Close(); }
                if (GUILayout.Button("Restore selected", GUILayout.Height(22))) { DoRestore(); }
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


