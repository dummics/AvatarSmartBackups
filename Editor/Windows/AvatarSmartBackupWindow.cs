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
using AvatarSmartBackup; // explicit

namespace AvatarSmartBackup
{
    public class AvatarSmartBackupWindow : EditorWindow
    {
        Vector2 _scroll;
        Vector2 _versionsScroll;
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
            // Simple tab system
            string[] tabs = new[] { "Backup", "Versions" };
            if (!_settings._uiTabInitialized)
            {
                _settings._activeTab = 0;
                _settings._uiTabInitialized = true;
            }
            _settings._activeTab = GUILayout.Toolbar(_settings._activeTab, tabs);
            EditorGUILayout.Space(4);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

                if (_settings._activeTab == 0)
                {
                    DrawBackupTab();
                }
                else if (_settings._activeTab == 1)
                {
                    DrawVersionsTab();
                }
    
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Backup Control", EditorStyles.boldLabel);
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
            
            EditorGUILayout.LabelField($"Next: {Session.NextRunUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "--"}    Last: {Session.LastBackupUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "never"}");
            
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
    
            // === ACTION BUTTONS (centralized) ===
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);
            
            if (GUILayout.Button(new GUIContent("Backup Now", "Start a backup immediately (non-blocking)."))) 
                BackupManager.RunBackupNow(_settings, showToast: true, reason: "manual", showProgressUI: true);
            
            // Snapshot button - always visible but disabled if not Manual policy
            bool isManualPolicy = _settings.zipPolicy == ZipPolicy.Manual;
            bool canCreateSnapshot = isManualPolicy && !BackupManager.IsBusy;
            string snapshotTooltip = isManualPolicy 
                ? (BackupManager.IsBusy ? "Cannot create snapshot while backup is running" : "Create a .zip snapshot from the current backup content")
                : "Available only with Manual snapshot policy (see Advanced Settings > Zip Policy)";
                
            using (new EditorGUI.DisabledScope(!canCreateSnapshot))
            {
                if (GUILayout.Button(new GUIContent("Create Snapshot Now", snapshotTooltip)))
                    _ = SnapshotCreator.RunManualSnapshotAsync(_settings);
            }
            
            if (GUILayout.Button(new GUIContent("Preview & Restore latest backup", "Preview files and choose what to restore. A pre-restore backup of current Assets can be created."), GUILayout.Height(22)))
            {
                RestorePreviewWindow.Open();
            }

