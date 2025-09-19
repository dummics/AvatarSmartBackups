#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using AvatarSmartBackup.Backup;
using AvatarSmartBackup.Localization;
using AvatarSmartBackup.Shared;

namespace AvatarSmartBackup.Easy
{
    internal sealed class EasyDashboardView
    {
        readonly BackupWindowContext _context;
        readonly StatusBanner _statusBanner;
        EasyDashboardSnapshot _snapshot;

        public EasyDashboardView(BackupWindowContext context, StatusBanner statusBanner)
        {
            _context = context;
            _statusBanner = statusBanner;
        }

        public EasyDashboardSnapshot LastSnapshot => _snapshot;

        public void Draw()
        {
            float viewWidth = EditorGUIUtility.currentViewWidth;
            bool stackButtons = viewWidth < 420f;
            _snapshot = new EasyDashboardSnapshot(stackButtons, viewWidth);
            _context.RecordLayoutMarker(stackButtons ? "EasyDashboard.Compact" : "EasyDashboard.Wide");

            DrawQuickActions(stackButtons);
            EditorGUILayout.Space(6);
            DrawSchedulerSummary(stackButtons);
            EditorGUILayout.Space(6);
            DrawLatestVersionSummary();
            DrawDiskStatusWarnings();
        }

        void DrawQuickActions(bool stackButtons)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(L.T("easy.dashboard.actions.title", "Quick actions"), EditorStyles.boldLabel);

