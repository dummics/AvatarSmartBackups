#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using AvatarSmartBackup;

namespace AvatarSmartBackup.Backup
{
    internal class BackupVersionsView
    {
        readonly BackupWindowContext _context;

        public BackupVersionsView(BackupWindowContext context)
        {
            _context = context;
        }

        public void Draw()
        {
            _context.RecordLayoutMarker("VersionsTab");
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.versions.title", "Versions"), EditorStyles.boldLabel);
            using (_context.Info(MessageType.Info, AvatarSmartBackup.Localization.L.T("ui.versions.help", "Restore points are automatically created when there are changes. Use the star to pin, click to select, double-click to preview.")))
            {
                GUILayout.Space(2);
            }

            _context.EnsureVersionsCache();
            var latest = _context.GetLatestVersionCached();
            if (_context.GetSelectedVersion() == null && latest != null)
            {
                _context.SelectVersion(latest);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.versions.refresh", "Refresh"), AvatarSmartBackup.Localization.L.T("tt.versions.refresh", "Reload versions from disk")), GUILayout.Width(70)))
            {
                _context.RefreshVersions();
            }
            if (_context.Settings.showRebuildTool)
            {
                if (GUILayout.Button(new GUIContent("Rebuild Index", "Recalculate fileCount/size from existing manifests"), GUILayout.Width(120)))
                {
                    _context.RunRebuildIndexTool();
                }
            }
            if (_context.Settings.AdvancedMode)
            {
                GUI.enabled = !BackupManager.IsBusy;
                bool allowManualVersion = _context.CanCreateManualVersion(out string reasonBlock);
                using (new EditorGUI.DisabledScope(!allowManualVersion))
                {
                    if (GUILayout.Button(new GUIContent("Create Version", allowManualVersion ? "Create a manual restore point" : reasonBlock), GUILayout.Width(110)))
                    {
                        _context.HandleManualVersionCreation();
                    }
                }
                GUI.enabled = true;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            var cached = _context.CachedVersions;
            if (cached == null || cached.Count == 0)
            {
                using (_context.Info(MessageType.Info, "No versions yet."))
                {
                    GUILayout.Space(2);
                }
                return;
            }

            _context.VersionsScroll = EditorGUILayout.BeginScrollView(_context.VersionsScroll);
            Event e = Event.current;
            int? pendingTogglePin = null;
            int? pendingDelete = null;
            foreach (var v in cached
                .OrderByDescending(v => v.pinned)
                .ThenByDescending(v => _context.ParseCreatedUtc(v)))
            {
                _context.RecordLayoutMarker($"VersionCard#{v.id}");
                GUI.enabled = true;
                bool isSelected = _context.IsVersionSelected(v);
                var selectedStyle = _context.SelectedTitleStyle;
                var explorerTex = _context.ExplorerTexture;

                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent(v.pinned ? "★" : "☆", v.pinned ? "Unpin" : "Pin"), GUILayout.Width(24)))
                    pendingTogglePin = v.id;
                Rect starRect = GUILayoutUtility.GetLastRect();
                string title = _context.BuildVersionCardTitle(v, latest != null && latest.id == v.id);
                EditorGUILayout.LabelField(title, isSelected ? selectedStyle : EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                Rect folderBtnRect = GUILayoutUtility.GetRect(20, 18, GUILayout.Width(20));
                if (explorerTex != null && Event.current.type == EventType.Repaint)
                    GUI.DrawTexture(folderBtnRect, explorerTex, ScaleMode.ScaleToFit, true);
                else if (Event.current.type == EventType.Repaint && explorerTex == null)
                {
                    var style = EditorStyles.miniLabel;
                    var pc = GUI.color;
                    GUI.color = new Color(1, 1, 1, 0.35f);
                    GUI.Label(folderBtnRect, "☰", style);
                    GUI.color = pc;
                }
                if (GUI.Button(folderBtnRect, GUIContent.none, GUIStyle.none))
                {
                    string dir = Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{v.id:D3}");
                    if (Directory.Exists(dir)) EditorUtility.RevealInFinder(dir);
                    else EditorUtility.DisplayDialog("Version", "Folder not found", "OK");
                }
                EditorGUILayout.EndHorizontal();
                var created = _context.ParseCreatedUtc(v);
                EditorGUILayout.LabelField($"Created: {(created == DateTime.MinValue ? "--" : created.ToString("yyyy-MM-dd HH:mm:ss"))}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Files: {v.fileCount}    Size: {_context.FormatSize(v.totalSizeBytes)}", EditorStyles.miniLabel);
                DrawVersionBadges(v);
                string changeSummary = _context.BuildChangeCountSummary(v);
                if (!string.IsNullOrEmpty(changeSummary))
                    EditorGUILayout.LabelField(changeSummary, EditorStyles.miniLabel);
                if (!v.isCheckpoint && v.checkpointId > 0)
                {
                    var cp = cached.FirstOrDefault(cv => cv.id == v.checkpointId);
                    if (cp != null)
                    {
                        var cpDate = _context.ParseCreatedUtc(cp);
                        string cpLabel = cpDate == DateTime.MinValue ? $"#{cp.id}" : $"#{cp.id} ({cpDate:yyyy-MM-dd HH:mm})";
                        EditorGUILayout.LabelField($"Source checkpoint: {cpLabel}", EditorStyles.miniLabel);
                    }
                }
                if (isSelected)
                {
                    GUILayout.Space(4);
                    EditorGUILayout.BeginVertical("box");
                    using (_context.Info(MessageType.Info, _context.BuildVersionDetailSummary(v)))
                    {
                        GUILayout.Space(2);
                    }
                    if (_context.IsRenaming(v))
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUI.SetNextControlName("RenameField");
                        _context.RenameBuffer = EditorGUILayout.TextField(_context.RenameBuffer);
                        if (GUILayout.Button("Save", GUILayout.Width(50))) _context.CommitRename(v);
                        if (GUILayout.Button("Cancel", GUILayout.Width(60))) _context.CancelRename();
                        EditorGUILayout.EndHorizontal();
                        if (e.isKey && e.keyCode == KeyCode.Return) _context.CommitRename(v);
                    }
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button(new GUIContent("Restore", "Open preview and proceed to restore"), GUILayout.Height(22))) _context.OpenPreviewRestore(v);
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
                    if (GUILayout.Button(new GUIContent("Preview", "Inspect changes and restore"), GUILayout.Width(90))) _context.OpenPreviewRestore(v);
                    if (GUILayout.Button(new GUIContent("Rename", "Rename this version"), GUILayout.Width(80))) _context.BeginRename(v);
                    if (GUILayout.Button(new GUIContent("Reveal", "Open version folder"), GUILayout.Width(70))) _context.RevealVersion(v);
                    if (GUILayout.Button(new GUIContent("Snapshot", "Rebuild snapshot for manual inspection"), GUILayout.Width(90))) _context.TriggerSnapshotRebuild(v);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                }

                Rect cardRect = GUILayoutUtility.GetLastRect();
                if (Event.current.type == EventType.MouseDown && cardRect.Contains(Event.current.mousePosition) && Event.current.button == 0)
                {
                    if (_context.HandleVersionClick(v))
                    {
                        _context.OpenPreviewRestore(v);
                    }
                    Event.current.Use();
                }

                EditorGUILayout.EndVertical();
                GUILayout.Space(4);
            }
            EditorGUILayout.EndScrollView();

