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
    void OnDisable() { BackupManager.SaveSettings(_settings); TimerService.InvalidateSettingsCache(); }

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
                TimerService.InvalidateSettingsCache();
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
                TimerService.InvalidateSettingsCache();
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

        if (GUI.changed) { BackupManager.SaveSettings(_settings); TimerService.InvalidateSettingsCache(); }
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
#endif