            if (stackButtons)
            {
                DrawBackupButton(GUILayout.Height(28));
                EditorGUILayout.Space(4);
                DrawRestoreButton(GUILayout.Height(28));
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                DrawBackupButton(GUILayout.Width(160), GUILayout.Height(28));
                DrawRestoreButton(GUILayout.Width(180), GUILayout.Height(28));
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(
                L.T("easy.dashboard.actions.microcopy", "Backups run automatically. Trigger a manual run or open the guided restore if you need control right now."),
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }

        void DrawBackupButton(params GUILayoutOption[] options)
        {
            bool canManual = true;
            string tooltip = L.T("tt.backup.now.easy", "Run a backup now (cooldown applies in Easy mode)");
            if (_context.ShouldThrottleManualBackup)
            {
                canManual = false;
                double rem = _context.RemainingManualCooldown;
                tooltip = string.Format(L.T("tt.backup.cooldown", "Wait {0:0}s before another manual backup"), rem);
            }

            using (new EditorGUI.DisabledScope(!canManual || BackupManager.IsBusy))
            {
                if (GUILayout.Button(new GUIContent(L.T("ui.backup.now", "Backup Now"), tooltip), options))
                {
                    _context.RunBackupNow();
                }
            }
        }

        void DrawRestoreButton(params GUILayoutOption[] options)
        {
            _context.EnsureVersionsCache();
            var latest = _context.GetLatestVersionCached();
            string tooltip = latest == null
                ? L.T("tt.latest.none", "No version available")
                : L.T("tt.easy.restore", "Open the guided restore for the latest version");

            using (new EditorGUI.DisabledScope(latest == null))
            {
                if (GUILayout.Button(new GUIContent(L.T("easy.dashboard.restore", "Guided Restore"), tooltip), options) && latest != null)
                {
                    _context.OpenPreviewRestore(latest);
                }
            }
        }

        void DrawSchedulerSummary(bool stackButtons)
        {
            EditorGUILayout.BeginVertical("box");
            bool running = Session.IsRunning;
            EditorGUILayout.LabelField(L.T("easy.dashboard.scheduler.title", "Automatic backups"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                running
                    ? L.T("easy.dashboard.scheduler.running", "Automatic backups are running in the background.")
                    : L.T("easy.dashboard.scheduler.paused", "Automatic backups are paused."),
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(2);
            if (stackButtons)
            {
                DrawSchedulerToggle(running, GUILayout.Height(24));
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                DrawSchedulerToggle(running, GUILayout.Width(200), GUILayout.Height(24));
                GUILayout.FlexibleSpace();
                DrawScheduleTimes();
                EditorGUILayout.EndHorizontal();
            }

            if (stackButtons)
            {
                EditorGUILayout.Space(2);
                DrawScheduleTimes();
            }

            if (_context.Settings.filtersDirty)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(
                    L.T("easy.dashboard.filters.pending", "Filter changes pending. The next backup will refresh the scope."),
                    EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        void DrawSchedulerToggle(bool running, params GUILayoutOption[] options)
        {
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = running ? new Color(0.25f, 0.55f, 0.25f, 1f) : new Color(0.45f, 0.2f, 0.2f, 1f);
            if (GUILayout.Button(new GUIContent(
                    running ? L.T("ui.autobackups.on", "Automatic Backups: ON") : L.T("ui.autobackups.off", "Automatic Backups: OFF"),
                    L.T("ui.autobackups.toggle.tt", "Toggle background backup scheduler")),
                    options))
            {
                _context.ToggleScheduler();
            }
            GUI.backgroundColor = prev;
        }

        void DrawScheduleTimes()
        {
            var nextLabel = L.T("ui.autobackups.next", "Next");
            var lastLabel = L.T("ui.autobackups.last", "Last");
            var never = L.T("ui.never", "never");
            var nextStr = Session.NextRunUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "--";
            var lastStr = Session.LastBackupUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? never;
            EditorGUILayout.LabelField($"{nextLabel}: {nextStr}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"{lastLabel}: {lastStr}", EditorStyles.miniLabel);
        }

        void DrawLatestVersionSummary()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(L.T("easy.dashboard.latest.title", "Latest version"), EditorStyles.boldLabel);
            _context.EnsureVersionsCache();
            var latest = _context.GetLatestVersionCached();
            if (latest != null)
            {
                string desc = _context.SanitizeInlineLabel(latest.description, L.T("ui.overview.noDescription", "(no description)"));
                EditorGUILayout.LabelField($"#{latest.id}  {desc}", EditorStyles.miniLabel);
                var created = _context.ParseCreatedUtc(latest);
                if (created != DateTime.MinValue)
                    EditorGUILayout.LabelField(created.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    string.Format(L.T("easy.dashboard.latest.files", "Files: {0}    Size: {1}"), latest.fileCount, _context.FormatSize(latest.totalSizeBytes)),
                    EditorStyles.miniLabel);
                if (GUILayout.Button(new GUIContent(L.T("easy.dashboard.latest.openVersions", "Review versions"), L.T("tt.overview.gotoVersions", "Open Versions tab")), GUILayout.Width(140)))
                {
                    _context.Settings._activeTab = 1;
                }
            }
            else
            {
                EditorGUILayout.LabelField(L.T("easy.dashboard.latest.none", "No versions available yet"), EditorStyles.miniLabel);
            }
            EditorGUILayout.EndVertical();
        }

        void DrawDiskStatusWarnings()
        {
            var report = DiskSpaceMonitor.LastReport;
            if (report.Status == DiskSpaceStatus.Unknown)
                return;

            var snapshot = report.Snapshot;
            if (snapshot.totalBytes <= 0)
                return;

            string summary = string.Format(
                L.T("easy.dashboard.disk.summary", "Disk free: {0} / {1} • Backups: {2}"),
                DiskSpaceMonitor.FormatBytes(snapshot.freeBytes),
                DiskSpaceMonitor.FormatBytes(snapshot.totalBytes),
                DiskSpaceMonitor.FormatBytes(snapshot.backupSizeBytes));

            EditorGUILayout.LabelField(summary, EditorStyles.miniLabel);

            if (report.RequiredBytes > 0 && report.Stage != DiskSpaceStage.PostBackup)
            {
                EditorGUILayout.LabelField(
                    string.Format(L.T("easy.dashboard.disk.next", "Next estimate: {0}"), DiskSpaceMonitor.FormatBytes(report.RequiredBytes)),
                    EditorStyles.miniLabel);
            }

            if (report.Status != DiskSpaceStatus.Ok)
            {
                MessageType type = report.Status switch
                {
                    DiskSpaceStatus.Warning => MessageType.Warning,
                    DiskSpaceStatus.Critical => MessageType.Error,
                    DiskSpaceStatus.Error => MessageType.Warning,
                    _ => MessageType.Info
                };
                _statusBanner.Draw(type, report.Message);
            }
        }
    }

    internal readonly struct EasyDashboardSnapshot
    {
        public EasyDashboardSnapshot(bool buttonsStacked, float viewWidth)
        {
            ButtonsStacked = buttonsStacked;
            ViewWidth = viewWidth;
        }

        public bool ButtonsStacked { get; }
        public float ViewWidth { get; }
    }
}
#endif