            if (pendingTogglePin.HasValue)
            {
                using var vm = new FileBasedVersionManager();
                vm.TogglePinned(pendingTogglePin.Value);
                _context.RefreshVersions();
            }
            if (pendingDelete.HasValue)
            {
                using var vm = new FileBasedVersionManager();
                vm.DeleteVersion(pendingDelete.Value);
                _context.RefreshVersions();
            }
        }

        void DrawVersionBadges(VersionInfo info)
        {
            if (info == null) return;
            EditorGUILayout.BeginHorizontal();
            _context.DrawBadge(info.isCheckpoint ? new Color(0.23f, 0.46f, 0.80f, 0.18f) : new Color(0.45f, 0.35f, 0.78f, 0.18f), info.isCheckpoint ? AvatarSmartBackup.Localization.L.T("vc.badge.checkpoint", "Checkpoint") : AvatarSmartBackup.Localization.L.T("vc.badge.incremental", "Incremental"));
            if (info.changedFileCount > 0)
                _context.DrawBadge(new Color(0.77f, 0.55f, 0.16f, 0.22f), string.Format(AvatarSmartBackup.Localization.L.T("vc.badge.changed", "Changed: {0}"), info.changedFileCount));
            if (info.removedFileCount > 0)
                _context.DrawBadge(new Color(0.75f, 0.25f, 0.25f, 0.22f), string.Format(AvatarSmartBackup.Localization.L.T("vc.badge.removed", "Removed: {0}"), info.removedFileCount));
            EditorGUILayout.EndHorizontal();
        }
    }
}
#endif
