#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using AvatarSmartBackup.Backup;

namespace AvatarSmartBackup.Shared
{
    internal sealed class SchedulerSection
    {
        readonly BackupWindowContext _context;
        readonly StatusBanner _statusBanner;

        public SchedulerSection(BackupWindowContext context, StatusBanner statusBanner)
        {
            _context = context;
            _statusBanner = statusBanner;
        }

        public void Draw()
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
                _statusBanner.Draw(MessageType.Info, AvatarSmartBackup.Localization.L.T("ui.filters.pending", "Filter changes pending. The next backup will create a full checkpoint to apply the new scope."));
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
                        _statusBanner.Draw(MessageType.Warning, AvatarSmartBackup.Localization.L.T("warn.backup.speed", "At {0} MB/s, backing up {1:0.0} MB takes ~{2:0.0} min, exceeding the interval.", effCopy, mb, minutesNeeded));
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

            _statusBanner.Draw(type, summary);
        }

        void DrawIntervalControls()
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
    }
}
#endif