            if (GUILayout.Button(new GUIContent("View Versions", "View and manage backup versions. Restore to any previous state."), GUILayout.Height(22)))
            {
                VersionHistoryWindow.Open();
            }
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Open Backup Folder", "Open the folder where backups are stored."))) 
                EditorUtility.RevealInFinder(FileUtilEx.BackupRoot);
            if (GUILayout.Button(new GUIContent("Open Log Folder", "Open the folder containing detailed log files for troubleshooting.")))
            {
                string logDir = Log.GetLogDirectory();
                if (Directory.Exists(logDir))
                    EditorUtility.RevealInFinder(logDir);
                else
                    EditorUtility.DisplayDialog("Log Folder", "Log folder not found. Logs will be created after the first backup operation.", "OK");
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);
            _settings.showAdvanced = EditorGUILayout.Foldout(_settings.showAdvanced, "Advanced Settings");
            if (_settings.showAdvanced)
            {
                // ZIP POLICY
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Zip Policy", EditorStyles.boldLabel);
                if (GUILayout.Button(new GUIContent("?", "What do these options mean?"), GUILayout.Width(22)))
                {
                    EditorUtility.DisplayDialog("Zip Policy", "On Change: create a snapshot only when files changed.\nIdle: create a snapshot when no changes were detected.\nManual: snapshots only when you click 'Create Snapshot Now' (for limited storage).", "OK");
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
                EditorGUILayout.EndVertical();                // WHAT TO INCLUDE
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
                
                // DEBUG SECTION (moved to bottom, contains technical options)
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
                _settings.debugMode = EditorGUILayout.ToggleLeft(new GUIContent("Debug mode", "Enable second-based intervals and extra debug options."), _settings.debugMode);
                
                if (_settings.debugMode)
                {
                    // Use Global Settings (inverted logic, only in debug)
                    EditorGUI.BeginChangeCheck();
                    bool useGlobal = !_settings.useProjectSettings; // Inverted logic
                    bool newUseGlobal = EditorGUILayout.ToggleLeft(new GUIContent("Use global settings", "Use global settings instead of project-local ones. When disabled, settings are stored in ProjectSettings and travel with the project."), useGlobal);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _settings.useProjectSettings = !newUseGlobal; // Inverted logic
                        BackupManager.SaveSettings(_settings);
                        TimerService.InvalidateSettingsCache();
                        _settings = BackupManager.LoadSettings();
                    }
                    
                    // Benchmark controls
                    if (_settings.lastMeasuredMBps > 0f)
                        EditorGUILayout.LabelField($"Measured throughput: {_settings.lastMeasuredMBps:F1} MB/s", EditorStyles.miniLabel);
                    if (GUILayout.Button(new GUIContent("Re-run benchmark", "Measure disk throughput again."), GUILayout.Width(150)))
                    {
                        _ = BackupManager.RunManualBenchmarkAsync(_settings);
                    }
                    
                    // Safety / anti-spam controls (technical, only in debug)
                    EditorGUILayout.Space(6);
                    EditorGUILayout.LabelField("Anti-spam cooldowns", EditorStyles.boldLabel);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(new GUIContent("Snapshot cooldown (s)", "Minimum seconds between manual snapshots."), GUILayout.Width(160));
                    _settings.manualSnapshotCooldownSeconds = Mathf.Clamp(EditorGUILayout.IntField(_settings.manualSnapshotCooldownSeconds, GUILayout.Width(60)), 1, 3600);
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(new GUIContent("Benchmark cooldown (s)", "Minimum seconds between manual benchmark runs."), GUILayout.Width(160));
                    _settings.manualBenchmarkCooldownSeconds = Mathf.Clamp(EditorGUILayout.IntField(_settings.manualBenchmarkCooldownSeconds, GUILayout.Width(60)), 1, 3600);
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(new GUIContent("Min backup interval (s)", "Minimum seconds between manual backup requests."), GUILayout.Width(160));
                    _settings.minManualBackupIntervalSeconds = Mathf.Clamp(EditorGUILayout.IntField(_settings.minManualBackupIntervalSeconds, GUILayout.Width(60)), 1, 3600);
                    EditorGUILayout.EndHorizontal();
                    
                    // Debug logging option
                    _settings.enableDebugLogging = EditorGUILayout.ToggleLeft(new GUIContent("Detailed file logging", "Enable detailed logging to file for troubleshooting (creates larger log files)."), _settings.enableDebugLogging);
                }
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndScrollView();            if (GUI.changed) { BackupManager.SaveSettings(_settings); TimerService.InvalidateSettingsCache(); }
        }
    
    void DrawBackupTab()
    {
            EditorGUILayout.LabelField("Avatar Smart Backup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Automatic safety copies in the background. Keeps Unity responsive. Creates compressed snapshots only when it’s helpful.", MessageType.Info);

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Backup Control", EditorStyles.boldLabel);
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
            
            EditorGUILayout.LabelField($"Next: {Session.NextRunUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "--"}    Last: {Session.LastBackupUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "never"}");
            
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

            // Actions
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);
            if (GUILayout.Button(new GUIContent("Backup Now", "Start a backup immediately (non-blocking)."))) 
                BackupManager.RunBackupNow(_settings, showToast: true, reason: "manual", showProgressUI: true);

            bool isManualPolicy = _settings.zipPolicy == ZipPolicy.Manual;
            bool canCreateSnapshot = isManualPolicy && !BackupManager.IsBusy;
            string snapshotTooltip = isManualPolicy 
                ? (BackupManager.IsBusy ? "Cannot create snapshot while backup is running" : "Create a .zip snapshot from the current backup content")
                : "Available only with Manual snapshot policy (see Advanced Settings > Zip Policy)";
            using (new EditorGUI.DisabledScope(!canCreateSnapshot))
            {
                if (GUILayout.Button(new GUIContent("Create Snapshot Now", snapshotTooltip)))
                    _ = SnapshotCreator.RunManualSnapshotAsync(_settings);
            }
            if (GUILayout.Button(new GUIContent("Preview & Restore latest backup", "Preview files and choose what to restore. A pre-restore backup of current Assets can be created."), GUILayout.Height(22)))
                RestorePreviewWindow.Open();
            if (GUILayout.Button(new GUIContent("View Versions (old window)", "Legacy standalone versions window."), GUILayout.Height(22)))
                VersionHistoryWindow.Open();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Open Backup Folder", "Open the folder where backups are stored."))) 
                EditorUtility.RevealInFinder(FileUtilEx.BackupRoot);
            if (GUILayout.Button(new GUIContent("Open Log Folder", "Open the folder containing detailed log files for troubleshooting.")))
            {
                string logDir = Log.GetLogDirectory();
                if (Directory.Exists(logDir)) EditorUtility.RevealInFinder(logDir); else EditorUtility.DisplayDialog("Log Folder", "Log folder not found.", "OK");
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            DrawAdvancedSettings();
    }

