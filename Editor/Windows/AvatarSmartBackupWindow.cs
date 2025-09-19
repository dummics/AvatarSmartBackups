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
        BackupSettings _settings = new BackupSettings();
        // Include/Exclude UI temp fields
        string _newIncludePattern = string.Empty;
        string _newExcludePattern = string.Empty;
        string _includePrefix = string.Empty;
        string _excludePrefix = string.Empty;
        UnityEngine.Object? _includeFolderObj;
        UnityEngine.Object? _excludeFolderObj;
        // Signature caching for gating manual version creation
        struct BackupSignature { public long size; public int count; }
        BackupSignature? _cachedCurrentSig; double _cachedCurrentSigTime;
        GUIStyle? _selectedTitleStyle; // enlarged style for selected version title
        List<VersionInfo>? _cachedVersions; // cached index
        int _renamingId = -1; string _renameBuffer = string.Empty; // rename state
        int _selectedVersionId = -1; // selected version id (default latest)
        // Doppio click tracking per apertura rapida preview
        int _lastClickId = -1; double _lastClickTime = -1;
        double _lastManualRunTime = -1; // throttle manual backup
        // Textures (Resources)
        static readonly Color BadgeColorCheckpoint = new Color(0.23f, 0.46f, 0.80f, 0.18f);
        static readonly Color BadgeColorIncremental = new Color(0.45f, 0.35f, 0.78f, 0.18f);
        static readonly Color BadgeColorChanged = new Color(0.77f, 0.55f, 0.16f, 0.22f);
        static readonly Color BadgeColorRemoved = new Color(0.75f, 0.25f, 0.25f, 0.22f);
        const float BadgeWidth = 128f;
        const int ManualCheckpointMax = 12;
        static GUIStyle? _badgeStyle;
        static Texture2D? _texExplorer; static bool _texTried;
        static bool _forceBackupTabOnOpen;
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
            _forceBackupTabOnOpen = true;
            var w = GetWindow<AvatarSmartBackupWindow>(true, AvatarSmartBackup.Localization.L.T("window.main.title", "Avatar Smart Backup"));
            w.minSize = new Vector2(320, 320);
            w.Show();
        }

        [MenuItem("Avatar Smart Backup/Restart Onboarding", false, 20)]
        public static void RestartOnboarding()
        {
            var settings = BackupManager.LoadSettings();
            settings.onboardingCompleted = false;
            BackupManager.SaveSettings(settings);
            TimerService.InvalidateSettingsCache();
            Open();
        }

        [MenuItem("Avatar Smart Backup/About", false, 100)]
        public static void ShowAbout()
        {
            // Per ora usamo valori statici - TODO: integrare PackageInfo quando risolviamo i namespace
            var packageName = "Avatar Smart Backup";
            var version = "0.2.0";
            var description = "Incremental backup for VRChat projects.";
            var author = "Dummics";
            var packageId = "dum.incb.system";

            var message = $"{packageName}\n\n" +
                         $"Version: {version}\n" +
                         $"Package ID: {packageId}\n" +
                         $"Author: {author}\n\n" +
                         $"{description}";

            UnityEditor.EditorUtility.DisplayDialog("About Avatar Smart Backup", message, "OK");
        }
        void OnEnable()
        {
            var loaded = BackupManager.LoadSettings();
            if (loaded != null)
            {
                _settings = loaded;
            }
            if (_forceBackupTabOnOpen)
            {
                _settings._activeTab = 0;
                _settings._uiTabInitialized = true;
                _forceBackupTabOnOpen = false;
            }
            RunOnboardingIfNeeded();
            BackupEvents.BackupCompleted += OnBackupCompleted;
        }
        void OnDisable()
        {
            BackupEvents.BackupCompleted -= OnBackupCompleted;
            BackupManager.SaveSettings(_settings);
            TimerService.InvalidateSettingsCache();
        }

        void OpenPreviewRestore(VersionInfo info)
        {
            if (info == null) return;

            try
            {
                RestorePreviewWindow.Open(info.id);
            }
            catch (Exception ex)
            {
                Log.Warn("Preview & Restore failed: " + ex.Message);
                var message = string.Format(AvatarSmartBackup.Localization.L.T("vc.error.body", "Unable to read changes:\n{0}"), ex.Message);
                EditorUtility.DisplayDialog(AvatarSmartBackup.Localization.L.T("vc.error.title", "Preview & Restore"), message, "OK");
            }
        }
        void OnBackupCompleted(BackupRunSummary summary)
        {
            _cachedVersions = null;
            _cachedCurrentSig = null;
            _cachedCurrentSigTime = 0;
            Repaint();
        }
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
            DrawModeSelector();
            _settings.AdvancedMode = !_settings.easyMode;
            DrawSchedulerSection();
            DrawPrimaryActions();
            DrawVersionsOverview();
            EditorGUILayout.Space();
            if (_settings.easyMode)
            {
                DrawEasyModeFooter();
                return;
            }
            DrawAdvancedOverview();
            DrawAdvancedSettings();
        }
        void RunOnboardingIfNeeded()
        {
            var loaded = BackupManager.LoadSettings();
            if (loaded != null)
                _settings = loaded;

            if (_settings.onboardingCompleted)
                return;

            bool awaitingChoice = true;
            while (awaitingChoice)
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    AvatarSmartBackup.Localization.L.T("onboarding.title", "Welcome to Avatar Smart Backup"),
                    AvatarSmartBackup.Localization.L.T("onboarding.body", "Choose the experience that fits you best. You can change it later from the mode selector."),
                    AvatarSmartBackup.Localization.L.T("ui.mode.easy", "Easy"),
                    AvatarSmartBackup.Localization.L.T("ui.mode.advanced", "Advanced"),
                    AvatarSmartBackup.Localization.L.T("onboarding.learn", "Learn more"));

                if (choice == 2)
                {
                    EditorUtility.DisplayDialog(
                        AvatarSmartBackup.Localization.L.T("onboarding.title", "Welcome to Avatar Smart Backup"),
                        AvatarSmartBackup.Localization.L.T("onboarding.learn.body", "Easy mode keeps the essentials. Advanced mode exposes all tools."),
                        "OK");
                    continue;
                }

                _settings.easyMode = (choice == 0);
                _settings.AdvancedMode = !_settings.easyMode;
                awaitingChoice = false;
            }

            _settings.onboardingCompleted = true;
            BackupManager.SaveSettings(_settings);
            TimerService.InvalidateSettingsCache();
        }

        void DrawModeSelector()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.mode.label", "Mode"), GUILayout.Width(60));
            string[] modes = { AvatarSmartBackup.Localization.L.T("ui.mode.easy", "Easy"), AvatarSmartBackup.Localization.L.T("ui.mode.advanced", "Advanced") };
            int current = _settings.easyMode ? 0 : 1;
            int newMode = GUILayout.Toolbar(current, modes, GUILayout.Width(200));
            EditorGUILayout.EndHorizontal();
            if (newMode != current)
            {
                _settings.easyMode = newMode == 0;
                _settings.AdvancedMode = !_settings.easyMode;
            }
        }

        void DrawEasyModeFooter()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.easy.summary", "Easy mode shows the essentials. Switch to Advanced for detailed controls."), EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button(AvatarSmartBackup.Localization.L.T("ui.easy.switch", "Switch to Advanced"), GUILayout.Width(200)))
            {
                _settings.easyMode = false;
                _settings.AdvancedMode = true;
            }
            EditorGUILayout.EndVertical();
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
            DrawDiskStatusRow();
            if (_settings.filtersDirty && _settings.easyMode)
            {
                EditorGUILayout.HelpBox(AvatarSmartBackup.Localization.L.T("ui.filters.pending", "Filter changes pending. The next backup will create a full checkpoint to apply the new scope."), MessageType.Info);
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
        void DrawDiskStatusRow()
        {
            var report = DiskSpaceMonitor.LastReport;
            if (report.Status == DiskSpaceStatus.Unknown)
                return;

            var snapshot = report.Snapshot;
            if (snapshot.totalBytes <= 0)
                return;

            string summary = $"Disk free: {DiskSpaceMonitor.FormatBytes(snapshot.freeBytes)} / {DiskSpaceMonitor.FormatBytes(snapshot.totalBytes)}";
            summary += $" � Backups: {DiskSpaceMonitor.FormatBytes(snapshot.backupSizeBytes)}";
            if (report.RequiredBytes > 0 && report.Stage != DiskSpaceStage.PostBackup)
            {
                summary += $" � Next estimate: {DiskSpaceMonitor.FormatBytes(report.RequiredBytes)}";
            }

            MessageType type = report.Status switch
            {
                DiskSpaceStatus.Warning => MessageType.Warning,
                DiskSpaceStatus.Critical => MessageType.Error,
                DiskSpaceStatus.Error => MessageType.Warning,
                _ => MessageType.Info
            };

            EditorGUILayout.HelpBox(summary, type);
        }

        void DrawIntervalControls()
        {
            EditorGUILayout.BeginHorizontal();
            bool changed = false;
            string[] unitOptions = { AvatarSmartBackup.Localization.L.T("ui.interval.min", "min"), AvatarSmartBackup.Localization.L.T("ui.interval.sec", "sec") };
            int currentUnitIndex = _settings.intervalInSeconds ? 1 : 0;
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.interval.label", "Interval"), GUILayout.Width(60));
            int maxValue = _settings.intervalInSeconds ? 3600 : 240;
            int newInterval = Mathf.Clamp(EditorGUILayout.IntField(_settings.intervalMinutes, GUILayout.Width(70)), 1, maxValue);
            if (newInterval != _settings.intervalMinutes)
            {
                _settings.intervalMinutes = newInterval;
                changed = true;
            }
            int newUnitIndex = EditorGUILayout.Popup(currentUnitIndex, unitOptions, GUILayout.Width(50));
            if (newUnitIndex != currentUnitIndex)
            {
                if (newUnitIndex == 1 && !_settings.intervalInSeconds)
                {
                    _settings.intervalMinutes = Mathf.Max(1, _settings.intervalMinutes * 60);
                    changed = true;
                }
                else if (newUnitIndex == 0 && _settings.intervalInSeconds)
                {
                    _settings.intervalMinutes = Mathf.Max(1, Mathf.RoundToInt(_settings.intervalMinutes / 60f));
                    changed = true;
                }
                _settings.intervalInSeconds = (newUnitIndex == 1);
                changed = true;
            }
            EditorGUILayout.EndHorizontal();
            if (changed)
            {
                TimerService.InvalidateSettingsCache();
                if (Session.IsRunning)
                {
                    TimerService.ScheduleNextRun(_settings);
                }
                Repaint();
            }
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
                        OpenPreviewRestore(latest);
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
                string desc = SanitizeInlineLabel(latest.description, AvatarSmartBackup.Localization.L.T("ui.overview.noDescription", "(no description)"));
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
                var selectedTitleStyle = _selectedTitleStyle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = EditorStyles.boldLabel.fontSize + 1 };
                EnsureTextures();
                // Begin card content
                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent(v.pinned ? "★" : "☆", v.pinned ? "Unpin" : "Pin"), GUILayout.Width(24))) pendingTogglePin = v.id;
                Rect starRect = GUILayoutUtility.GetLastRect(); // rect of the star button
                string title = BuildVersionCardTitle(v, latest != null && latest.id == v.id);
                EditorGUILayout.LabelField(title, isSelected ? selectedTitleStyle : EditorStyles.boldLabel);
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
                DrawVersionBadges(v);
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
                    if (GUILayout.Button(new GUIContent("Restore", "Open preview and proceed to restore"), GUILayout.Height(22))) OpenPreviewRestore(v);
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
                        OpenPreviewRestore(v);
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
                { if (!starRect.Contains(e.mousePosition) && !folderBtnRect.Contains(e.mousePosition)) { double now = EditorApplication.timeSinceStartup; bool db = (_lastClickId == v.id) && (now - _lastClickTime < 0.35f); _lastClickId = v.id; _lastClickTime = now; _selectedVersionId = v.id; GUI.FocusControl(""); Repaint(); if (db) OpenPreviewRestore(v); e.Use(); } }
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

        static string SanitizeInlineLabel(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (ch == '\r' || ch == '\n')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                        sb.Append(' ');
                    continue;
                }
                if (!char.IsControl(ch))
                    sb.Append(ch);
            }

            var result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? fallback : result;
        }

        static string BuildVersionCardTitle(VersionInfo version, bool isLatest)
        {
            if (version == null) return string.Empty;

            string fallback = AvatarSmartBackup.Localization.L.T("ui.overview.noDescription", "(no description)");
            string description = SanitizeInlineLabel(version.description, fallback);
            string baseTitle = $"#{version.id}  {description}";

            if (version.incomplete)
                baseTitle += "  " + AvatarSmartBackup.Localization.L.T("ui.versions.status.writing", "(writing...)");

            if (isLatest)
            {
                string latestLabel = AvatarSmartBackup.Localization.L.T("ui.versions.latestBadge", "Latest");
                return $"{latestLabel} - {baseTitle}";
            }

            return baseTitle;
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

        void DrawVersionBadges(VersionInfo info)
        {
            if (info == null) return;
            EditorGUILayout.BeginHorizontal();
            DrawBadge(info.isCheckpoint ? BadgeColorCheckpoint : BadgeColorIncremental, info.isCheckpoint ? AvatarSmartBackup.Localization.L.T("vc.badge.checkpoint", "Checkpoint") : AvatarSmartBackup.Localization.L.T("vc.badge.incremental", "Incremental"));
            if (info.changedFileCount > 0)
                DrawBadge(BadgeColorChanged, string.Format(AvatarSmartBackup.Localization.L.T("vc.badge.changed", "Changed: {0}"), info.changedFileCount));
            if (info.removedFileCount > 0)
                DrawBadge(BadgeColorRemoved, string.Format(AvatarSmartBackup.Localization.L.T("vc.badge.removed", "Removed: {0}"), info.removedFileCount));
            EditorGUILayout.EndHorizontal();
        }

        void DrawBadge(Color tint, string text)
        {
            Rect rect = GUILayoutUtility.GetRect(BadgeWidth, 20f, BadgeStyle, GUILayout.MaxWidth(BadgeWidth));
            EditorGUI.DrawRect(rect, tint);
            var labelRect = new Rect(rect.x + 6, rect.y + 2, rect.width - 12, rect.height - 4);
            GUI.Label(labelRect, text, BadgeStyle);
        }

        static GUIStyle BadgeStyle
        {
            get
            {
                var style = _badgeStyle ??= new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter };
                return style;
            }
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


        void TriggerSnapshotRebuild(VersionInfo info)
        {
            if (info == null) return;
            try
            {
                var path = VersionRestoreService.PrepareSnapshot(info.id, forceRebuild: true);
                if (string.IsNullOrEmpty(path))
                {
                    Log.Warn($"Snapshot rebuild returned no path for version #{info.id}.");
                    EditorUtility.DisplayDialog("Rebuild snapshot", "Unable to rebuild version: snapshot path unavailable.", "OK");
                    return;
                }
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
            _settings.showAdvanced = EditorGUILayout.Foldout(_settings.showAdvanced, AvatarSmartBackup.Localization.L.T("ui.advanced.settings", "Advanced Settings"));
            if (!_settings.showAdvanced) return;

            _settings.EnsureVersioningDefaults();
            _settings.SyncLegacyCheckpointInterval();

            DrawVersioningPolicySection();
            EditorGUILayout.Space(4f);
            DrawPerformanceSection();
            EditorGUILayout.Space(4f);
            DrawScopeSection();
            EditorGUILayout.Space(4f);
            DrawDebugToolsSection();
        }

        void DrawVersioningPolicySection()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.versioning.policy.title", "Versioning policy"), EditorStyles.boldLabel);

            var policies = (VersioningPolicy[])Enum.GetValues(typeof(VersioningPolicy));
            var labels = new[]
            {
                AvatarSmartBackup.Localization.L.T("ui.versioning.policy.option.balanced", "Automatic"),
                AvatarSmartBackup.Localization.L.T("ui.versioning.policy.option.frequent", "Frequent"),
                AvatarSmartBackup.Localization.L.T("ui.versioning.policy.option.manual", "Manual"),
            };
            int currentIndex = Array.IndexOf(policies, _settings.versioningPolicy);
            if (currentIndex < 0) currentIndex = 0;

            GUIContent presetLabel = AvatarSmartBackup.Localization.L.C("ui.versioning.policy.mode", "Preset", "ui.versioning.policy.mode.tooltip", "Choose how often checkpoints are forced.");
            int newIndex = EditorGUILayout.Popup(presetLabel, currentIndex, labels);
            if (newIndex != currentIndex && newIndex >= 0 && newIndex < policies.Length)
            {
                _settings.versioningPolicy = policies[newIndex];
            }

            switch (_settings.versioningPolicy)
            {
                case VersioningPolicy.Frequent:
                    EditorGUILayout.HelpBox(AvatarSmartBackup.Localization.L.T("ui.versioning.policy.frequent.help", "Favors frequent checkpoints after significant changes."), MessageType.Info);
                    break;
                case VersioningPolicy.Manual:
                {
                    int manual = Mathf.Clamp(_settings.manualCheckpointFrequency, 1, ManualCheckpointMax);
                    manual = EditorGUILayout.IntSlider(AvatarSmartBackup.Localization.L.C("ui.versioning.policy.manual.label", "Checkpoint every (incremental versions)", "ui.versioning.policy.manual.tooltip", "Number of incremental versions before forcing a full checkpoint."), manual, 1, ManualCheckpointMax);
                    _settings.manualCheckpointFrequency = manual;
                    EditorGUILayout.HelpBox(string.Format(AvatarSmartBackup.Localization.L.T("ui.versioning.policy.manual.help", "Forces a full checkpoint after {0} incremental versions."), manual), MessageType.None);
                    break;
                }
                default:
                    EditorGUILayout.HelpBox(AvatarSmartBackup.Localization.L.T("ui.versioning.policy.balanced.help", "Automatic checkpoints based on project activity."), MessageType.Info);
                    break;
            }

            _settings.showRebuildTool = EditorGUILayout.ToggleLeft(new GUIContent("Show rebuild index tool", "Enable manual rebuild of the versions index from the Versions tab."), _settings.showRebuildTool);

            _settings.EnsureVersioningDefaults();
            _settings.SyncLegacyCheckpointInterval();
            EditorGUILayout.EndVertical();
        }

        void DrawPerformanceSection()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.performance.title", "Performance"), EditorStyles.boldLabel);

            _settings.autoThrottle = EditorGUILayout.ToggleLeft(AvatarSmartBackup.Localization.L.C("ui.performance.autoThrottle", "Auto throttle (recommended)", "ui.performance.autoThrottle.tt", "Automatically cap disk throughput to keep the editor responsive."), _settings.autoThrottle);

            int maxThreads = Math.Max(1, Environment.ProcessorCount);
            _settings.maxParallelThreads = Mathf.Clamp(EditorGUILayout.IntField(AvatarSmartBackup.Localization.L.C("ui.performance.threads", "Max parallel threads", "ui.performance.threads.tt", "Concurrent worker threads for hashing and copying."), _settings.maxParallelThreads), 1, maxThreads);

            _settings.saveScenesBeforeBackup = EditorGUILayout.ToggleLeft(AvatarSmartBackup.Localization.L.C("ui.performance.saveScenes", "Save open scenes before backup", "ui.performance.saveScenes.tt", "Save dirty scenes before running a backup."), _settings.saveScenesBeforeBackup);

            if (!_settings.autoThrottle)
            {
                EditorGUILayout.HelpBox(AvatarSmartBackup.Localization.L.T("ui.performance.autothrottle.off", "Auto throttle is disabled. Manual speed caps will be used if set."), MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        void DrawScopeSection()
        {
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
                    v = Math.Max(128L, Math.Min(v, 1024L * 10L));
                    _settings.materialsMaxKB = v;
                }
            }
            _settings.incDlls = EditorGUILayout.ToggleLeft(new GUIContent("DLLs (.dll) under size limit", "Managed DLLs and native plugins."), _settings.incDlls);
            using (new EditorGUI.DisabledScope(!_settings.incDlls))
            {
                string[] dNames = { "256 KB", "512 KB", "1 MB", "2 MB", "4 MB", "Custom" };
                long[] dValues = { 256, 512, 1024, 2048, 4096, -1 };
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

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Folders & Types", EditorStyles.boldLabel);
            bool filtersChanged = false;
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Add folders from Project or simple extensions (e.g., .prefab).", EditorStyles.miniLabel);
            EditorGUI.BeginChangeCheck();
            _settings.extWithinIncludeFolders = EditorGUILayout.ToggleLeft(new GUIContent("Apply extensions only within included folders", "When enabled, extensions like .prefab are searched only inside the folders you included."), _settings.extWithinIncludeFolders);
            if (EditorGUI.EndChangeCheck()) filtersChanged = true;
            EditorGUILayout.EndVertical();
            filtersChanged |= DrawIncludeExcludeSection("Include", _settings.includeFolders, ref _newIncludePattern, ref _includePrefix, ref _includeFolderObj);
            EditorGUILayout.Space(6);
            filtersChanged |= DrawIncludeExcludeSection("Exclude", _settings.excludeFolders, ref _newExcludePattern, ref _excludePrefix, ref _excludeFolderObj);
            DrawTrackedSelectionSection();
            if (filtersChanged) FlagFiltersChanged();
            if (_settings.filtersDirty)
            {
                EditorGUILayout.HelpBox(AvatarSmartBackup.Localization.L.T("ui.filters.pending", "Filter changes pending. The next backup will create a full checkpoint to apply the new scope."), MessageType.Info);
            }
            EditorGUILayout.EndVertical();
        }

        void DrawDebugToolsSection()
        {
            EditorGUILayout.BeginVertical("box");
            _settings.showDebugTools = EditorGUILayout.Foldout(_settings.showDebugTools, AvatarSmartBackup.Localization.L.T("ui.debug.tools.title", "Debug tools"), true);
            if (_settings.showDebugTools)
            {
                EditorGUI.indentLevel++;
                bool newDebug = EditorGUILayout.ToggleLeft(AvatarSmartBackup.Localization.L.C("ui.debug.tools.enable", "Enable debug tools", "ui.debug.tools.enable.tt", "Turn on diagnostics and manual overrides."), _settings.debugMode);
                if (newDebug != _settings.debugMode)
                {
                    _settings.debugMode = newDebug;
                    if (!_settings.debugMode)
                        _settings.enableDebugLogging = false;
                }

                if (_settings.debugMode)
                {
                    bool useGlobal = !_settings.useProjectSettings;
                    bool newUseGlobal = EditorGUILayout.ToggleLeft(AvatarSmartBackup.Localization.L.C("ui.debug.tools.global", "Use global settings", "ui.debug.tools.global.tt", "Share this configuration across projects."), useGlobal);
                    if (newUseGlobal != useGlobal)
                    {
                        _settings.useProjectSettings = !newUseGlobal;
                        BackupManager.SaveSettings(_settings);
                        TimerService.InvalidateSettingsCache();
                        _settings = BackupManager.LoadSettings();
                    }

                    if (_settings.lastMeasuredMBps > 0f)
                    {
                        EditorGUILayout.LabelField(string.Format(AvatarSmartBackup.Localization.L.T("ui.debug.tools.throughput", "Measured throughput: {0:0.0} MB/s"), _settings.lastMeasuredMBps), EditorStyles.miniLabel);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(AvatarSmartBackup.Localization.L.T("ui.debug.tools.throughput.none", "Benchmark has not been run yet."), MessageType.None);
                    }

                    using (new EditorGUI.DisabledScope(BackupManager.IsBusy))
                    {
                        if (GUILayout.Button(AvatarSmartBackup.Localization.L.C("ui.debug.tools.benchmark", "Run benchmark again"), GUILayout.Width(170)))
                        {
                            _ = BackupManager.RunManualBenchmarkAsync(_settings);
                        }
                    }
                    if (BackupManager.IsBusy)
                    {
                        EditorGUILayout.HelpBox(AvatarSmartBackup.Localization.L.T("ui.debug.tools.benchmark.busy", "Benchmark unavailable while a backup is running."), MessageType.Info);
                    }

                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.debug.tools.cooldowns", "Manual cooldowns"), EditorStyles.boldLabel);

                    DrawCooldownRow("ui.debug.tools.snapshot.cooldown", "Snapshot cooldown (s)", "ui.debug.tools.snapshot.cooldown.tt", "Minimum seconds between manual snapshots.", ref _settings.manualSnapshotCooldownSeconds);
                    DrawCooldownRow("ui.debug.tools.benchmark.cooldown", "Benchmark cooldown (s)", "ui.debug.tools.benchmark.cooldown.tt", "Minimum seconds between manual benchmark runs.", ref _settings.manualBenchmarkCooldownSeconds);
                    DrawCooldownRow("ui.debug.tools.backup.cooldown", "Min backup interval (s)", "ui.debug.tools.backup.cooldown.tt", "Minimum seconds between manual backup requests.", ref _settings.minManualBackupIntervalSeconds);

                    _settings.enableDebugLogging = EditorGUILayout.ToggleLeft(AvatarSmartBackup.Localization.L.C("ui.debug.tools.logs", "Detailed file logging", "ui.debug.tools.logs.tt", "Write verbose file logs for troubleshooting."), _settings.enableDebugLogging);
                    EditorGUILayout.HelpBox(AvatarSmartBackup.Localization.L.T("ui.debug.tools.snapshots.info", "Zip snapshots are managed automatically. Legacy settings remain available for compatibility."), MessageType.None);
                }
                else
                {
                    EditorGUILayout.HelpBox(AvatarSmartBackup.Localization.L.T("ui.debug.tools.disabled", "Enable to access diagnostics and manual overrides."), MessageType.None);
                }

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndVertical();
        }

        void DrawCooldownRow(string labelKey, string labelFallback, string tooltipKey, string tooltipFallback, ref int value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.C(labelKey, labelFallback, tooltipKey, tooltipFallback), GUILayout.Width(200));
            value = Mathf.Clamp(EditorGUILayout.IntField(value, GUILayout.Width(60)), 1, 3600);
            EditorGUILayout.EndHorizontal();
        }




        void DrawTrackedSelectionSection()
        {
            if (_settings.trackedRoots == null)
                _settings.trackedRoots = new List<string>();

            var tracked = _settings.trackedRoots;
            bool frozen = BackupManager.IsSelectionFrozen(_settings);

            if (tracked.Count == 0 && !frozen)
                return; // nothing legacy to show

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Tracked Selection", EditorStyles.boldLabel);

            if (tracked.Count == 0)
            {
                EditorGUILayout.LabelField("Tracking all assets allowed by filters.", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Selection locked once versions exist.", EditorStyles.miniBoldLabel);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.LabelField("Legacy entries kept for compatibility (read-only).", EditorStyles.wordWrappedMiniLabel);
            foreach (var entry in tracked)
            {
                EditorGUILayout.LabelField(entry, EditorStyles.miniLabel);
            }
            EditorGUILayout.HelpBox("Use Folders & Types to adjust what gets backed up. Tracked selection editing is deprecated.", MessageType.Info);
            EditorGUILayout.EndVertical();
        }

        void FlagFiltersChanged()
        {
            if (!_settings.filtersDirty)
            {
                _settings.filtersDirty = true;
                _settings.filtersChangedTicks = DateTime.UtcNow.Ticks;
            }
        }

        static bool DrawIncludeExcludeSection(string title, List<string> list, ref string newExt, ref string newPrefix, ref UnityEngine.Object? folderObj)
        {
            bool changed = false;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(title + $":  (" + list.Count + ")", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(list.Count == 0))
            {
                if (GUILayout.Button(new GUIContent("Clear", "Remove all entries from this list"), GUILayout.Width(60)))
                {
                    list.Clear();
                    changed = true;
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Folder", GUILayout.Width(60));
            var newObj = EditorGUILayout.ObjectField(folderObj, typeof(DefaultAsset), false);
            if (newObj != folderObj) folderObj = newObj;
            using (new EditorGUI.DisabledScope(folderObj == null))
            {
                if (GUILayout.Button("Add", GUILayout.Width(60)))
                {
                    if (folderObj != null)
                    {
                        string p = AssetDatabase.GetAssetPath(folderObj);
                        if (string.IsNullOrEmpty(p) || !AssetDatabase.IsValidFolder(p))
                        {
                            EditorUtility.DisplayDialog("Not a folder", "Please select a folder inside the Project window.", "OK");
                        }
                        else
                        {
                            if (!p.EndsWith("/")) p += "/";
                            if (!list.Contains(p))
                            {
                                list.Add(p);
                                changed = true;
                            }
                            folderObj = null;
                        }
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
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
                    if (!list.Contains(t))
                    {
                        list.Add(t);
                        changed = true;
                    }
                    newExt = string.Empty;
                }
            }
            EditorGUILayout.EndHorizontal();
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
                    if (!list.Contains(t))
                    {
                        list.Add(t);
                        changed = true;
                    }
                    newPrefix = string.Empty;
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            DrawThinSeparator();
            int remove = -1;
            for (int i = 0; i < list.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("", GUILayout.Width(2));
                var before = list[i];
                var after = EditorGUILayout.TextField(before, GUILayout.ExpandWidth(true));
                if (!string.Equals(before, after, StringComparison.Ordinal))
                {
                    list[i] = after;
                    changed = true;
                }
                if (GUILayout.Button("X", GUILayout.Width(20))) remove = i;
                EditorGUILayout.EndHorizontal();
            }
            if (remove >= 0)
            {
                list.RemoveAt(remove);
                changed = true;
            }
            return changed;
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











