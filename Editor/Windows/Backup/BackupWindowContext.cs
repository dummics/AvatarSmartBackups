#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup.Backup
{
    internal sealed class BackupWindowContext : IDisposable
    {
        struct BackupSignature { public long size; public int count; }

        readonly AvatarSmartBackupWindow _window;
        readonly ModernInfoBanner _infoBanner = new ModernInfoBanner();
        readonly List<string> _layoutMarkers = new List<string>();

        Vector2 _scroll;
        Vector2 _versionsScroll;

        public BackupSettings Settings { get; private set; }

        // Include/Exclude UI temp fields
        public string NewIncludePattern = string.Empty;
        public string NewExcludePattern = string.Empty;
        public string IncludePrefix = string.Empty;
        public string ExcludePrefix = string.Empty;
        public UnityEngine.Object? IncludeFolderObj;
        public UnityEngine.Object? ExcludeFolderObj;

        BackupSignature? _cachedCurrentSig; double _cachedCurrentSigTime;
        GUIStyle? _selectedTitleStyle;
        List<VersionInfo>? _cachedVersions;
        int _renamingId = -1; string _renameBuffer = string.Empty;
        int _selectedVersionId = -1;
        int _lastClickId = -1; double _lastClickTime = -1;
        double _lastManualRunTime = -1;

        static readonly Color BadgeColorCheckpoint = new Color(0.23f, 0.46f, 0.80f, 0.18f);
        static readonly Color BadgeColorIncremental = new Color(0.45f, 0.35f, 0.78f, 0.18f);
        static readonly Color BadgeColorChanged = new Color(0.77f, 0.55f, 0.16f, 0.22f);
        static readonly Color BadgeColorRemoved = new Color(0.75f, 0.25f, 0.25f, 0.22f);
        const float BadgeWidth = 128f;
        const int ManualCheckpointMax = 12;
        static GUIStyle? _badgeStyle;
        static Texture2D? _texExplorer; static bool _texTried;

        public BackupWindowContext(AvatarSmartBackupWindow window)
        {
            _window = window;
            Settings = BackupManager.LoadSettings() ?? new BackupSettings();
            BackupEvents.BackupCompleted += OnBackupCompleted;
        }

        public void Dispose()
        {
            BackupEvents.BackupCompleted -= OnBackupCompleted;
            BackupManager.SaveSettings(Settings);
            TimerService.InvalidateSettingsCache();
        }

        public Vector2 Scroll
        {
            get => _scroll;
            set => _scroll = value;
        }

        public Vector2 VersionsScroll
        {
            get => _versionsScroll;
            set => _versionsScroll = value;
        }

        public ModernInfoBanner InfoBanner => _infoBanner;

        public bool LayoutRecordingEnabled { get; set; }

        public IReadOnlyList<string> LayoutMarkers => _layoutMarkers;

        public void ClearLayoutMarkers()
        {
            if (!LayoutRecordingEnabled)
                return;
            _layoutMarkers.Clear();
        }

        public void RecordLayoutMarker(string marker)
        {
            if (!LayoutRecordingEnabled)
                return;
            if (!string.IsNullOrEmpty(marker))
                _layoutMarkers.Add(marker);
        }

        public void ReloadSettings()
        {
            Settings = BackupManager.LoadSettings() ?? Settings;
        }

        public void EnsureTextures()
        {
            if (_texTried)
                return;

            _texExplorer = Resources.Load<Texture2D>("explorerIcon");
            _texTried = true;
        }

        public void RequestRepaint()
        {
            _window.Repaint();
        }

        public Texture2D? ExplorerTexture
        {
            get
            {
                EnsureTextures();
                return _texExplorer;
            }
        }

        public void HandleOnboarding(bool forceBackupTab)
        {
            if (forceBackupTab)
            {
                Settings._activeTab = 0;
                Settings._uiTabInitialized = true;
            }

            RunOnboardingIfNeeded();
        }

        void RunOnboardingIfNeeded()
        {
            if (Settings.onboardingCompleted)
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

                Settings.easyMode = (choice == 0);
                Settings.AdvancedMode = !Settings.easyMode;
                awaitingChoice = false;
            }

            Settings.onboardingCompleted = true;
            BackupManager.SaveSettings(Settings);
            TimerService.InvalidateSettingsCache();
        }

        void OnBackupCompleted(BackupRunSummary summary)
        {
            _cachedVersions = null;
            _cachedCurrentSig = null;
            _cachedCurrentSigTime = 0;
            _window.Repaint();
        }

        public void SaveSettingsIfChanged(bool changed)
        {
            if (!changed)
                return;

            BackupManager.SaveSettings(Settings);
            TimerService.InvalidateSettingsCache();
        }

        public void ToggleScheduler()
        {
            bool running = Session.IsRunning;
            if (running)
                TimerService.PauseTimer();
            else
                TimerService.StartTimerIfNeeded(Settings);
        }

        public void RefreshVersions()
        {
            _cachedVersions = null;
            EnsureVersionsCache();
        }

        public void EnsureVersionsCache()
        {
            if (_cachedVersions != null)
                return;

            try
            {
                using var vm = new FileBasedVersionManager();
                _cachedVersions = vm.ListVersions().OrderByDescending(v => ParseCreatedUtc(v)).ToList();
                if (_cachedVersions.Count > ManualCheckpointMax)
                {
                    _cachedVersions = _cachedVersions.Take(ManualCheckpointMax).ToList();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Unable to load versions: " + ex.Message);
                _cachedVersions = new List<VersionInfo>();
            }
        }

        public IReadOnlyList<VersionInfo>? CachedVersions => _cachedVersions;

        public VersionInfo? GetLatestVersionCached()
        {
            if (_cachedVersions == null || _cachedVersions.Count == 0) return null;
            return _cachedVersions.OrderByDescending(v => ParseCreatedUtc(v)).FirstOrDefault();
        }

        public DateTime ParseCreatedUtc(VersionInfo info)
        {
            if (info == null) return DateTime.MinValue;
            if (!string.IsNullOrEmpty(info.createdUtc) && DateTime.TryParse(info.createdUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                if (parsed.Year >= 2000)
                    return parsed.ToLocalTime();
            }
            if (info.timestamp != default)
                return info.timestamp.ToLocalTime();
            return DateTime.MinValue;
        }

        public string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double val = bytes;
            int u = 0;
            while (val > 1024 && u < units.Length - 1) { val /= 1024; u++; }
            return $"{val:0.0} {units[u]}";
        }

        public string SanitizeInlineLabel(string value, string fallback)
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

        public string BuildVersionCardTitle(VersionInfo version, bool isLatest)
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

        public string BuildChangeCountSummary(VersionInfo info)
        {
            if (info == null) return string.Empty;
            if (info.changedFileCount <= 0 && info.removedFileCount <= 0) return string.Empty;
            string delta = info.changedBytes > 0 ? FormatSize(info.changedBytes) : "0 B";
            return $"Changes: {info.changedFileCount}  Removed: {info.removedFileCount}  Delta: {delta}";
        }

        public string BuildVersionTypeSummary(VersionInfo info)
        {
            if (info == null) return "Type: --";
            if (info.isCheckpoint) return "Type: Full checkpoint";
            var parts = new List<string>();
            if (info.changedFileCount > 0) parts.Add($"{info.changedFileCount} changed");
            if (info.removedFileCount > 0) parts.Add($"{info.removedFileCount} removed");
            string suffix = parts.Count > 0 ? string.Join(", ", parts) : "no recorded changes";
            return $"Type: Incremental ({suffix})";
        }

        public string BuildCategorySummary(VersionInfo info)
        {
            if (info?.categoryStats == null || info.categoryStats.Count == 0) return string.Empty;
            return string.Join(", ", info.categoryStats
                .OrderByDescending(cs => cs.count)
                .ThenBy(cs => cs.category)
                .Select(cs => $"{cs.category}: {cs.count}"));
        }

        public string BuildVersionDetailSummary(VersionInfo info)
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

        public void OpenPreviewRestore(VersionInfo info)
        {
            if (info == null) return;

            try
            {
                if (Settings.easyMode && !Settings.AdvancedMode)
                {
                    AvatarSmartBackup.Restore.Easy.RestoreEasyWizard.Open(info);
                }
                else
                {
                    RestorePreviewWindow.Open(info.id);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Preview & Restore failed: " + ex.Message);
                var message = string.Format(AvatarSmartBackup.Localization.L.T("vc.error.body", "Unable to read changes:\n{0}"), ex.Message);
                EditorUtility.DisplayDialog(AvatarSmartBackup.Localization.L.T("vc.error.title", "Preview & Restore"), message, "OK");
            }
        }

        public void TriggerSnapshotRebuild(VersionInfo info)
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

        public void RunRebuildIndexTool()
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
                    _cachedVersions = null; EnsureVersionsCache();
                    EditorUtility.DisplayDialog("Rebuild Index", "Index updated.", "OK");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Rebuild Index", "Operation failed:\n" + ex.Message, "OK");
            }
        }

        public bool CanCreateManualVersion(out string reason)
        {
            reason = string.Empty;
            if (!Settings.AdvancedMode && _cachedVersions != null && _cachedVersions.Count > 0)
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

        public bool HandleManualVersionCreation()
        {
            using var vm = new FileBasedVersionManager();
            vm.CreateVersion("Manual", Path.Combine(FileUtilEx.BackupRoot, "Current"), Settings, forceCheckpoint: true);
            _cachedVersions = null; EnsureVersionsCache();
            return true;
        }

        public bool ShouldThrottleManualBackup
        {
            get
            {
                const double BackupManualCooldownSeconds = 30;
                double now = EditorApplication.timeSinceStartup;
                bool throttle = !Settings.AdvancedMode;
                if (!throttle)
                    return false;
                if (_lastManualRunTime <= 0)
                    return false;
                return now - _lastManualRunTime < BackupManualCooldownSeconds;
            }
        }

        public double RemainingManualCooldown
        {
            get
            {
                const double BackupManualCooldownSeconds = 30;
                double now = EditorApplication.timeSinceStartup;
                double rem = BackupManualCooldownSeconds - (now - _lastManualRunTime);
                return Math.Max(0, rem);
            }
        }

        public void RecordManualRun()
        {
            _lastManualRunTime = EditorApplication.timeSinceStartup;
        }

        public void RunBackupNow()
        {
            RecordManualRun();
            BackupManager.RunBackupNow(Settings, showToast: true, reason: "manual", showProgressUI: true);
        }

        public bool IsLatestSelected(VersionInfo info)
        {
            var latest = GetLatestVersionCached();
            return latest != null && latest.id == info?.id;
        }

        public void SelectVersion(VersionInfo info)
        {
            _selectedVersionId = info?.id ?? -1;
        }

        public VersionInfo? GetSelectedVersion()
        {
            if (_cachedVersions == null)
                return null;
            if (_selectedVersionId < 0)
                return GetLatestVersionCached();
            return _cachedVersions.FirstOrDefault(v => v.id == _selectedVersionId);
        }

        public GUIStyle SelectedTitleStyle
        {
            get
            {
                return _selectedTitleStyle ??= new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 12,
                    fontStyle = FontStyle.Bold
                };
            }
        }

        public void BeginRename(VersionInfo info)
        {
            _renamingId = info.id;
            _renameBuffer = info.description ?? string.Empty;
        }

        public void CancelRename()
        {
            _renamingId = -1;
            _renameBuffer = string.Empty;
        }

        public bool IsRenaming(VersionInfo info) => _renamingId == info?.id;

        public string RenameBuffer
        {
            get => _renameBuffer;
            set => _renameBuffer = value;
        }

        public void CommitRename(VersionInfo v)
        {
            string trimmed = (_renameBuffer ?? string.Empty).Trim();
            if (trimmed.Length == 0) { _renamingId = -1; _renameBuffer = string.Empty; return; }
            if (trimmed != v.description)
            {
                try
                {
                    using var vm = new FileBasedVersionManager();
                    vm.UpdateDescription(v.id, trimmed);
                    v.description = trimmed;
                }
                catch (Exception ex)
                {
                    EditorUtility.DisplayDialog("Rename version", "Unable to rename:\n" + ex.Message, "OK");
                }
            }
            _renamingId = -1; _renameBuffer = string.Empty;
        }

        public bool HandleVersionClick(VersionInfo info)
        {
            double now = EditorApplication.timeSinceStartup;
            bool doubleClick = (_lastClickId == info.id && now - _lastClickTime < 0.4f);
            _lastClickId = info.id; _lastClickTime = now;
            SelectVersion(info);
            return doubleClick;
        }

        public void RevealVersion(VersionInfo info)
        {
            EnsureTextures();
            if (info == null) return;
            var dir = Path.Combine(FileUtilEx.BackupRoot, info.id.ToString("D4"));
            if (Directory.Exists(dir))
                EditorUtility.RevealInFinder(dir);
        }

        public void DrawBadge(Color tint, string text)
        {
            Rect rect = GUILayoutUtility.GetRect(BadgeWidth, 20f, BadgeStyle, GUILayout.MaxWidth(BadgeWidth));
            EditorGUI.DrawRect(rect, tint);
            var labelRect = new Rect(rect.x + 6, rect.y + 2, rect.width - 12, rect.height - 4);
            GUI.Label(labelRect, text, BadgeStyle);
        }

        public GUIStyle BadgeStyle
        {
            get
            {
                var style = _badgeStyle ??= new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter };
                return style;
            }
        }

        public ModernInfoBanner.Scope Info(MessageType type, string content)
        {
            return _infoBanner.Scope(type, content);
        }

        public ModernInfoBanner.Scope Info(string title, string content, MessageType type = MessageType.Info)
        {
            return _infoBanner.Scope(type, content, title);
        }

        public bool IsVersionSelected(VersionInfo version)
        {
            return _selectedVersionId == version?.id;
        }

        public void ResetVersionSelection()
        {
            _selectedVersionId = -1;
        }

        public void FlagFiltersChanged()
        {
            if (!Settings.filtersDirty)
            {
                Settings.filtersDirty = true;
                Settings.filtersChangedTicks = DateTime.UtcNow.Ticks;
            }
        }
    }
}
#endif
