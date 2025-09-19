#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup.Backup
{
    internal class BackupSharedView
    {
        const int ManualCheckpointMax = 12;
        readonly BackupWindowContext _context;

        public BackupSharedView(BackupWindowContext context)
        {
            _context = context;
        }

        public void DrawModeSelector()
        {
            _context.RecordLayoutMarker("ModeSelector");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.mode.label", "Mode"), GUILayout.Width(60));
            string[] modes = { AvatarSmartBackup.Localization.L.T("ui.mode.easy", "Easy"), AvatarSmartBackup.Localization.L.T("ui.mode.advanced", "Advanced") };
            int current = _context.Settings.easyMode ? 0 : 1;
            int newMode = GUILayout.Toolbar(current, modes, GUILayout.Width(200));
            EditorGUILayout.EndHorizontal();
            if (newMode != current)
            {
                _context.Settings.easyMode = newMode == 0;
                _context.Settings.AdvancedMode = !_context.Settings.easyMode;
            }
        }

        public void DrawSchedulerSection()
        {
            _context.RecordLayoutMarker("SchedulerSection");
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.autobackups.title", "Automatic Backups"), EditorStyles.boldLabel);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            bool running = Session.IsRunning;
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = running ? new Color(0.25f, 0.55f, 0.25f, 1f) : new Color(0.45f, 0.2f, 0.2f, 1f);
            if (GUILayout.Button(new GUIContent(running ? AvatarSmartBackup.Localization.L.T("ui.autobackups.on", "Automatic Backups: ON") : AvatarSmartBackup.Localization.L.T("ui.autobackups.off", "Automatic Backups: OFF"), AvatarSmartBackup.Localization.L.T("ui.autobackups.toggle.tt", "Toggle background backup scheduler")), GUILayout.Width(220), GUILayout.Height(30)))
            {
                _context.ToggleScheduler();
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
            if (_context.Settings.filtersDirty && _context.Settings.easyMode)
            {
                using (_context.Info(MessageType.Info, AvatarSmartBackup.Localization.L.T("ui.filters.pending", "Filter changes pending. The next backup will create a full checkpoint to apply the new scope.")))
                {
                    GUILayout.Space(2);
                }
            }
            if (_context.Settings.AdvancedMode)
            {
                EditorGUILayout.Space(4);
                DrawIntervalControls();
                int effCopy = BackupManager.EffectiveCopyMBps(_context.Settings);
                if (_context.Settings.lastBackupBytes > 0 && effCopy > 0)
                {
                    double secNeeded = _context.Settings.lastBackupBytes / (effCopy * 1024.0 * 1024.0);
                    double intervalSeconds = _context.Settings.intervalInSeconds ? _context.Settings.intervalMinutes : _context.Settings.intervalMinutes * 60.0;
                    if (secNeeded > intervalSeconds)
                    {
                        double minutesNeeded = secNeeded / 60.0;
                        double mb = _context.Settings.lastBackupBytes / (1024.0 * 1024.0);
                        using (_context.Info(MessageType.Warning, AvatarSmartBackup.Localization.L.T("warn.backup.speed", "At {0} MB/s, backing up {1:0.0} MB takes ~{2:0.0} min, exceeding the interval.", effCopy, mb, minutesNeeded)))
                        {
                            GUILayout.Space(2);
                        }
                    }
                }
            }
            else
            {
                EditorGUILayout.Space(4);
                DrawIntervalControls();
            }
            EditorGUILayout.EndVertical();
        }

        void DrawDiskStatusRow()
        {
            var report = DiskSpaceMonitor.LastReport;
            if (report.Status == DiskSpaceStatus.Unknown)
                return;

            var snapshot = report.Snapshot;
            if (snapshot.totalBytes <= 0)
                return;

            string summary = $"Disk free: {DiskSpaceMonitor.FormatBytes(snapshot.freeBytes)} / {DiskSpaceMonitor.FormatBytes(snapshot.totalBytes)}";
            summary += $" • Backups: {DiskSpaceMonitor.FormatBytes(snapshot.backupSizeBytes)}";
            if (report.RequiredBytes > 0 && report.Stage != DiskSpaceStage.PostBackup)
            {
                summary += $" • Next estimate: {DiskSpaceMonitor.FormatBytes(report.RequiredBytes)}";
            }

            MessageType type = report.Status switch
            {
                DiskSpaceStatus.Warning => MessageType.Warning,
                DiskSpaceStatus.Critical => MessageType.Error,
                DiskSpaceStatus.Error => MessageType.Warning,
                _ => MessageType.Info
            };

            using (_context.Info(type, summary))
            {
                GUILayout.Space(2);
            }
        }

        public void DrawIntervalControls()
        {
            EditorGUILayout.BeginHorizontal();
            bool changed = false;
            string[] unitOptions = { AvatarSmartBackup.Localization.L.T("ui.interval.min", "min"), AvatarSmartBackup.Localization.L.T("ui.interval.sec", "sec") };
            int currentUnitIndex = _context.Settings.intervalInSeconds ? 1 : 0;
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.interval.label", "Interval"), GUILayout.Width(60));
            int maxValue = _context.Settings.intervalInSeconds ? 3600 : 240;
            int newInterval = Mathf.Clamp(EditorGUILayout.IntField(_context.Settings.intervalMinutes, GUILayout.Width(70)), 1, maxValue);
            if (newInterval != _context.Settings.intervalMinutes)
            {
                _context.Settings.intervalMinutes = newInterval;
                changed = true;
            }
            int newUnitIndex = EditorGUILayout.Popup(currentUnitIndex, unitOptions, GUILayout.Width(50));
            if (newUnitIndex != currentUnitIndex)
            {
                if (newUnitIndex == 1 && !_context.Settings.intervalInSeconds)
                {
                    _context.Settings.intervalMinutes = Mathf.Max(1, _context.Settings.intervalMinutes * 60);
                    changed = true;
                }
                else if (newUnitIndex == 0 && _context.Settings.intervalInSeconds)
                {
                    _context.Settings.intervalMinutes = Mathf.Max(1, Mathf.RoundToInt(_context.Settings.intervalMinutes / 60f));
                    changed = true;
                }
                _context.Settings.intervalInSeconds = (newUnitIndex == 1);
                changed = true;
            }
            EditorGUILayout.EndHorizontal();
            if (changed)
            {
                TimerService.InvalidateSettingsCache();
                if (Session.IsRunning)
                {
                    TimerService.ScheduleNextRun(_context.Settings);
                }
                _context.RequestRepaint();
            }
        }

        public void DrawPrimaryActions()
        {
            _context.RecordLayoutMarker("PrimaryActions");
            const double BackupManualCooldownSeconds = 30;
            double now = EditorApplication.timeSinceStartup;
            bool throttle = !_context.Settings.AdvancedMode;
            bool canManual = true;
            string tooltip = "Run a backup now (30s cooldown)";
            if (throttle && _context.ShouldThrottleManualBackup)
            {
                canManual = false;
                double rem = _context.RemainingManualCooldown;
                tooltip = $"Wait {rem:0}s before another manual backup";
            }
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!canManual || BackupManager.IsBusy))
            {
                string backupTooltip = _context.Settings.AdvancedMode ? AvatarSmartBackup.Localization.L.T("tt.backup.now.advanced", "Run a backup now (no cooldown in Advanced Mode)") : tooltip;
                if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.backup.now", "Backup Now"), backupTooltip)))
                {
                    _context.RunBackupNow();
                }
            }
            _context.EnsureVersionsCache();
            var latest = _context.GetLatestVersionCached();
            using (new EditorGUI.DisabledScope(latest == null))
            {
                string restoreTooltip = latest == null ? AvatarSmartBackup.Localization.L.T("tt.latest.none", "No version available") : AvatarSmartBackup.Localization.L.T("tt.latest.open", "Open the latest version to restore or inspect");
                if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.latest.previewRestore", "Preview & Restore latest"), restoreTooltip)))
                {
                    if (latest != null)
                        _context.OpenPreviewRestore(latest);
                }
            }
            if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.open.backup.folder", "Open Backup Folder"), AvatarSmartBackup.Localization.L.T("tt.open.backup.folder", "Open the backups folder"))))
            {
                EditorUtility.RevealInFinder(FileUtilEx.BackupRoot);
            }
            EditorGUILayout.EndHorizontal();
        }

        public void DrawVersionsOverview()
        {
            _context.RecordLayoutMarker("VersionsOverview");
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.overview.latest.title", "Latest Version"), EditorStyles.boldLabel);
            _context.EnsureVersionsCache();
            var latest = _context.GetLatestVersionCached();
            if (latest != null)
            {
                EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.overview.latest.label", "Latest Version:"), EditorStyles.miniBoldLabel);
                string desc = _context.SanitizeInlineLabel(latest.description, AvatarSmartBackup.Localization.L.T("ui.overview.noDescription", "(no description)"));
                EditorGUILayout.LabelField($"# {latest.id}  {desc}", EditorStyles.miniLabel);
                var created = _context.ParseCreatedUtc(latest);
                if (created != DateTime.MinValue)
                    EditorGUILayout.LabelField($"{AvatarSmartBackup.Localization.L.T("ui.overview.created", "Created:")} {created:yyyy-MM-dd HH:mm:ss}", EditorStyles.miniLabel);
                {
                    var filesLbl = AvatarSmartBackup.Localization.L.T("ui.overview.filesSize", "Files");
                    var sizeLbl = AvatarSmartBackup.Localization.L.T("ui.overview.size", "Size");
                    EditorGUILayout.LabelField($"{filesLbl}: {latest.fileCount}  {sizeLbl}: {_context.FormatSize(latest.totalSizeBytes)}", EditorStyles.miniLabel);
                }
                if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.overview.gotoVersions", "Go to Versions"), AvatarSmartBackup.Localization.L.T("tt.overview.gotoVersions", "Open Versions tab")), GUILayout.Width(140)))
                {
                    _context.Settings._activeTab = 1;
                }
            }
            else
            {
                EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.overview.none", "No versions available"), EditorStyles.miniLabel);
            }
            EditorGUILayout.EndVertical();
        }

        public void DrawEasyModeFooter()
        {
            _context.RecordLayoutMarker("EasyFooter");
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.easy.summary", "Easy mode shows the essentials. Switch to Advanced for detailed controls."), EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button(AvatarSmartBackup.Localization.L.T("ui.easy.switch", "Switch to Advanced"), GUILayout.Width(200)))
            {
                _context.Settings.easyMode = false;
                _context.Settings.AdvancedMode = true;
            }
            EditorGUILayout.EndVertical();
        }

        public void DrawAdvancedOverview()
        {
            _context.RecordLayoutMarker("AdvancedOverview");
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.advanced.tools.title", "Advanced Tools"), EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.advanced.openLogFolder", "Open Log Folder"), AvatarSmartBackup.Localization.L.T("tt.advanced.openLogFolder", "Open the logs folder for support")), GUILayout.Width(150)))
            {
                string logDir = Log.GetLogDirectory();
                if (Directory.Exists(logDir)) EditorUtility.RevealInFinder(logDir);
                else EditorUtility.DisplayDialog(AvatarSmartBackup.Localization.L.T("dlg.log.title", "Log Folder"), AvatarSmartBackup.Localization.L.T("dlg.log.notfound", "Log folder not found."), "OK");
            }
            if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.advanced.refreshVersions", "Refresh Versions"), AvatarSmartBackup.Localization.L.T("tt.advanced.refreshVersions", "Reload the list of versions")), GUILayout.Width(150)))
            {
                _context.RefreshVersions();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        public void DrawAdvancedSettings()
        {
            _context.RecordLayoutMarker("AdvancedSettings");
            if (!_context.Settings.AdvancedMode) return;

            EditorGUILayout.Space(10);
            _context.Settings.showAdvanced = EditorGUILayout.Foldout(_context.Settings.showAdvanced, AvatarSmartBackup.Localization.L.T("ui.advanced.settings", "Advanced Settings"));
            if (!_context.Settings.showAdvanced) return;

            _context.Settings.EnsureVersioningDefaults();
            _context.Settings.SyncLegacyCheckpointInterval();

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
            _context.RecordLayoutMarker("VersioningPolicy");
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.versioning.policy.title", "Versioning policy"), EditorStyles.boldLabel);

            var policies = (VersioningPolicy[])Enum.GetValues(typeof(VersioningPolicy));
            var labels = new[]
            {
                AvatarSmartBackup.Localization.L.T("ui.versioning.policy.option.balanced", "Automatic"),
                AvatarSmartBackup.Localization.L.T("ui.versioning.policy.option.frequent", "Frequent"),
                AvatarSmartBackup.Localization.L.T("ui.versioning.policy.option.manual", "Manual"),
            };
            int currentIndex = Array.IndexOf(policies, _context.Settings.versioningPolicy);
            if (currentIndex < 0) currentIndex = 0;

            GUIContent presetLabel = AvatarSmartBackup.Localization.L.C("ui.versioning.policy.mode", "Preset", "ui.versioning.policy.mode.tooltip", "Choose how often checkpoints are forced.");
            int newIndex = EditorGUILayout.Popup(presetLabel, currentIndex, labels);
            if (newIndex != currentIndex && newIndex >= 0 && newIndex < policies.Length)
            {
                _context.Settings.versioningPolicy = policies[newIndex];
            }

            switch (_context.Settings.versioningPolicy)
            {
                case VersioningPolicy.Frequent:
                    using (_context.Info(MessageType.Info, AvatarSmartBackup.Localization.L.T("ui.versioning.policy.frequent.help", "Favors frequent checkpoints after significant changes.")))
                    {
                        GUILayout.Space(2);
                    }
                    break;
                case VersioningPolicy.Manual:
                {
                    int manual = Mathf.Clamp(_context.Settings.manualCheckpointFrequency, 1, ManualCheckpointMax);
                    manual = EditorGUILayout.IntSlider(AvatarSmartBackup.Localization.L.C("ui.versioning.policy.manual.label", "Checkpoint every (incremental versions)", "ui.versioning.policy.manual.tooltip", "Number of incremental versions before forcing a full checkpoint."), manual, 1, ManualCheckpointMax);
                    _context.Settings.manualCheckpointFrequency = manual;
                    using (_context.Info(MessageType.None, string.Format(AvatarSmartBackup.Localization.L.T("ui.versioning.policy.manual.help", "Forces a full checkpoint after {0} incremental versions."), manual)))
                    {
                        GUILayout.Space(2);
                    }
                    break;
                }
                default:
                    using (_context.Info(MessageType.Info, AvatarSmartBackup.Localization.L.T("ui.versioning.policy.balanced.help", "Automatic checkpoints based on project activity.")))
                    {
                        GUILayout.Space(2);
                    }
                    break;
            }

            _context.Settings.showRebuildTool = EditorGUILayout.ToggleLeft(new GUIContent("Show rebuild index tool", "Enable manual rebuild of the versions index from the Versions tab."), _context.Settings.showRebuildTool);

            _context.Settings.EnsureVersioningDefaults();
            _context.Settings.SyncLegacyCheckpointInterval();
            EditorGUILayout.EndVertical();
        }

        void DrawPerformanceSection()
        {
            _context.RecordLayoutMarker("PerformanceSection");
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.performance.title", "Performance"), EditorStyles.boldLabel);

            _context.Settings.autoThrottle = EditorGUILayout.ToggleLeft(AvatarSmartBackup.Localization.L.C("ui.performance.autoThrottle", "Auto throttle (recommended)", "ui.performance.autoThrottle.tt", "Automatically cap disk throughput to keep the editor responsive."), _context.Settings.autoThrottle);

            int maxThreads = Math.Max(1, Environment.ProcessorCount);
            _context.Settings.maxParallelThreads = Mathf.Clamp(EditorGUILayout.IntField(AvatarSmartBackup.Localization.L.C("ui.performance.threads", "Max parallel threads", "ui.performance.threads.tt", "Concurrent worker threads for hashing and copying."), _context.Settings.maxParallelThreads), 1, maxThreads);

            _context.Settings.saveScenesBeforeBackup = EditorGUILayout.ToggleLeft(AvatarSmartBackup.Localization.L.C("ui.performance.saveScenes", "Save open scenes before backup", "ui.performance.saveScenes.tt", "Save dirty scenes before running a backup."), _context.Settings.saveScenesBeforeBackup);

            if (!_context.Settings.autoThrottle)
            {
                using (_context.Info(MessageType.Info, AvatarSmartBackup.Localization.L.T("ui.performance.autothrottle.off", "Auto throttle is disabled. Manual speed caps will be used if set.")))
                {
                    GUILayout.Space(2);
                }
            }

            EditorGUILayout.EndVertical();
        }

        void DrawScopeSection()
        {
            _context.RecordLayoutMarker("ScopeSection");
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("What to include", EditorStyles.boldLabel);
            _context.Settings.incVRCAssets = EditorGUILayout.ToggleLeft(new GUIContent("VRC Expressions (.asset)", "Common VRC expression assets and similarly named .asset files."), _context.Settings.incVRCAssets);
            _context.Settings.incAnimControllers = EditorGUILayout.ToggleLeft(new GUIContent("Animator Controllers (.controller)", "Animator controller assets."), _context.Settings.incAnimControllers);
            _context.Settings.incAnimationClips = EditorGUILayout.ToggleLeft(new GUIContent("Animation Clips (.anim)", "Animation clip assets."), _context.Settings.incAnimationClips);
            _context.Settings.incScenes = EditorGUILayout.ToggleLeft(new GUIContent("Scenes (.unity)", "Unity scene files."), _context.Settings.incScenes);
            _context.Settings.incMaterials = EditorGUILayout.ToggleLeft(new GUIContent("Materials (.mat) under size limit", "Small material files. Larger ones are skipped by threshold."), _context.Settings.incMaterials);
            using (new EditorGUI.DisabledScope(!_context.Settings.incMaterials))
            {
                string[] mNames = { "256 KB", "512 KB", "1 MB", "2 MB", "4 MB", "Custom" };
                long[] mValues = { 256, 512, 1024, 2048, 4096, -1 };
                _context.Settings.materialsSizePresetIndex = EditorGUILayout.Popup(new GUIContent("Material size limit", "Skip materials larger than this size."), _context.Settings.materialsSizePresetIndex, mNames);
                int mi = Mathf.Clamp(_context.Settings.materialsSizePresetIndex, 0, mNames.Length - 1);
                if (mi < mNames.Length - 1)
                {
                    _context.Settings.materialsMaxKB = mValues[mi];
                    EditorGUILayout.LabelField($"= {_context.Settings.materialsMaxKB} KB");
                }
                else
                {
                    long v = EditorGUILayout.LongField(new GUIContent("Custom (KB)", "Custom max size in KB."), _context.Settings.materialsMaxKB);
                    v = Math.Max(128L, Math.Min(v, 1024L * 10L));
                    _context.Settings.materialsMaxKB = v;
                }
            }
            _context.Settings.incDlls = EditorGUILayout.ToggleLeft(new GUIContent("DLLs (.dll) under size limit", "Managed DLLs and native plugins."), _context.Settings.incDlls);
            using (new EditorGUI.DisabledScope(!_context.Settings.incDlls))
            {
                string[] dNames = { "256 KB", "512 KB", "1 MB", "2 MB", "4 MB", "Custom" };
                long[] dValues = { 256, 512, 1024, 2048, 4096, -1 };
                _context.Settings.dllSizePresetIndex = EditorGUILayout.Popup(new GUIContent("DLL size limit", "Skip DLLs larger than this size."), _context.Settings.dllSizePresetIndex, dNames);
                int di = Mathf.Clamp(_context.Settings.dllSizePresetIndex, 0, dNames.Length - 1);
                if (di < dNames.Length - 1)
                {
                    _context.Settings.dllsMaxKB = dValues[di];
                    EditorGUILayout.LabelField($"= {_context.Settings.dllsMaxKB} KB");
                }
                else
                {
                    long v = EditorGUILayout.LongField(new GUIContent("Custom (KB)", "Custom max size in KB."), _context.Settings.dllsMaxKB);
                    v = Math.Max(128L, Math.Min(v, 1024L * 10L));
                    _context.Settings.dllsMaxKB = v;
                }
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Folders & Types", EditorStyles.boldLabel);
            bool filtersChanged = false;
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Add folders from Project or simple extensions (e.g., .prefab).", EditorStyles.miniLabel);
            EditorGUI.BeginChangeCheck();
            _context.Settings.extWithinIncludeFolders = EditorGUILayout.ToggleLeft(new GUIContent("Apply extensions only within included folders", "When enabled, extensions like .prefab are searched only inside the folders you included."), _context.Settings.extWithinIncludeFolders);
            if (EditorGUI.EndChangeCheck()) filtersChanged = true;
            EditorGUILayout.EndVertical();
            filtersChanged |= DrawIncludeExcludeSection("Include", _context.Settings.includeFolders, ref _context.NewIncludePattern, ref _context.IncludePrefix, ref _context.IncludeFolderObj);
            EditorGUILayout.Space(6);
            filtersChanged |= DrawIncludeExcludeSection("Exclude", _context.Settings.excludeFolders, ref _context.NewExcludePattern, ref _context.ExcludePrefix, ref _context.ExcludeFolderObj);
            DrawTrackedSelectionSection();
            if (filtersChanged) _context.FlagFiltersChanged();
            if (_context.Settings.filtersDirty)
            {
                using (_context.Info(MessageType.Info, AvatarSmartBackup.Localization.L.T("ui.filters.pending", "Filter changes pending. The next backup will create a full checkpoint to apply the new scope.")))
                {
                    GUILayout.Space(2);
                }
            }
            EditorGUILayout.EndVertical();
        }

        void DrawDebugToolsSection()
        {
            _context.RecordLayoutMarker("DebugToolsSection");
            EditorGUILayout.BeginVertical("box");
            _context.Settings.showDebugTools = EditorGUILayout.Foldout(_context.Settings.showDebugTools, AvatarSmartBackup.Localization.L.T("ui.debug.tools.title", "Debug tools"), true);
            if (_context.Settings.showDebugTools)
            {
                EditorGUI.indentLevel++;
                bool newDebug = EditorGUILayout.ToggleLeft(AvatarSmartBackup.Localization.L.C("ui.debug.tools.enable", "Enable debug tools", "ui.debug.tools.enable.tt", "Turn on diagnostics and manual overrides."), _context.Settings.debugMode);
                if (newDebug != _context.Settings.debugMode)
                {
                    _context.Settings.debugMode = newDebug;
                    if (!_context.Settings.debugMode)
                        _context.Settings.enableDebugLogging = false;
                }

                if (_context.Settings.debugMode)
                {
                    bool useGlobal = !_context.Settings.useProjectSettings;
                    bool newUseGlobal = EditorGUILayout.ToggleLeft(AvatarSmartBackup.Localization.L.C("ui.debug.tools.global", "Use global settings", "ui.debug.tools.global.tt", "Share this configuration across projects."), useGlobal);
                    if (newUseGlobal != useGlobal)
                    {
                        _context.Settings.useProjectSettings = !newUseGlobal;
                        BackupManager.SaveSettings(_context.Settings);
                        TimerService.InvalidateSettingsCache();
                        _context.ReloadSettings();
                    }

                    if (_context.Settings.lastMeasuredMBps > 0f)
                    {
                        EditorGUILayout.LabelField(string.Format(AvatarSmartBackup.Localization.L.T("ui.debug.tools.throughput", "Measured throughput: {0:0.0} MB/s"), _context.Settings.lastMeasuredMBps), EditorStyles.miniLabel);
                    }
                    else
                    {
                        using (_context.Info(MessageType.None, AvatarSmartBackup.Localization.L.T("ui.debug.tools.throughput.none", "Benchmark has not been run yet.")))
                        {
                            GUILayout.Space(2);
                        }
                    }

                    using (new EditorGUI.DisabledScope(BackupManager.IsBusy))
                    {
                        if (GUILayout.Button(AvatarSmartBackup.Localization.L.C("ui.debug.tools.benchmark", "Run benchmark again"), GUILayout.Width(170)))
                        {
                            _ = BackupManager.RunManualBenchmarkAsync(_context.Settings);
                        }
                    }
                    if (BackupManager.IsBusy)
                    {
                        using (_context.Info(MessageType.Info, AvatarSmartBackup.Localization.L.T("ui.debug.tools.benchmark.busy", "Benchmark unavailable while a backup is running.")))
                        {
                            GUILayout.Space(2);
                        }
                    }

                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.debug.tools.cooldowns", "Manual cooldowns"), EditorStyles.boldLabel);

                    DrawCooldownRow("ui.debug.tools.snapshot.cooldown", "Snapshot cooldown (s)", "ui.debug.tools.snapshot.cooldown.tt", "Minimum seconds between manual snapshots.", ref _context.Settings.manualSnapshotCooldownSeconds);
                    DrawCooldownRow("ui.debug.tools.benchmark.cooldown", "Benchmark cooldown (s)", "ui.debug.tools.benchmark.cooldown.tt", "Minimum seconds between manual benchmark runs.", ref _context.Settings.manualBenchmarkCooldownSeconds);
                    DrawCooldownRow("ui.debug.tools.backup.cooldown", "Min backup interval (s)", "ui.debug.tools.backup.cooldown.tt", "Minimum seconds between manual backup requests.", ref _context.Settings.minManualBackupIntervalSeconds);

                    _context.Settings.enableDebugLogging = EditorGUILayout.ToggleLeft(AvatarSmartBackup.Localization.L.C("ui.debug.tools.logs", "Detailed file logging", "ui.debug.tools.logs.tt", "Write verbose file logs for troubleshooting."), _context.Settings.enableDebugLogging);
                    using (_context.Info(MessageType.None, AvatarSmartBackup.Localization.L.T("ui.debug.tools.snapshots.info", "Zip snapshots are managed automatically. Legacy settings remain available for compatibility.")))
                    {
                        GUILayout.Space(2);
                    }
                }
                else
                {
                    using (_context.Info(MessageType.None, AvatarSmartBackup.Localization.L.T("ui.debug.tools.disabled", "Enable to access diagnostics and manual overrides.")))
                    {
                        GUILayout.Space(2);
                    }
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
            if (_context.Settings.trackedRoots == null)
                _context.Settings.trackedRoots = new List<string>();

            var tracked = _context.Settings.trackedRoots;
            bool frozen = BackupManager.IsSelectionFrozen(_context.Settings);

            if (tracked.Count == 0 && !frozen)
                return;

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
            using (_context.Info(MessageType.Info, "Use Folders & Types to adjust what gets backed up. Tracked selection editing is deprecated."))
            {
                GUILayout.Space(2);
            }
            EditorGUILayout.EndVertical();
        }

        bool DrawIncludeExcludeSection(string title, List<string> list, ref string newExt, ref string newPrefix, ref UnityEngine.Object? folderObj)
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

        void DrawThinSeparator()
        {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(1f, 1f, 1f, 0.18f));
        }
    }
}
#endif
