// / 2022.x – Editor only Backup Script for FLOWYE

#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Reflection;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AvatarSmartBackup
{
    // ============= SETTINGS (persistenti su disco) =====================

    [Serializable]
    public class BackupSettings
    {
        // Scheduling / autostart
        public bool autoRunOnLoad = true;
        public int intervalMinutes = 10;            // default richiesto
        public int keepSnapshots = 3;               // 1 = solo Current, >1 = anche ZIP in Archive
        public bool backupOnPlayEnter = true;       // “safety shot” quando si entra in Play

        // Cosa includere (di default tutto attivo)
        public bool incVRCAssets = true;            // VRCExpressionsMenu / VRCExpressionParameters (.asset)
        public bool incAnimControllers = true;      // .controller
        public bool incAnimationClips = true;       // .anim
        public bool incScenes = true;               // .unity
        public bool incMaterials = true;            // .mat leggeri
        public long materialsMaxKB = 1024;          // soglia per materiali leggeri
        public bool incDlls = true;                 // .dll dentro Assets (plugin leggeri)
        public long dllsMaxKB = 2048;

        // Filtri cartelle (facile da estendere in futuro)
        public List<string> includeFolders = new List<string>(); // vuoto = tutto Assets
        public List<string> excludeFolders = new List<string>()  // default exclude classici
        {
            "Assets/StreamingAssets", "Assets/__Backups", "Assets/Plugins/EditorOnly"
        };

        // UI
        public bool showAdvanced = false;
        public bool zipSnapshots = true;            // crea ZIP se keepSnapshots > 1
    }

    // ============= SESSION STATE (NON persiste al riavvio di Unity) =========
    internal static class SessionKeys
    {
        public const string IsRunning = "ASB/IsRunning";
        public const string NextRunUtcTicks = "ASB/NextRunUtcTicks";
        public const string LastBackupUtcTicks = "ASB/LastBackupUtcTicks";
        public const string RunsCount = "ASB/RunsCount";
        public const string HookedVRC = "ASB/HookedVRC";
    }

    internal static class Session
    {
        public static bool IsRunning
        {
            get => SessionState.GetBool(SessionKeys.IsRunning, false);
            set => SessionState.SetBool(SessionKeys.IsRunning, value);
        }
        public static DateTime? NextRunUtc
        {
            get
            {
                var s = SessionState.GetString(SessionKeys.NextRunUtcTicks, "");
                if (string.IsNullOrEmpty(s)) return null;
                // Back-compat: could be ticks or ISO8601
                if (long.TryParse(s, out var ticks) && ticks > 0)
                    return new DateTime(ticks, DateTimeKind.Utc);
                if (DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var dt))
                    return dt.ToUniversalTime();
                return null;
            }
            set
            {
                if (value.HasValue)
                    SessionState.SetString(SessionKeys.NextRunUtcTicks, value.Value.ToString("o"));
                else
                    SessionState.SetString(SessionKeys.NextRunUtcTicks, "");
            }
        }
        public static DateTime? LastBackupUtc
        {
            get
            {
                var s = SessionState.GetString(SessionKeys.LastBackupUtcTicks, "");
                if (string.IsNullOrEmpty(s)) return null;
                if (long.TryParse(s, out var ticks) && ticks > 0)
                    return new DateTime(ticks, DateTimeKind.Utc);
                if (DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var dt))
                    return dt.ToUniversalTime();
                return null;
            }
            set
            {
                if (value.HasValue)
                    SessionState.SetString(SessionKeys.LastBackupUtcTicks, value.Value.ToString("o"));
                else
                    SessionState.SetString(SessionKeys.LastBackupUtcTicks, "");
            }
        }
        public static int RunsCount
        {
            get => SessionState.GetInt(SessionKeys.RunsCount, 0);
            set => SessionState.SetInt(SessionKeys.RunsCount, value);
        }

        public static bool HookedVRC
        {
            get => SessionState.GetBool(SessionKeys.HookedVRC, false);
            set => SessionState.SetBool(SessionKeys.HookedVRC, value);
        }
    }

    // ============= MANIFEST / REGISTRY (per move/delete, incrementale) =======

    [Serializable]
    public class ManifestEntry
    {
        public string guid;     // robusto su move/rename
        public string relPath;  // Assets/... relativo root progetto
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

    // ============= FILE UTILS ================================================

    internal static class FileUtilEx
    {
        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        public static string ProjectName => new DirectoryInfo(ProjectRoot).Name;

        public static string BackupRoot
        {
            get
            {
                string name = $"{ProjectName} Backups";
                return Path.Combine(ProjectRoot, name);
            }
        }

        public static string MakeRelToProject(string abs)
        {
            var root = ProjectRoot.Replace('\\','/');
            var p = abs.Replace('\\','/');
            if (p.StartsWith(root + "/")) return p.Substring(root.Length + 1);
            return p;
        }

        public static string AssetToAbs(string assetPath) => Path.Combine(ProjectRoot, assetPath);

        public static void AtomicReplace(string tmp, string finalPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath));
            if (File.Exists(finalPath))
            {
                try { File.Replace(tmp, finalPath, null, true); }
                catch { File.Delete(finalPath); File.Move(tmp, finalPath); }
            }
            else
            {
                File.Move(tmp, finalPath);
            }
        }

        public static string MD5Of(string file)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(file);
            var hash = md5.ComputeHash(stream);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static void SafeCopy(string src, string dst, bool preserveTime = true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            string tmp = dst + ".tmp";
            File.Copy(src, tmp, true);
            if (preserveTime)
            {
                var info = new FileInfo(src);
                File.SetLastWriteTimeUtc(tmp, info.LastWriteTimeUtc);
            }
            AtomicReplace(tmp, dst);
        }

        public static void ZipDirectory(string sourceDir, string zipPath)
        {
            string tmp = zipPath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(sourceDir, tmp, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Move(tmp, zipPath);
        }
    }

    // ============= COLLECTOR modulari ========================================

    internal interface IBackupCollector
    {
        IEnumerable<string> CollectAbsolutePaths(BackupSettings s);
    }

    // Helpers comuni
    internal static class CollectHelpers
    {
        public static bool PassesFolderFilters(string assetPath, BackupSettings s)
        {
            // includeFolders vuoto => tutto; altrimenti almeno un prefisso deve combaciare
            if (s.includeFolders != null && s.includeFolders.Count > 0)
            {
                bool any = s.includeFolders.Any(f => assetPath.StartsWith(f, StringComparison.OrdinalIgnoreCase));
                if (!any) return false;
            }
            if (s.excludeFolders != null && s.excludeFolders.Any(f => assetPath.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                return false;
            return true;
        }
    }

    internal class VRCAssetsCollector : IBackupCollector
    {
        // Cerca file tipici: VRCExpressionsMenu.asset / VRCExpressionParameters.asset (+ varianti nome)
        static readonly string[] CommonNames =
        {
            "VRCExpressionsMenu.asset", "VRCExpressionParameters.asset",
            "Expressions Menu.asset", "Expression Parameters.asset"
        };

        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incVRCAssets) yield break;

            string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            foreach (var cand in CommonNames)
            {
                foreach (var p in Directory.GetFiles(root, cand, SearchOption.AllDirectories))
                {
                    string rel = FileUtilEx.MakeRelToProject(p).Replace("\\","/");
                    if (CollectHelpers.PassesFolderFilters(rel, s)) yield return p;
                }
            }

            // fallback euristico: .asset che contengono "VRCExpression"
            foreach (var p in Directory.GetFiles(root, "*.asset", SearchOption.AllDirectories))
            {
                string fn = Path.GetFileName(p);
                if (fn.IndexOf("VRCExpression", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string rel = FileUtilEx.MakeRelToProject(p).Replace("\\","/");
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
            string[] guids = AssetDatabase.FindAssets("t:AnimatorController");
            foreach (var g in guids)
            {
                string ap = AssetDatabase.GUIDToAssetPath(g);
                if (!CollectHelpers.PassesFolderFilters(ap, s)) continue;
                string abs = FileUtilEx.AssetToAbs(ap);
                if (File.Exists(abs)) yield return abs;
            }
        }
    }

    internal class AnimClipsCollector : IBackupCollector
    {
        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incAnimationClips) yield break;
            string[] guids = AssetDatabase.FindAssets("t:AnimationClip");
            foreach (var g in guids)
            {
                string ap = AssetDatabase.GUIDToAssetPath(g);
                if (!CollectHelpers.PassesFolderFilters(ap, s)) continue;
                string abs = FileUtilEx.AssetToAbs(ap);
                if (File.Exists(abs) && ap.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                    yield return abs;
            }
        }
    }

    internal class ScenesCollector : IBackupCollector
    {
        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incScenes) yield break;
            string[] guids = AssetDatabase.FindAssets("t:SceneAsset");
            foreach (var g in guids)
            {
                string ap = AssetDatabase.GUIDToAssetPath(g);
                if (!CollectHelpers.PassesFolderFilters(ap, s)) continue;
                string abs = FileUtilEx.AssetToAbs(ap);
                if (File.Exists(abs) && ap.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    yield return abs;
            }
        }
    }

    internal class MaterialsCollector : IBackupCollector
    {
        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incMaterials) yield break;
            long maxBytes = Math.Max(10, s.materialsMaxKB) * 1024L;
            string[] guids = AssetDatabase.FindAssets("t:Material");
            foreach (var g in guids)
            {
                string ap = AssetDatabase.GUIDToAssetPath(g);
                if (!CollectHelpers.PassesFolderFilters(ap, s)) continue;
                string abs = FileUtilEx.AssetToAbs(ap);
                if (File.Exists(abs) && new FileInfo(abs).Length <= maxBytes)
                    yield return abs;
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
                string rel = FileUtilEx.MakeRelToProject(p).Replace("\\","/");
                if (!CollectHelpers.PassesFolderFilters(rel, s)) continue;
                var fi = new FileInfo(p);
                if (fi.Length <= maxBytes) yield return p;
            }
        }
    }

    // ============= BACKUP MANAGER ============================================

    internal static class BackupManager
    {
        static readonly IBackupCollector[] Collectors = new IBackupCollector[]
        {
            new VRCAssetsCollector(),
            new ControllersCollector(),
            new AnimClipsCollector(),
            new ScenesCollector(),
            new MaterialsCollector(),
            new DllCollector()
        };

        const string SettingsPath = "ProjectSettings/AvatarBackupSettings.json";

        public static BackupSettings LoadSettings()
        {
            try
            {
                string p = Path.Combine(FileUtilEx.ProjectRoot, SettingsPath);
                if (File.Exists(p))
                    return JsonUtility.FromJson<BackupSettings>(File.ReadAllText(p, Encoding.UTF8));
            }
            catch { }
            return new BackupSettings();
        }

        public static void SaveSettings(BackupSettings s)
        {
            try
            {
                string p = Path.Combine(FileUtilEx.ProjectRoot, SettingsPath);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, JsonUtility.ToJson(s, true), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ASB] Impossibile salvare impostazioni: " + ex.Message);
            }
        }

        static string CurrentDir => Path.Combine(FileUtilEx.BackupRoot, "Current");
        static string ArchiveDir => Path.Combine(FileUtilEx.BackupRoot, "Archive");
        static string ManifestPath => Path.Combine(CurrentDir, "manifest.json");

        static BackupManifest LoadManifest()
        {
            try
            {
                if (!File.Exists(ManifestPath)) return null;
                var json = File.ReadAllText(ManifestPath, Encoding.UTF8);
                return JsonUtility.FromJson<BackupManifest>(json);
            }
            catch { return null; }
        }

        static void WriteManifest(BackupManifest m)
        {
            string tmp = ManifestPath + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(m, true), Encoding.UTF8);
            FileUtilEx.AtomicReplace(tmp, ManifestPath);
        }

        static void WriteOkFlag()
        {
            string ok = Path.Combine(CurrentDir, "backup.ok");
            File.WriteAllText(ok + ".tmp", DateTime.UtcNow.ToString("o"));
            FileUtilEx.AtomicReplace(ok + ".tmp", ok);
        }

        static void PruneRemoved(BackupManifest newMan)
        {
            var keep = new HashSet<string>(newMan.entries.Select(e => e.relPath), StringComparer.OrdinalIgnoreCase);
            keep.Add("manifest.json");
            keep.Add("backup.ok");
            var allFiles = Directory.Exists(CurrentDir)
                ? Directory.GetFiles(CurrentDir, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();

            foreach (var abs in allFiles)
            {
                string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\","/");
                // MakeRelToProject su Current produce "<ProjName> Backups/Current/..." – usiamo un metodo relativo a Current
                rel = MakeRelTo(abs, CurrentDir).Replace("\\","/");
                if (!keep.Contains(rel))
                {
                    try { File.Delete(abs); } catch { }
                }
            }

            // rimuovi cartelle vuote
            foreach (var d in Directory.GetDirectories(CurrentDir, "*", SearchOption.AllDirectories))
            {
                if (!Directory.EnumerateFileSystemEntries(d).Any())
                    try { Directory.Delete(d, true); } catch { }
            }
        }

        static string MakeRelTo(string path, string root)
        {
            var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var pu = new Uri(p);
            var ru = new Uri(r);
            return Uri.UnescapeDataString(ru.MakeRelativeUri(pu).ToString()).Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        }

        // Salva tutte le scene aperte se “dirty”
        static void SaveOpenScenesIfDirty()
        {
            bool anyDirty = false;
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scn = EditorSceneManager.GetSceneAt(i);
                if (scn.isDirty) { anyDirty = true; break; }
            }
            if (anyDirty)
            {
                EditorSceneManager.SaveOpenScenes();
            }
        }

        public static void RunBackupNow(BackupSettings s, bool showToast = true, string reason = null)
        {
            try
            {
                // 1) salva scene correnti (richiesto)
                SaveOpenScenesIfDirty();

                // 2) raccogli file (modulare)
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in Collectors)
                    foreach (var abs in c.CollectAbsolutePaths(s))
                        if (File.Exists(abs)) set.Add(abs);

                // escludi formati pesanti che non vogliamo
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

                int copied = 0, skipped = 0;

                foreach (var abs in set)
                {
                    string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\","/");
                    string guid = AssetDatabase.AssetPathToGUID(rel);

                    var fi = new FileInfo(abs);
                    string md5 = FileUtilEx.MD5Of(abs);

                    var e = new ManifestEntry
                    {
                        guid = guid,
                        relPath = rel,
                        md5 = md5,
                        size = fi.Length,
                        lastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks
                    };
                    man.entries.Add(e);

                    bool needsCopy = true;
                    if (prev != null)
                    {
                        // preferisci match per GUID (move/rename) se presente, altrimenti per path
                        var prevEntry = (!string.IsNullOrEmpty(guid))
                            ? prev.entries.FirstOrDefault(x => x.guid == guid)
                            : prev.entries.FirstOrDefault(x => x.relPath == rel);

                        if (prevEntry != null && prevEntry.md5 == md5 && prevEntry.size == fi.Length)
                            needsCopy = false;
                    }

                    // copia (mantiene stessa struttura Assets/...)
                    string dst = Path.Combine(CurrentDir, rel);
                    FileUtilEx.SafeCopy(abs, dst);
                    if (needsCopy) copied++; else skipped++;

                    // copia anche .meta
                    var meta = abs + ".meta";
                    if (File.Exists(meta))
                    {
                        string mrel = rel + ".meta";
                        string mdst = Path.Combine(CurrentDir, mrel);
                        FileUtilEx.SafeCopy(meta, mdst);
                    }
                }

                // 3) prune di file rimossi
                PruneRemoved(man);

                // 4) scrivi manifest e flag ok (atomico)
                WriteManifest(man);
                WriteOkFlag();

                // 5) snapshot ZIP + retention
                if (s.keepSnapshots > 1 && s.zipSnapshots)
                {
                    string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    string zip = Path.Combine(ArchiveDir, $"{Sanitize(FileUtilEx.ProjectName)}_{stamp}.zip");
                    FileUtilEx.ZipDirectory(CurrentDir, zip);
                    PruneOldZips(ArchiveDir, s.keepSnapshots);
                }

                Session.LastBackupUtc = DateTime.UtcNow;
                Session.RunsCount = Session.RunsCount + 1;

                if (showToast)
                    EditorWindow.focusedWindow?.ShowNotification(
                        new GUIContent($"Backup OK ({reason ?? "timer"}) • copiati {copied}, skippati {skipped}"));
                Debug.Log($"[ASB] Backup completato. Copiati {copied}, Skippati {skipped}, Totali {man.entries.Count}.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[ASB] Errore backup: " + ex);
                EditorUtility.DisplayDialog("Avatar Smart Backup – Errore", ex.Message, "OK");
            }
        }

        static void PruneOldZips(string dir, int keep)
        {
            var zips = Directory.GetFiles(dir, "*.zip", SearchOption.TopDirectoryOnly)
                                .OrderByDescending(f => new FileInfo(f).CreationTimeUtc)
                                .ToList();
            for (int i = keep; i < zips.Count; i++)
            {
                try { File.Delete(zips[i]); } catch { }
            }
        }

        static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }

    // ============= TIMER SERVICE (usa SessionState) ==========================

    [InitializeOnLoad]
    public static class TimerService
    {
        static readonly double UpdateEverySec = 0.5;
        static double _nextTick;

        static TimerService()
        {
            EditorApplication.update += Update;
            // Autostart in base alle settings
            var settings = BackupManager.LoadSettings();
            if (settings.autoRunOnLoad)
            {
                StartTimerIfNeeded(settings); // non blocca se già in esecuzione
            }
            TryHookVRChat(); // una volta, best-effort
        }

        public static void StartTimerIfNeeded(BackupSettings s)
        {
            if (!Session.IsRunning)
            {
                Session.IsRunning = true;
                ScheduleNext(DateTime.UtcNow.AddMinutes(Math.Max(1, s.intervalMinutes)));
            }
            else if (Session.NextRunUtc == null) // edge reload
            {
                ScheduleNext(DateTime.UtcNow.AddMinutes(Math.Max(1, s.intervalMinutes)));
            }
        }

        public static void PauseTimer() => Session.IsRunning = false;

        static void ScheduleNext(DateTime utc) => Session.NextRunUtc = utc;

        static void Update()
        {
            if (EditorApplication.timeSinceStartup < _nextTick) return;
            _nextTick = EditorApplication.timeSinceStartup + UpdateEverySec;

            var s = BackupManager.LoadSettings();
            if (!Session.IsRunning) return;

            // non in play / non durante compilazione
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            DateTime now = DateTime.UtcNow;
            var due = Session.NextRunUtc ?? now;
            if (now >= due)
            {
                BackupManager.RunBackupNow(s, showToast:true, reason:"timer");
                ScheduleNext(now.AddMinutes(Math.Max(1, s.intervalMinutes)));
            }
        }

        // Trigger extra all'entrata in Play
        [InitializeOnLoadMethod]
        static void HookPlaymodeShot()
        {
            var s = BackupManager.LoadSettings();
            EditorApplication.playModeStateChanged += (state) =>
            {
                if (state == PlayModeStateChange.ExitingEditMode && s.backupOnPlayEnter)
                {
                    BackupManager.RunBackupNow(s, showToast:false, reason:"play-enter");
                }
            };
        }

        // ====== VRChat integrazione “best-effort” (riflessione) ==========
        static void TryHookVRChat()
        {
            if (Session.HookedVRC) return;

            try
            {
                // Cerchiamo qualunque evento statico chiamato "OnPreprocessAvatar" in namespace che contenga "VRC"
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types = Array.Empty<Type>();
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t.FullName == null || t.FullName.IndexOf("VRC", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        var ev = t.GetEvent("OnPreprocessAvatar", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (ev == null) continue;

                        // Provare ad agganciarsi con un delegate compatibile (0..2 arg)
                        MethodInfo callback = typeof(TimerService).GetMethod(nameof(OnVRCPreprocessAvatar),
                            BindingFlags.NonPublic | BindingFlags.Static);

                        Delegate del = null;
                        var et = ev.EventHandlerType;
                        var invoke = et.GetMethod("Invoke");
                        var pars = invoke.GetParameters();
                        if (pars.Length == 0)
                            del = Delegate.CreateDelegate(et, null, typeof(TimerService).GetMethod(nameof(OnVRC0),
                                BindingFlags.NonPublic | BindingFlags.Static));
                        else if (pars.Length == 1)
                            del = Delegate.CreateDelegate(et, null, typeof(TimerService).GetMethod(nameof(OnVRC1),
                                BindingFlags.NonPublic | BindingFlags.Static));
                        else
                            del = Delegate.CreateDelegate(et, null, callback);

                        ev.AddEventHandler(null, del);
                        Session.HookedVRC = true;
                        Debug.Log("[ASB] Hook VRChat OnPreprocessAvatar agganciato su " + t.FullName);
                        return;
                    }
                }
                // Se non abbiamo trovato l’evento, va bene: fallback su Play enter + timer.
            }
            catch (Exception ex)
            {
                Debug.Log("[ASB] Hook VRChat non riuscito (ok fallback). " + ex.Message);
            }
        }

        // Varianti per firme diverse
        static void OnVRC0() => BackupManager.RunBackupNow(BackupManager.LoadSettings(), reason:"vrchat-preprocess");
        static void OnVRC1(object _unused) => BackupManager.RunBackupNow(BackupManager.LoadSettings(), reason:"vrchat-preprocess");
        static void OnVRCPreprocessAvatar(object _a, object _b) => BackupManager.RunBackupNow(BackupManager.LoadSettings(), reason:"vrchat-preprocess");
    }

    // ============= EDITOR WINDOW (UI semplice) ===============================

    public class AvatarSmartBackupWindow : EditorWindow
    {
        Vector2 _scroll;
        BackupSettings _settings;

        [MenuItem("Tools/Avatar Smart Backup")]
        public static void Open()
        {
            var w = GetWindow<AvatarSmartBackupWindow>();
            w.titleContent = new GUIContent("Avatar Smart Backup");
            w.minSize = new Vector2(420, 460);
            w.Show();
        }

        void OnEnable()
        {
            _settings = BackupManager.LoadSettings();
        }

        void OnDisable()
        {
            BackupManager.SaveSettings(_settings);
        }

        void OnGUI()
        {
            if (_settings == null) _settings = BackupManager.LoadSettings();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Backup VRChat/Avatar – Peace of Mind", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Backup incrementale, atomico e a prova di crash. Struttura cartelle identica al progetto. Manifest con GUID per gestire move/rename. ZIP opzionali con retention.", MessageType.Info);

            // Stato
            EditorGUILayout.LabelField("Project Root:", FileUtilEx.ProjectRoot);
            EditorGUILayout.LabelField("Backup Root:", FileUtilEx.BackupRoot);

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginVertical("box");
            _settings.autoRunOnLoad = EditorGUILayout.ToggleLeft("Avvio automatico all’apertura del progetto", _settings.autoRunOnLoad);
            _settings.intervalMinutes = Mathf.Clamp(EditorGUILayout.IntField("Intervallo (minuti)", _settings.intervalMinutes), 1, 240);
            _settings.keepSnapshots = Mathf.Clamp(EditorGUILayout.IntField("N° snapshot (ZIP) da mantenere", _settings.keepSnapshots), 1, 50);
            _settings.zipSnapshots = EditorGUILayout.ToggleLeft("Crea snapshot ZIP oltre a Current/", _settings.zipSnapshots);
            _settings.backupOnPlayEnter = EditorGUILayout.ToggleLeft("Backup quando si entra in Play", _settings.backupOnPlayEnter);
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Cosa includere", EditorStyles.boldLabel);
            _settings.incVRCAssets = EditorGUILayout.ToggleLeft("VRC Expressions (.asset)", _settings.incVRCAssets);
            _settings.incAnimControllers = EditorGUILayout.ToggleLeft("Animator Controller (.controller)", _settings.incAnimControllers);
            _settings.incAnimationClips = EditorGUILayout.ToggleLeft("Animation Clips (.anim)", _settings.incAnimationClips);
            _settings.incScenes = EditorGUILayout.ToggleLeft("Scene (.unity)", _settings.incScenes);
            _settings.incMaterials = EditorGUILayout.ToggleLeft("Materiali (.mat) leggeri", _settings.incMaterials);
            using (new EditorGUI.DisabledScope(!_settings.incMaterials))
                _settings.materialsMaxKB = (long)Mathf.Clamp(EditorGUILayout.LongField("Soglia materiali (KB)", _settings.materialsMaxKB), 10, 100*1024);
            _settings.incDlls = EditorGUILayout.ToggleLeft("Plugin .dll in Assets (leggeri)", _settings.incDlls);
            using (new EditorGUI.DisabledScope(!_settings.incDlls))
                _settings.dllsMaxKB = (long)Mathf.Clamp(EditorGUILayout.LongField("Soglia DLL (KB)", _settings.dllsMaxKB), 128, 1024*10);
            EditorGUILayout.EndVertical();

            _settings.showAdvanced = EditorGUILayout.Foldout(_settings.showAdvanced, "Avanzate / Cartelle");
            if (_settings.showAdvanced)
            {
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("Include folders (opzionale, prefissi tipo “Assets/Avatars/”):");
                DrawStringList(_settings.includeFolders, "Aggiungi cartella");
                EditorGUILayout.LabelField("Exclude folders:");
                DrawStringList(_settings.excludeFolders, "Aggiungi esclusione");
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Backup adesso", GUILayout.Height(28)))
            {
                BackupManager.RunBackupNow(_settings, showToast:true, reason:"manual");
            }

            if (Session.IsRunning)
            {
                if (GUILayout.Button("⏸ Pausa timer", GUILayout.Height(28)))
                {
                    TimerService.PauseTimer();
                }
            }
            else
            {
                if (GUILayout.Button("▶ Avvia timer", GUILayout.Height(28)))
                {
                    TimerService.StartTimerIfNeeded(_settings);
                }
            }

            if (GUILayout.Button("Apri cartella backup", GUILayout.Height(28)))
            {
                EditorUtility.RevealInFinder(FileUtilEx.BackupRoot);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            var next = Session.NextRunUtc.HasValue ? Session.NextRunUtc.Value.ToLocalTime().ToString("HH:mm:ss") : "--";
            var last = Session.LastBackupUtc.HasValue ? Session.LastBackupUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "mai";
            EditorGUILayout.LabelField($"Prossimo run (stimato): {next}");
            EditorGUILayout.LabelField($"Ultimo backup: {last} • Runs: {Session.RunsCount}");

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Ripristino (semplice)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Per semplicità: un tasto per ripristinare l’ULTIMO backup completo (Current/) sovrascrivendo i file del progetto. In alternativa apri la cartella e copia solo ciò che serve.", MessageType.None);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Ripristina ultimo backup (OVERWRITE)", GUILayout.Height(26)))
            {
                if (EditorUtility.DisplayDialog("Ripristino", "Sovrascrivere i file del progetto con l'ultimo backup (Current/)? Consigliato chiudere Play e salvare tutto prima.", "Sì, ripristina", "Annulla"))
                {
                    RestoreLatest();
                }
            }
            if (GUILayout.Button("Apri Current/ per restore manuale", GUILayout.Height(26)))
            {
                EditorUtility.RevealInFinder(Path.Combine(FileUtilEx.BackupRoot, "Current"));
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();

            if (GUI.changed) BackupManager.SaveSettings(_settings);
        }

        static void DrawStringList(List<string> list, string addLabel)
        {
            int remove = -1;
            for (int i = 0; i < list.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                list[i] = EditorGUILayout.TextField(list[i]);
                if (GUILayout.Button("X", GUILayout.Width(24))) remove = i;
                EditorGUILayout.EndHorizontal();
            }
            if (remove >= 0) list.RemoveAt(remove);
            if (GUILayout.Button(addLabel)) list.Add("Assets/");
        }

        static void RestoreLatest()
        {
            string srcRoot = Path.Combine(FileUtilEx.BackupRoot, "Current");
            if (!Directory.Exists(srcRoot))
            {
                EditorUtility.DisplayDialog("Ripristino", "Nessun backup Current/ trovato.", "OK");
                return;
            }
            // Copia tutto da Current/ su ProjectRoot (sovrascrive Assets/.. a mirror)
            foreach (var src in Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories))
            {
                string rel = MakeRelTo(src, srcRoot).Replace("\\","/");
                string dst = Path.Combine(FileUtilEx.ProjectRoot, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(src, dst, true);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ASB] Ripristino: non riesco a copiare " + rel + " – " + ex.Message);
                }
            }
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Ripristino", "Ripristino completato. Se necessario, forzare un Reimport degli asset interessati.", "OK");
        }

        static string MakeRelTo(string path, string root)
        {
            var p = Path.GetFullPath(path);
            var r = Path.GetFullPath(root);
            var pu = new Uri(p);
            var ru = new Uri(r.EndsWith(Path.DirectorySeparatorChar.ToString()) ? r : r + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(ru.MakeRelativeUri(pu).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
#endif