    void DrawVersionsTab()
    {
        EditorGUILayout.LabelField("Versions", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Restore points created after backups. Pin to protect from cleanup. Rename for clarity.", MessageType.Info);

        // Toolbar line
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(new GUIContent("Refresh", "Reload versions from disk"), GUILayout.Width(70))) _cachedVersions = null;
        GUI.enabled = !BackupManager.IsBusy;
        if (GUILayout.Button(new GUIContent("Create Version", "Force creation of a new version now"), GUILayout.Width(110)))
        {
            using var vm = new FileBasedVersionManager();
            vm.CreateVersion("Manual", Path.Combine(FileUtilEx.BackupRoot, "Current"));
            _cachedVersions = null; // invalidate cache
        }
        GUI.enabled = true;
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        EnsureVersionsCache();
        if (_cachedVersions == null || _cachedVersions.Count == 0)
        {
            EditorGUILayout.HelpBox("No versions yet.", MessageType.Info);
            return;
        }

        _versionsScroll = EditorGUILayout.BeginScrollView(_versionsScroll);
    foreach (var v in _cachedVersions.OrderByDescending(v => v.pinned).ThenByDescending(v => v.timestamp))
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            string title = $"#{v.id}  {(string.IsNullOrEmpty(v.description) ? "(no description)" : v.description)}";
            if (v.pinned) title = "📌 " + title;
            if (v.incomplete) title += "  (writing...)";
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(v.pinned ? "Unpin" : "Pin", GUILayout.Width(50)))
            {
                using var vm = new FileBasedVersionManager();
                vm.SetPinned(v.id, !v.pinned);
                _cachedVersions = null; break;
            }
            if (GUILayout.Button("Open", GUILayout.Width(50)))
            {
                string dir = Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{v.id:D3}");
                if (Directory.Exists(dir)) EditorUtility.RevealInFinder(dir); else EditorUtility.DisplayDialog("Version", "Folder not found", "OK");
            }
            if (_renamingId == v.id)
            {
                GUI.SetNextControlName("RenameField");
                EditorGUILayout.BeginHorizontal();
                _renameBuffer = EditorGUILayout.TextField(_renameBuffer, GUILayout.MinWidth(120));
                if (GUILayout.Button("OK", GUILayout.Width(40))) CommitRename(v);
                if (GUILayout.Button("X", GUILayout.Width(22))) { _renamingId = -1; _renameBuffer = string.Empty; }
                EditorGUILayout.EndHorizontal();
                if (Event.current.isKey && Event.current.keyCode == KeyCode.Return)
                {
                    CommitRename(v);
                }
            }
            else if (GUILayout.Button("Rename", GUILayout.Width(60)))
            {
                _renamingId = v.id;
                _renameBuffer = v.description;
                GUI.FocusControl("RenameField");
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField($"Created: {v.timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Files: {v.fileCount}    Size: {FormatSize(v.totalSizeBytes)}", EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }
        EditorGUILayout.EndScrollView();
    }

    void EnsureVersionsCache()
    {
        if (_cachedVersions != null) return;
        try
        {
            using var vm = new FileBasedVersionManager();
            _cachedVersions = vm.GetVersions();
        }
        catch (Exception ex)
        {
            EditorGUILayout.HelpBox("Failed to load versions: " + ex.Message, MessageType.Error);
        }
    }

    string FormatSize(long bytes)
    {
        if (bytes <= 0) return "--";
        string[] units = { "B", "KB", "MB", "GB" };
        double val = bytes;
        int u = 0;
        while (val > 1024 && u < units.Length - 1) { val /= 1024; u++; }
        return $"{val:0.0} {units[u]}";
    }

    List<VersionInfo> _cachedVersions;
    int _renamingId = -1;
    string _renameBuffer = string.Empty;

    void CommitRename(VersionInfo v)
    {
        string trimmed = (_renameBuffer ?? string.Empty).Trim();
        if (trimmed.Length == 0) { _renamingId = -1; _renameBuffer = string.Empty; return; }
        if (trimmed != v.description)
        {
            using var vm = new FileBasedVersionManager();
            vm.UpdateDescription(v.id, trimmed);
            _cachedVersions = null;
        }
        _renamingId = -1; _renameBuffer = string.Empty;
    }

    void DrawAdvancedSettings()
    {
        EditorGUILayout.Space(10);
        _settings.showAdvanced = EditorGUILayout.Foldout(_settings.showAdvanced, "Advanced Settings");
        if (!_settings.showAdvanced) return;
        // Inline move of original advanced settings block
        // ... existing advanced settings UI code reused from previous OnGUI (kept above in original file) ...
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
}
#endif
