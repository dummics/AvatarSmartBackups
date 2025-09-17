#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
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
        // Include/Exclude UI temp fields
        string _newIncludePattern = string.Empty;
        string _newExcludePattern = string.Empty;
        string _includePrefix = string.Empty;
        string _excludePrefix = string.Empty;
        UnityEngine.Object _includeFolderObj;
        UnityEngine.Object _excludeFolderObj;
        // Signature caching for gating manual version creation
        struct BackupSignature { public long size; public int count; }
        BackupSignature? _cachedCurrentSig; double _cachedCurrentSigTime;
        GUIStyle _selectedTitleStyle; // enlarged style for selected version title
        List<VersionInfo> _cachedVersions; // cached index
        int _renamingId = -1; string _renameBuffer = string.Empty; // rename state
        int _selectedVersionId = -1; // selected version id (default latest)
        // Doppio click tracking per apertura rapida preview
        int _lastClickId = -1; double _lastClickTime = -1;
        double _lastManualRunTime = -1; // throttle manual backup
        // Textures (Resources)
        static Texture2D _texExplorer; static bool _texTried;
        void EnsureTextures()
        {
            if (!_texTried)
            {
                _texExplorer = Resources.Load<Texture2D>("explorerIcon");
                _texTried = true;
            }
        }
        [MenuItem("Avatar Smart Backup/Open", false, 0)]
        public static void Open()
        {
            var w = GetWindow<AvatarSmartBackupWindow>(true, AvatarSmartBackup.Localization.L.T("window.main.title", "Avatar Smart Backup"));
            w.minSize = new Vector2(320, 320);
            w.Show();
        }
        void OnEnable() => _settings = BackupManager.LoadSettings();
        void OnDisable() { BackupManager.SaveSettings(_settings); TimerService.InvalidateSettingsCache(); }
        void OnGUI()
        {
            try
            {
                if (_settings == null) _settings = BackupManager.LoadSettings();
                EditorGUI.BeginChangeCheck();
                // Tabs
                string[] tabs = { AvatarSmartBackup.Localization.L.T("ui.tabs.backup", "Backup"), AvatarSmartBackup.Localization.L.T("ui.tabs.versions", "Versions") };
                if (!_settings._uiTabInitialized) { _settings._activeTab = 0; _settings._uiTabInitialized = true; }
                _settings._activeTab = GUILayout.Toolbar(_settings._activeTab, tabs);
                EditorGUILayout.Space(4);
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                if (_settings._activeTab == 0) DrawBackupTab(); else DrawVersionsTab();
                EditorGUILayout.EndScrollView();
                if (EditorGUI.EndChangeCheck())
                {
                    BackupManager.SaveSettings(_settings);
                    TimerService.InvalidateSettingsCache();
                }
            }
            catch (Exception ex)
            {
                // Evita che un'eccezione lasci il layout in stato corrotto e generi spam
                try { EditorGUILayout.EndScrollView(); } catch { }
                GUILayout.Label("UI error: " + ex.Message, EditorStyles.helpBox);
                Repaint();
            }
        }
        void DrawBackupTab()
        {
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("window.main.header", "Avatar Smart Backup"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(AvatarSmartBackup.Localization.L.T("window.main.autobackup.help", "Automatic safety copies run in the background. Use 'Backup Now' to force one (throttled)."), MessageType.Info);
            DrawSchedulerSection();
            DrawPrimaryActions();
            DrawVersionsOverview();
            EditorGUILayout.Space();
            bool newAdvanced = EditorGUILayout.ToggleLeft(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.advancedMode", "Advanced Mode"), AvatarSmartBackup.Localization.L.T("ui.advancedMode.tooltip", "Show advanced options (filters, performance, verify, retention).")), _settings.AdvancedMode);
            if (newAdvanced != _settings.AdvancedMode)
            {
                _settings.AdvancedMode = newAdvanced;
                TimerService.InvalidateSettingsCache();
            }
            if (_settings.AdvancedMode)
            {
                DrawAdvancedOverview();
                DrawAdvancedSettings();
            }
        }
        void DrawSchedulerSection()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.autobackups.title", "Automatic Backups"), EditorStyles.boldLabel);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            bool running = Session.IsRunning;
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = running ? new Color(0.25f, 0.55f, 0.25f, 1f) : new Color(0.45f, 0.2f, 0.2f, 1f);
            if (GUILayout.Button(new GUIContent(running ? AvatarSmartBackup.Localization.L.T("ui.autobackups.on", "Automatic Backups: ON") : AvatarSmartBackup.Localization.L.T("ui.autobackups.off", "Automatic Backups: OFF"), AvatarSmartBackup.Localization.L.T("ui.autobackups.toggle.tt", "Toggle background backup scheduler")), GUILayout.Width(220), GUILayout.Height(30)))
            {
                if (running) TimerService.PauseTimer();
                else TimerService.StartTimerIfNeeded(_settings);
            }
            GUI.backgroundColor = prev;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
            {
                var nextLabel = AvatarSmartBackup.Localization.L.T("ui.autobackups.next", "Next");
                var lastLabel = AvatarSmartBackup.Localization.L.T("ui.autobackups.last", "Last");
                var never = AvatarSmartBackup.Localization.L.T("ui.never", "never");
                var nextStr = Session.NextRunUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "--";
                var lastStr = Session.LastBackupUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? never;
                EditorGUILayout.LabelField($"{nextLabel}: {nextStr}    {lastLabel}: {lastStr}");
            }
            if (_settings.AdvancedMode)
            {
                EditorGUILayout.Space(4);
                DrawIntervalControls();
                int effCopy = BackupManager.EffectiveCopyMBps(_settings);
                if (_settings.lastBackupBytes > 0 && effCopy > 0)
                {
                    double secNeeded = _settings.lastBackupBytes / (effCopy * 1024.0 * 1024.0);
                    double intervalSeconds = _settings.intervalInSeconds ? _settings.intervalMinutes : _settings.intervalMinutes * 60.0;
                    if (secNeeded > intervalSeconds)
                    {
                        double minutesNeeded = secNeeded / 60.0;
                        double mb = _settings.lastBackupBytes / (1024.0 * 1024.0);
                        EditorGUILayout.HelpBox(AvatarSmartBackup.Localization.L.T("warn.backup.speed", "At {0} MB/s, backing up {1:0.0} MB takes ~{2:0.0} min, exceeding the interval.", effCopy, mb, minutesNeeded), MessageType.Warning);
                    }
                }
            }
            else
            {
                EditorGUILayout.Space(4);
                // Timer controls (moved from advanced mode)
                DrawIntervalControls();
            }
            EditorGUILayout.EndVertical();
        void DrawIntervalControls()
        {
            EditorGUILayout.BeginHorizontal();
            string[] unitOptions = { AvatarSmartBackup.Localization.L.T("ui.interval.min", "min"), AvatarSmartBackup.Localization.L.T("ui.interval.sec", "sec") };
            int currentUnitIndex = _settings.intervalInSeconds ? 1 : 0;
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.interval.label", "Interval"), GUILayout.Width(60));
            int maxValue = _settings.intervalInSeconds ? 3600 : 240;
            int newInterval = Mathf.Clamp(EditorGUILayout.IntField(_settings.intervalMinutes, GUILayout.Width(70)), 1, maxValue);
            if (newInterval != _settings.intervalMinutes)
                _settings.intervalMinutes = newInterval;
            int newUnitIndex = EditorGUILayout.Popup(currentUnitIndex, unitOptions, GUILayout.Width(50));
            if (newUnitIndex != currentUnitIndex)
            {
                if (newUnitIndex == 1 && !_settings.intervalInSeconds)
                {
                    _settings.intervalMinutes = Mathf.Max(1, _settings.intervalMinutes * 60);
                }
                else if (newUnitIndex == 0 && _settings.intervalInSeconds)
                {
                    _settings.intervalMinutes = Mathf.Max(1, Mathf.RoundToInt(_settings.intervalMinutes / 60f));
                }
                _settings.intervalInSeconds = (newUnitIndex == 1);
            }
            EditorGUILayout.EndHorizontal();
        }

        }
        void DrawPrimaryActions()
        {
            const double BackupManualCooldownSeconds = 30;
            double now = EditorApplication.timeSinceStartup;
            bool throttle = !_settings.AdvancedMode;
            bool canManual = true;
            string tooltip = "Run a backup now (30s cooldown)";
            if (throttle && _lastManualRunTime > 0 && now - _lastManualRunTime < BackupManualCooldownSeconds)
            {
                canManual = false;
                double rem = BackupManualCooldownSeconds - (now - _lastManualRunTime);
                tooltip = $"Wait {rem:0}s before another manual backup";
            }
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!canManual || BackupManager.IsBusy))
            {
                string backupTooltip = _settings.AdvancedMode ? AvatarSmartBackup.Localization.L.T("tt.backup.now.advanced", "Run a backup now (no cooldown in Advanced Mode)") : tooltip;
                if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.backup.now", "Backup Now"), backupTooltip)))
                {
                    _lastManualRunTime = now;
                    BackupManager.RunBackupNow(_settings, showToast: true, reason: "manual", showProgressUI: true);
                }
            }
            EnsureVersionsCache();
            var latest = GetLatestVersionCached();
            using (new EditorGUI.DisabledScope(latest == null))
            {
                string restoreTooltip = latest == null ? AvatarSmartBackup.Localization.L.T("tt.latest.none", "No version available") : AvatarSmartBackup.Localization.L.T("tt.latest.open", "Open the latest version to restore or inspect");
                if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.latest.previewRestore", "Preview & Restore latest"), restoreTooltip)))
                {
                    if (latest != null)
                        RestorePreviewWindow.Open(latest.id);
                }
            }
            if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.open.backup.folder", "Open Backup Folder"), AvatarSmartBackup.Localization.L.T("tt.open.backup.folder", "Open the backups folder"))))
            {
                EditorUtility.RevealInFinder(FileUtilEx.BackupRoot);
            }
            EditorGUILayout.EndHorizontal();
        }
        void DrawAdvancedOverview()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.advanced.tools.title", "Advanced Tools"), EditorStyles.boldLabel);
            // Single toolbar row with actions
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.advanced.openLogFolder", "Open Log Folder"), AvatarSmartBackup.Localization.L.T("tt.advanced.openLogFolder", "Open the logs folder for support")), GUILayout.Width(150)))
            {
                string logDir = Log.GetLogDirectory();
                if (Directory.Exists(logDir)) EditorUtility.RevealInFinder(logDir);
                else EditorUtility.DisplayDialog(AvatarSmartBackup.Localization.L.T("dlg.log.title", "Log Folder"), AvatarSmartBackup.Localization.L.T("dlg.log.notfound", "Log folder not found."), "OK");
            }
            if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.advanced.refreshVersions", "Refresh Versions"), AvatarSmartBackup.Localization.L.T("tt.advanced.refreshVersions", "Reload the list of versions")), GUILayout.Width(150)))
            {
                _cachedVersions = null;
                EnsureVersionsCache();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }
        void DrawVersionsOverview()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.overview.latest.title", "Latest Version"), EditorStyles.boldLabel);
            EnsureVersionsCache();
            var latest = GetLatestVersionCached();
            if (latest != null)
            {
                EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.overview.latest.label", "Latest Version:"), EditorStyles.miniBoldLabel);
                string desc = string.IsNullOrEmpty(latest.description) ? AvatarSmartBackup.Localization.L.T("ui.overview.noDescription", "(no description)") : latest.description;
                EditorGUILayout.LabelField($"# {latest.id}  {desc}", EditorStyles.miniLabel);
                var created = ParseCreatedUtc(latest);
                if (created != DateTime.MinValue)
                    EditorGUILayout.LabelField($"{AvatarSmartBackup.Localization.L.T("ui.overview.created", "Created:")} {created:yyyy-MM-dd HH:mm:ss}", EditorStyles.miniLabel);
                {
                    var filesLbl = AvatarSmartBackup.Localization.L.T("ui.overview.filesSize", "Files");
                    var sizeLbl = AvatarSmartBackup.Localization.L.T("ui.overview.size", "Size");
                    EditorGUILayout.LabelField($"{filesLbl}: {latest.fileCount}  {sizeLbl}: {FormatSize(latest.totalSizeBytes)}", EditorStyles.miniLabel);
                }
                if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.overview.gotoVersions", "Go to Versions"), AvatarSmartBackup.Localization.L.T("tt.overview.gotoVersions", "Open Versions tab")), GUILayout.Width(140)))
                {
                    _settings._activeTab = 1;
                }
            }
            else
            {
                EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.overview.none", "No versions available"), EditorStyles.miniLabel);
            }
            EditorGUILayout.EndVertical();
        }
        void DrawVersionsTab()
        {
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.versions.title", "Versions"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(AvatarSmartBackup.Localization.L.T("ui.versions.help", "Restore points are automatically created when there are changes. Use the star to pin, click to select, double-click to preview."), MessageType.Info);
            // Versions list (in-place selection, no reordering)
            EnsureVersionsCache();
            var latest = GetLatestVersionCached();
            if (_selectedVersionId < 0 && latest != null) _selectedVersionId = latest.id;
            // Toolbar line
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.versions.refresh", "Refresh"), AvatarSmartBackup.Localization.L.T("tt.versions.refresh", "Reload versions from disk")), GUILayout.Width(70))) _cachedVersions = null;
            if (_settings.showRebuildTool)
            {
                if (GUILayout.Button(new GUIContent("Rebuild Index", "Recalculate fileCount/size from existing manifests"), GUILayout.Width(120)))
                    RunRebuildIndexTool();
            }
            if (_settings.AdvancedMode)
            {
                GUI.enabled = !BackupManager.IsBusy;
                bool allowManualVersion = CanCreateManualVersion(out string reasonBlock);
                using (new EditorGUI.DisabledScope(!allowManualVersion))
                {
                    if (GUILayout.Button(new GUIContent("Create Version", allowManualVersion ? "Create a manual restore point" : reasonBlock), GUILayout.Width(110)))
                    {
                        using var vm = new FileBasedVersionManager();
                        vm.CreateVersion("Manual", Path.Combine(FileUtilEx.BackupRoot, "Current"), _settings, forceCheckpoint: true);
                        _cachedVersions = null; EnsureVersionsCache();
                    }
                }
                GUI.enabled = true;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            if (_cachedVersions == null || _cachedVersions.Count == 0)
            {
                EditorGUILayout.HelpBox("No versions yet.", MessageType.Info);
                return;
            }
            _versionsScroll = EditorGUILayout.BeginScrollView(_versionsScroll);
            int rowIndex = 0;
            Event e = Event.current;
            int? pendingTogglePin = null; // differiamo mutazioni per evitare rottura layout
            int? pendingDelete = null;
            foreach (var v in _cachedVersions
                .OrderByDescending(v => v.pinned)
                .ThenByDescending(v => ParseCreatedUtc(v)))
            {
                // Hard reset di sicurezza per evitare stato disabled ereditato
                GUI.enabled = true;
                bool isSelected = v.id == _selectedVersionId;
                if (_selectedTitleStyle == null)
                    _selectedTitleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = EditorStyles.boldLabel.fontSize + 1 };
                EnsureTextures();
                // Begin card content
                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent(v.pinned ? "★" : "☆", v.pinned ? "Unpin" : "Pin"), GUILayout.Width(24))) pendingTogglePin = v.id;
                Rect starRect = GUILayoutUtility.GetLastRect(); // rect of the star button
                string title = $"#{v.id}  {(string.IsNullOrEmpty(v.description) ? "(no description)" : v.description)}";
                if (v.incomplete) title += "  (writing...)";
                if (latest != null && latest.id == v.id) title = "Latest • " + title;
                EditorGUILayout.LabelField(title, isSelected ? _selectedTitleStyle : EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                Rect folderBtnRect = GUILayoutUtility.GetRect(20, 18, GUILayout.Width(20));
                if (_texExplorer != null && Event.current.type == EventType.Repaint)
                    GUI.DrawTexture(folderBtnRect, _texExplorer, ScaleMode.ScaleToFit, true);
                else if (Event.current.type == EventType.Repaint && _texExplorer == null)
                { var style = EditorStyles.miniLabel; var pc = GUI.color; GUI.color = new Color(1, 1, 1, 0.35f); GUI.Label(folderBtnRect, "☰", style); GUI.color = pc; }
                if (GUI.Button(folderBtnRect, GUIContent.none, GUIStyle.none)) { string dir = Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{v.id:D3}"); if (Directory.Exists(dir)) EditorUtility.RevealInFinder(dir); else EditorUtility.DisplayDialog("Version", "Folder not found", "OK"); }
                EditorGUILayout.EndHorizontal();
                var created = ParseCreatedUtc(v);
                EditorGUILayout.LabelField($"Created: {(created == DateTime.MinValue ? "--" : created.ToString("yyyy-MM-dd HH:mm:ss"))}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Files: {v.fileCount}    Size: {FormatSize(v.totalSizeBytes)}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField(BuildVersionTypeSummary(v), EditorStyles.miniLabel);
                string changeSummary = BuildChangeCountSummary(v);
                if (!string.IsNullOrEmpty(changeSummary))
                    EditorGUILayout.LabelField(changeSummary, EditorStyles.miniLabel);
                if (!v.isCheckpoint && v.checkpointId > 0)
                {
                    var cp = _cachedVersions?.FirstOrDefault(cv => cv.id == v.checkpointId);
                    if (cp != null)
                    {
                        var cpDate = ParseCreatedUtc(cp);
                        string cpLabel = cpDate == DateTime.MinValue ? $"#{cp.id}" : $"#{cp.id} ({cpDate:yyyy-MM-dd HH:mm})";
                        EditorGUILayout.LabelField($"Source checkpoint: {cpLabel}", EditorStyles.miniLabel);
                    }
                }
                if (isSelected)
                {
                    GUILayout.Space(4);
                    EditorGUILayout.BeginVertical("box");
                    EditorGUILayout.HelpBox(BuildVersionDetailSummary(v), MessageType.Info);
                    if (_renamingId == v.id)
                    {
                        EditorGUILayout.BeginHorizontal(); GUI.SetNextControlName("RenameField"); _renameBuffer = EditorGUILayout.TextField(_renameBuffer);
                        if (GUILayout.Button("Save", GUILayout.Width(50))) CommitRename(v);
                        if (GUILayout.Button("Cancel", GUILayout.Width(60))) { _renamingId = -1; _renameBuffer = string.Empty; }
                        EditorGUILayout.EndHorizontal(); if (e.isKey && e.keyCode == KeyCode.Return) CommitRename(v);
                    }
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button(new GUIContent("Restore", "Open preview and proceed to restore"), GUILayout.Height(22))) RestorePreviewWindow.Open(v.id);
                    using (new EditorGUI.DisabledScope(v.pinned))
                    {
                        if (GUILayout.Button(new GUIContent("Delete", v.pinned ? "Pinned version protected" : "Delete this version"), GUILayout.Height(22), GUILayout.Width(70)))
                        {
                            if (!v.pinned && EditorUtility.DisplayDialog("Delete Version", $"Delete version #{v.id}?", "Delete", "Cancel")) pendingDelete = v.id;
                        }
                    }
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button(new GUIContent("Show changes", "Open a summary of changed files"), GUILayout.Height(22)))
                    {
                        ShowVersionDiffSummary(v);
                    }
                    GUILayout.FlexibleSpace();
                    if (_settings.AdvancedMode)
                    {
                        using (new EditorGUI.DisabledScope(v.isCheckpoint))
                        {
                            if (GUILayout.Button(new GUIContent("Rebuild snapshot", "Generate a temporary snapshot and open the folder"), GUILayout.Height(22)))
                            {
                                TriggerSnapshotRebuild(v);
                            }
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.LabelField("Press F2 to rename", EditorStyles.miniLabel);
                    EditorGUILayout.EndVertical();
                }
                EditorGUILayout.EndVertical();
                Rect cardRect = GUILayoutUtility.GetLastRect();
                bool isHover = cardRect.Contains(e.mousePosition);
                if (Event.current.type == EventType.Repaint)
                {
                    // Niente fill sopra il contenuto: solo un accento visivo non invasivo
                    if (isSelected)
                    {
                        var bar = new Rect(cardRect.x, cardRect.y, 4f, cardRect.height);
                        EditorGUI.DrawRect(bar, new Color(0.30f, 0.65f, 1f, 0.95f));
                        // Sottile outline semi trasparente (dietro testo non lo schiaccia)
                        Handles.BeginGUI();
                        Handles.color = new Color(0.30f, 0.65f, 1f, 0.35f);
                        Handles.DrawAAPolyLine(1.5f, new Vector3(cardRect.x, cardRect.y), new Vector3(cardRect.xMax, cardRect.y));
                        Handles.DrawAAPolyLine(1.5f, new Vector3(cardRect.xMax, cardRect.y), new Vector3(cardRect.xMax, cardRect.yMax));
                        Handles.DrawAAPolyLine(1.5f, new Vector3(cardRect.xMax, cardRect.yMax), new Vector3(cardRect.x, cardRect.yMax));
                        Handles.DrawAAPolyLine(1.5f, new Vector3(cardRect.x, cardRect.yMax), new Vector3(cardRect.x, cardRect.y));
                        Handles.EndGUI();
                    }
                    else if (isHover)
                    {
                        var bar = new Rect(cardRect.x, cardRect.y, 3f, cardRect.height);
                        EditorGUI.DrawRect(bar, new Color(1f, 1f, 1f, 0.25f));
                    }
                }
                if (e.type == EventType.MouseDown && e.button == 0 && cardRect.Contains(e.mousePosition))
                { if (!starRect.Contains(e.mousePosition) && !folderBtnRect.Contains(e.mousePosition)) { double now = EditorApplication.timeSinceStartup; bool db = (_lastClickId == v.id) && (now - _lastClickTime < 0.35f); _lastClickId = v.id; _lastClickTime = now; _selectedVersionId = v.id; GUI.FocusControl(""); Repaint(); if (db) RestorePreviewWindow.Open(v.id); e.Use(); } }
                GUILayout.Space(4);
                rowIndex++;
            }
            EditorGUILayout.EndScrollView();
            // Gestione hotkey F2 per rename su selezionata
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F2 && _selectedVersionId > 0 && _renamingId != _selectedVersionId)
            {
                var sel = _cachedVersions.FirstOrDefault(vv => vv.id == _selectedVersionId);
                if (sel != null) { _renamingId = sel.id; _renameBuffer = sel.description; Repaint(); }
            }
            // Esegui azioni mutate fuori dal loop per evitare problemi layout
            if (pendingTogglePin.HasValue)
            {
                using var vm = new FileBasedVersionManager();
                vm.SetPinned(pendingTogglePin.Value, !_cachedVersions.First(v => v.id == pendingTogglePin.Value).pinned);
                _cachedVersions = null; EnsureVersionsCache();
                Repaint();
                GUIUtility.ExitGUI();
            }
            if (pendingDelete.HasValue)
            {
                try
                {
                    using var vm = new FileBasedVersionManager();
                    vm.DeleteVersion(pendingDelete.Value);
                }
                catch (Exception ex)
                {
                        EditorUtility.DisplayDialog("Delete", "Failed: " + ex.Message, "OK");
                }
                _cachedVersions = null; EnsureVersionsCache();
                if (_selectedVersionId == pendingDelete.Value) _selectedVersionId = -1;
                Repaint();
                GUIUtility.ExitGUI();
            }
            // Overlay icona cartella dopo avere l'intero contenuto (evita mismatch layout)
            if (Event.current.type == EventType.Repaint)
            {
                // Ridisegniamo tutte le card di nuovo? No: semplice approccio futuro -> TODO: convertire in IMGUIContainer overlay.
                // Current simplicity: no overlay multi pass; keep previous behaviour (no extra slot).
                // (Se serve davvero overlay fisso, reintrodurremo slot ma con contenuto invisibile invece di vuoto.)
            }
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
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double val = bytes; int u = 0;
            while (val > 1024 && u < units.Length - 1) { val /= 1024; u++; }
            return $"{val:0.0} {units[u]}";
        }
        DateTime ParseCreatedUtc(VersionInfo info)
        {
            if (info == null) return DateTime.MinValue;
            if (!string.IsNullOrEmpty(info.createdUtc) && DateTime.TryParse(info.createdUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                // Tratta valori legacy/sbagliati come invalidi (es. 0001-01-01 ...)
                if (parsed.Year >= 2000)
                    return parsed.ToLocalTime();
            }
            if (info.timestamp != default)
                return info.timestamp.ToLocalTime();
            return DateTime.MinValue;
        }
        string BuildChangeCountSummary(VersionInfo info)
        {
            if (info == null) return string.Empty;
            if (info.changedFileCount <= 0 && info.removedFileCount <= 0) return string.Empty;
            string delta = info.changedBytes > 0 ? FormatSize(info.changedBytes) : "0 B";
            return $"Changes: {info.changedFileCount}  Removed: {info.removedFileCount}  Delta: {delta}";
        }

        string BuildVersionTypeSummary(VersionInfo info)
        {
            if (info == null) return "Type: --";
            if (info.isCheckpoint) return "Type: Full checkpoint";
            var parts = new List<string>();
            if (info.changedFileCount > 0) parts.Add($"{info.changedFileCount} changed");
            if (info.removedFileCount > 0) parts.Add($"{info.removedFileCount} removed");
            string suffix = parts.Count > 0 ? string.Join(", ", parts) : "no recorded changes";
            return $"Type: Incremental ({suffix})";
        }

        string BuildVersionDetailSummary(VersionInfo info)
        {
            if (info == null) return string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine(info.isCheckpoint ? "Full checkpoint (complete snapshot)." : "Incremental: automatically reconstructed from checkpoint.");
            var changeSummary = BuildChangeCountSummary(info);
            if (!string.IsNullOrEmpty(changeSummary)) sb.AppendLine(changeSummary);
            if (!info.isCheckpoint && info.checkpointId > 0)
            {
                var cp = _cachedVersions?.FirstOrDefault(v => v.id == info.checkpointId);
                if (cp != null)
                {
                    var cpDate = ParseCreatedUtc(cp);
                    sb.AppendLine($"Source checkpoint: #{cp.id}" + (cpDate == DateTime.MinValue ? string.Empty : $" ({cpDate:yyyy-MM-dd HH:mm})"));
                }
            }
            var catSummary = BuildCategorySummary(info);
            if (!string.IsNullOrEmpty(catSummary)) sb.AppendLine("Categories: " + catSummary);
            return sb.ToString().Trim();
        }

        string BuildCategorySummary(VersionInfo info)
        {
            if (info?.categoryStats == null || info.categoryStats.Count == 0) return string.Empty;
            return string.Join(", ", info.categoryStats
                .OrderByDescending(cs => cs.count)
                .ThenBy(cs => cs.category)
                .Select(cs => $"{cs.category}: {cs.count}"));
        }

        void ShowVersionDiffSummary(VersionInfo info)
        {
            if (info == null) return;
            try
            {
                var meta = VersionRestoreService.LoadDeltaMetadata(info) ?? new VersionDeltaMetadata();
                var sb = new StringBuilder();
                sb.AppendLine(BuildVersionDetailSummary(info));
                if (meta.changedEntries != null && meta.changedEntries.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Modified:");
                    int limit = Mathf.Min(20, meta.changedEntries.Count);
                    for (int i = 0; i < limit; i++)
                        sb.AppendLine($" • {meta.changedEntries[i].relPath}");
                    if (meta.changedEntries.Count > limit)
                        sb.AppendLine($" • (+{meta.changedEntries.Count - limit} more)");
                }
                if (meta.removedEntries != null && meta.removedEntries.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Removed:");
                    int limit = Mathf.Min(10, meta.removedEntries.Count);
                    for (int i = 0; i < limit; i++)
                        sb.AppendLine($" • {meta.removedEntries[i]}");
                    if (meta.removedEntries.Count > limit)
                        sb.AppendLine($" • (+{meta.removedEntries.Count - limit} more)");
                }
                EditorUtility.DisplayDialog($"Version #{info.id}", sb.ToString().Trim(), "Close");
            }
            catch (Exception ex)
            {
                Log.Warn("Version diff summary failed: " + ex.Message);
                EditorUtility.DisplayDialog("Show changes", "Unable to read changes:\n" + ex.Message, "OK");
            }
        }

        void TriggerSnapshotRebuild(VersionInfo info)
        {
            if (info == null) return;
            try
            {
                string path = VersionRestoreService.PrepareSnapshot(info.id, forceRebuild: true);
                Log.Info($"Snapshot rebuilt for version #{info.id}: {path}");
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception ex)
            {
                Log.Warn("Rebuild snapshot: " + ex.Message);
                EditorUtility.DisplayDialog("Rebuild snapshot", "Unable to rebuild version:\n" + ex.Message, "OK");
            }
        }
        void RunRebuildIndexTool()
        {
            try
            {
                using var vm = new FileBasedVersionManager();
                var preview = vm.RebuildIndex(applyChanges: false);
                if (!preview.HasChanges && !preview.HasIssues)
                {
                    EditorUtility.DisplayDialog("Rebuild Index", "No discrepancies found.", "OK");
                    return;
                }
                var sb = new StringBuilder();
                if (preview.HasChanges)
                {
                    sb.AppendLine("Detected changes:");
                    foreach (var change in preview.changes)
                    {
                        sb.AppendLine($" • v{change.versionId:D3}: file {change.oldCount} → {change.newCount}, size {FormatSize(change.oldSize)} → {FormatSize(change.newSize)}");
                    }
                }
                if (preview.HasIssues)
                {
                    if (preview.HasChanges) sb.AppendLine();
                    sb.AppendLine("Issues:");
                    foreach (var issue in preview.issues)
                        sb.AppendLine($" • v{issue.versionId:D3}: {issue.message}");
                }
                sb.AppendLine();
                sb.Append("Apply updates?");
                if (EditorUtility.DisplayDialog("Rebuild Index", sb.ToString(), "Apply", "Cancel"))
                {
                    vm.RebuildIndex(applyChanges: true);
                    _cachedVersions = null;
                    EnsureVersionsCache();
                    EditorUtility.DisplayDialog("Rebuild Index", "Index updated.", "OK");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Rebuild Index", "Operation failed:\n" + ex.Message, "OK");
            }
        }

        VersionInfo GetLatestVersionCached()
        {
            if (_cachedVersions == null || _cachedVersions.Count == 0) return null;
            return _cachedVersions.OrderByDescending(v => ParseCreatedUtc(v)).FirstOrDefault();
        }
        bool CanCreateManualVersion(out string reason)
        {
            reason = string.Empty;
            if (!_settings.AdvancedMode && _cachedVersions != null && _cachedVersions.Count > 0)
            { reason = "Manual versions available only in Advanced Mode"; return false; }
            string currentDir = Path.Combine(FileUtilEx.BackupRoot, "Current");
            if (!Directory.Exists(currentDir)) { reason = "No Current backup yet"; return false; }
            double now = EditorApplication.timeSinceStartup;
            if (_cachedCurrentSig == null || now - _cachedCurrentSigTime > 2.0)
            {
                long total = 0; int count = 0;
                try
                {
                    foreach (var f in Directory.GetFiles(currentDir, "*", SearchOption.AllDirectories))
                    {
                        if (f.EndsWith("backup.ok", StringComparison.OrdinalIgnoreCase)) continue;
                        var fi = new FileInfo(f); total += fi.Length; count++;
                    }
                }
                catch { }
                _cachedCurrentSig = new BackupSignature { size = total, count = count }; _cachedCurrentSigTime = now;
            }
            var sig = _cachedCurrentSig.Value;
            var latest = GetLatestVersionCached();
            if (latest != null && latest.fileCount == sig.count && latest.totalSizeBytes == sig.size)
            { reason = "No changes since last version"; return false; }
            return true;
        }
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
            if (!_settings.AdvancedMode) return;
            EditorGUILayout.Space(10);
            _settings.showAdvanced = EditorGUILayout.Foldout(_settings.showAdvanced, "Advanced Settings");
            if (!_settings.showAdvanced) return;
            // ZIP POLICY
            if (_settings.AdvancedMode) // show snapshot policy only in advanced to declutter normal UX
            {
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Snapshot (Zip) Policy", EditorStyles.boldLabel);
                if (GUILayout.Button(new GUIContent("?", "Legacy snapshot system – mainly for compressed archives."), GUILayout.Width(22)))
                {
                    EditorUtility.DisplayDialog("Snapshot Policy", "Snapshots are compressed .zip archives of the backup set. Regular users can rely on Versions instead.", "OK");
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
            }
            // PERFORMANCE
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Performance", EditorStyles.boldLabel);
            _settings.autoThrottle = EditorGUILayout.ToggleLeft(new GUIContent("Auto throttle (recommended)", "Automatically caps IO speed to keep the editor responsive."), _settings.autoThrottle);
            _settings.maxParallelThreads = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Max parallel threads", "Number of concurrent copy/hash tasks."), _settings.maxParallelThreads), 1, Math.Max(1, System.Environment.ProcessorCount));
            _settings.saveScenesBeforeBackup = EditorGUILayout.ToggleLeft(new GUIContent("Save open scenes before backup", "Saves scenes if dirty before backup. May block briefly."), _settings.saveScenesBeforeBackup);
            if (_settings.AdvancedMode && _settings.lastMeasuredMBps > 0f)
                EditorGUILayout.LabelField($"Measured throughput: {_settings.lastMeasuredMBps:F1} MB/s", EditorStyles.miniLabel);
            // Benchmark button only in advanced (moved below) keeps UI simpler
            EditorGUILayout.EndVertical();
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Versioning", EditorStyles.boldLabel);
            _settings.forceFullCheckpointEveryN = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Force checkpoint every N incrementals", "0 = automatic policy (heuristic)."), _settings.forceFullCheckpointEveryN));
            EditorGUILayout.LabelField(_settings.forceFullCheckpointEveryN <= 0 ? "Automatic policy: creates checkpoints when needed." : $"Creates a full checkpoint after {_settings.forceFullCheckpointEveryN} incremental versions.", EditorStyles.miniLabel);
            _settings.showRebuildTool = EditorGUILayout.ToggleLeft(new GUIContent("Show rebuild index tool", "Enable manual rebuild of the versions index from the Versions tab."), _settings.showRebuildTool);
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
                string[] mNames = { "256 KB", "512 KB", "1 MB", "2 MB", "4 MB", "Custom" };
                long[] mValues = { 256, 512, 1024, 2048, 4096, -1 };
                _settings.materialsSizePresetIndex = EditorGUILayout.Popup(new GUIContent("Material size limit", "Skip materials larger than this size."), _settings.materialsSizePresetIndex, mNames);
                int mi = Mathf.Clamp(_settings.materialsSizePresetIndex, 0, mNames.Length - 1);
                if (mi < mNames.Length - 1)
                {
                    _settings.materialsMaxKB = mValues[mi];
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
                string[] dNames = { "512 KB", "1 MB", "2 MB", "4 MB", "Custom" };
                long[] dValues = { 512, 1024, 2048, 4096, -1 };
                if (_settings.dllSizePresetIndex == dNames.Length - 1) { for (int i = 0; i < dValues.Length - 1; i++) if (_settings.dllsMaxKB == dValues[i]) { _settings.dllSizePresetIndex = i; break; } }
                _settings.dllSizePresetIndex = EditorGUILayout.Popup(new GUIContent("DLL size limit", "Skip DLLs larger than this size."), _settings.dllSizePresetIndex, dNames);
                int di = Mathf.Clamp(_settings.dllSizePresetIndex, 0, dNames.Length - 1);
                if (di < dNames.Length - 1)
                {
                    _settings.dllsMaxKB = dValues[di];
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
            // FOLDERS & EXTENSIONS
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Folders & Types", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Add folders from Project or simple extensions (e.g., .prefab).", EditorStyles.miniLabel);
            _settings.extWithinIncludeFolders = EditorGUILayout.ToggleLeft(new GUIContent("Apply extensions only within included folders", "When enabled, extensions like .prefab are searched only inside the folders you included."), _settings.extWithinIncludeFolders);
            EditorGUILayout.EndVertical();
            DrawIncludeExcludeSection("Include", _settings.includeFolders, ref _newIncludePattern, ref _includePrefix, ref _includeFolderObj);
            EditorGUILayout.Space(6);
            DrawIncludeExcludeSection("Exclude", _settings.excludeFolders, ref _newExcludePattern, ref _excludePrefix, ref _excludeFolderObj);
            DrawTrackedSelectionSection();
            EditorGUILayout.EndVertical();
            EditorGUILayout.BeginVertical("box");
            _settings.debugMode = EditorGUILayout.ToggleLeft(new GUIContent("Diagnostics & utilities", "Enable additional tools (benchmark, detailed logs)."), _settings.debugMode);
            if (_settings.debugMode)
            {
                EditorGUI.BeginChangeCheck();
                bool useGlobal = !_settings.useProjectSettings;
                bool newUseGlobal = EditorGUILayout.ToggleLeft(new GUIContent("Use global settings", "Use global settings instead of project-local ones."), useGlobal);
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.useProjectSettings = !newUseGlobal;
                    BackupManager.SaveSettings(_settings);
                    TimerService.InvalidateSettingsCache();
                    _settings = BackupManager.LoadSettings();
                }
                if (GUILayout.Button(new GUIContent("Re-run benchmark", "Measure disk throughput again."), GUILayout.Width(150))) _ = BackupManager.RunManualBenchmarkAsync(_settings);
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
                _settings.enableDebugLogging = EditorGUILayout.ToggleLeft(new GUIContent("Detailed file logging", "Enable detailed logging to file for troubleshooting."), _settings.enableDebugLogging);
            }
            EditorGUILayout.EndVertical();
        }
        void DrawTrackedSelectionSection()
        {
            if (_settings.trackedRoots == null) _settings.trackedRoots = new List<string>();
            var tracked = _settings.trackedRoots;
            bool frozen = BackupManager.IsSelectionFrozen(_settings);
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Tracked Selection", EditorStyles.boldLabel);
            if (tracked.Count == 0)
            {
                EditorGUILayout.LabelField("Tracking all assets allowed by filters.", EditorStyles.miniLabel);
            }
            else
            {
                int removeIndex = -1;
                for (int i = 0; i < tracked.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(tracked[i], GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Remove", GUILayout.Width(70))) removeIndex = i;
                    EditorGUILayout.EndHorizontal();
                }
                if (removeIndex >= 0)
                {
                    var updated = new List<string>(tracked);
                    updated.RemoveAt(removeIndex);
                    if (!BackupManager.TryUpdateTrackedSelection(updated, _settings.selectionLocked, out var err))
                        EditorUtility.DisplayDialog("Tracked Selection", err ?? "Failed to update selection.", "OK");
                    _settings = BackupManager.LoadSettings();
                    TimerService.InvalidateSettingsCache();
                    EditorGUILayout.EndVertical();
                    return;
                }
            }
            string folderToAdd = null;
            string fileToAdd = null;
            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(frozen))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Add Folder...", GUILayout.Width(110)))
                {
                    folderToAdd = EditorUtility.OpenFolderPanel("Select folder", FileUtilEx.ProjectRoot, string.Empty);
                }
                if (GUILayout.Button("Add File...", GUILayout.Width(110)))
                {
                    fileToAdd = EditorUtility.OpenFilePanel("Select asset", FileUtilEx.ProjectRoot, "*");
                }
                EditorGUILayout.EndHorizontal();
                if (!_settings.selectionLocked && GUILayout.Button("Lock Selection"))
                {
                    if (!BackupManager.TryUpdateTrackedSelection(_settings.trackedRoots, true, out var err))
                        EditorUtility.DisplayDialog("Tracked Selection", err ?? "Failed to lock selection.", "OK");
                    _settings = BackupManager.LoadSettings();
                    TimerService.InvalidateSettingsCache();
                    EditorGUILayout.EndVertical();
                    return;
                }
            }
            if (!string.IsNullOrEmpty(folderToAdd))
            {
                string rel = FileUtilEx.MakeRelToProject(folderToAdd).Replace("\\", "/");
                if (!rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    EditorUtility.DisplayDialog("Tracked Selection", "Please choose a folder inside the Assets directory.", "OK");
                }
                else
                {
                    if (!rel.EndsWith("/")) rel += "/";
                    var updated = new List<string>(_settings.trackedRoots ?? new List<string>()) { rel };
                    if (!BackupManager.TryUpdateTrackedSelection(updated, _settings.selectionLocked, out var err))
                        EditorUtility.DisplayDialog("Tracked Selection", err ?? "Failed to update selection.", "OK");
                    _settings = BackupManager.LoadSettings();
                    TimerService.InvalidateSettingsCache();
                    EditorGUILayout.EndVertical();
                    return;
                }
            }
            if (!string.IsNullOrEmpty(fileToAdd))
            {
                string rel = FileUtilEx.MakeRelToProject(fileToAdd).Replace("\\", "/");
                if (!rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    EditorUtility.DisplayDialog("Tracked Selection", "Please choose a file inside the Assets directory.", "OK");
                }
                else if (rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    EditorUtility.DisplayDialog("Tracked Selection", "Script files (.cs) are excluded from backups.", "OK");
                }
                else
                {
                    var updated = new List<string>(_settings.trackedRoots ?? new List<string>()) { rel };
                    if (!BackupManager.TryUpdateTrackedSelection(updated, _settings.selectionLocked, out var err))
                        EditorUtility.DisplayDialog("Tracked Selection", err ?? "Failed to update selection.", "OK");
                    _settings = BackupManager.LoadSettings();
                    TimerService.InvalidateSettingsCache();
                    EditorGUILayout.EndVertical();
                    return;
                }
            }
            if (frozen)
            {
                EditorGUILayout.HelpBox("Selection locked. Remove entries to narrow scope. Reset settings to change additions.", MessageType.Info);
            }
            EditorGUILayout.EndVertical();
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
                catch (Exception ex) { Log.Warn("Restore: failed to copy " + rel + " - " + ex.Message); }
            }
            MainThread.Invoke(() => AssetDatabase.Refresh());
            EditorUtility.DisplayDialog("Restore", "Restore completed.", "OK");
        }
        static string MakeRelTo(string p, string root)
        {
            var pu = new Uri(Path.GetFullPath(p));
            var ru = new Uri(Path.GetFullPath(root) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(ru.MakeRelativeUri(pu).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }
        // Removed separate selected card drawing (now inline)
    }
}
#endif

