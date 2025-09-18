#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using AvatarSmartBackup.Localization;
namespace AvatarSmartBackup
{
    public class VersionHistoryWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private List<VersionInfo> _versions = new List<VersionInfo>();
        private bool _isLoading = true;
        private string _errorMessage = null;
        // [MenuItem("Tools/Avatar Smart Backup/Versions")]
        public static void Open()
        {
            var window = GetWindow<VersionHistoryWindow>(true, "Backup Versions");
            window.minSize = new Vector2(500, 400);
            window.maxSize = new Vector2(800, 600);
            window.Show();
        }
        void OnEnable()
        {
            BackupEvents.BackupCompleted += OnBackupCompleted;
            RefreshVersions();
        }
        void OnDisable()
        {
            BackupEvents.BackupCompleted -= OnBackupCompleted;
        }
        void OnBackupCompleted(BackupRunSummary summary)
        {
            RefreshVersions();
            Repaint();
        }

        void RefreshVersions()
        {
            _isLoading = true;
            _errorMessage = null;
            try
            {
                using var versionManager = new FileBasedVersionManager();
                _versions = versionManager.GetVersions();
                _isLoading = false;
            }
            catch (Exception ex)
            {
                _errorMessage = ex.Message;
                _isLoading = false;
                Debug.LogError($"Failed to load versions: {ex.Message}");
            }
        }
        void OnGUI()
        {
            // Header
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L.T("vh.title", "Backup Versions"), EditorStyles.boldLabel);
            if (GUILayout.Button(L.T("vh.refresh", "Refresh"), GUILayout.Width(80)))
            {
                RefreshVersions();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(L.T("window.version.history.help", "Versions are of two types: full checkpoints and incremental. Incremental versions store only changes since the previous checkpoint."), MessageType.Info);
            // Status
            if (_isLoading)
            {
                EditorGUILayout.HelpBox(L.T("vh.loading", "Loading versions..."), MessageType.Info);
                return;
            }
            if (_errorMessage != null)
            {
                EditorGUILayout.HelpBox(string.Format(L.T("vh.load.error", "Error loading versions: {0}"), _errorMessage), MessageType.Error);
                return;
            }
            if (_versions.Count == 0)
            {
                EditorGUILayout.HelpBox(L.T("vh.none", "No versions found. Versions are automatically created after each backup with changes."), MessageType.Info);
                return;
            }
            // Stats
            EditorGUILayout.LabelField(string.Format(L.T("vh.total", "Total Versions: {0}"), _versions.Count), EditorStyles.miniBoldLabel);
            // Versions list
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            for (int i = 0; i < _versions.Count; i++)
            {
                var version = _versions[i];
                bool isMostRecent = i == 0;
                EditorGUILayout.BeginVertical("box");
                // Version header
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(string.Format(L.T("vh.version.header", "Version #{0}"), version.id), EditorStyles.boldLabel);
                if (isMostRecent)
                {
                    GUI.contentColor = Color.green;
                    EditorGUILayout.LabelField(L.T("vh.latest", "Latest"), EditorStyles.miniBoldLabel, GUILayout.Width(50));
                    GUI.contentColor = Color.white;
                }
                EditorGUILayout.EndHorizontal();
                // Version details
                EditorGUILayout.LabelField(string.Format(L.T("vh.desc", "Description: {0}"), version.description), EditorStyles.miniLabel);
                EditorGUILayout.LabelField(L.T("vh.created", "Created:") + " " + version.timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), EditorStyles.miniLabel);
                // Actions
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(L.T("vh.restore", "Restore to This Version"), GUILayout.Height(25)))
                {
                    if (EditorUtility.DisplayDialog(L.T("vh.restore.confirm.title", "Confirm Restore"),
                        string.Format(L.T("vh.restore.confirm.body", "Are you sure you want to restore to version #{0}?\n\nDescription: {1}\nCreated: {2}\n\nThis will replace your current Assets with the backup from this version."), version.id, version.description, version.timestamp.ToLocalTime()),
                        L.T("vh.restore.confirm.ok", "Restore"), L.T("vh.restore.confirm.cancel", "Cancel")))
                    {
                        PerformRestore(version);
                    }
                }
                if (GUILayout.Button(L.T("vh.open.folder", "Open Folder"), GUILayout.Width(100), GUILayout.Height(25)))
                {
                    string versionDir = Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{version.id:D3}");
                    if (Directory.Exists(versionDir))
                    {
                        EditorUtility.RevealInFinder(versionDir);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog(L.T("vh.folder.notfound.title", "Folder Not Found"), L.T("vh.folder.notfound.body", "The version folder could not be found."), "OK");
                    }
                }
                if (GUILayout.Button(L.T("ui.versions.previewRestore", "Preview & Restore"), GUILayout.Width(110), GUILayout.Height(25)))
                {
                    try
                    {
                        RestorePreviewWindow.Open(version.id);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Preview & Restore failed: " + ex.Message);
                        var message = string.Format(L.T("vc.error.body", "Unable to read changes:\n{0}"), ex.Message);
                        EditorUtility.DisplayDialog(L.T("vc.error.title", "Preview & Restore"), message, "OK");
                    }
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(5);
            }
            EditorGUILayout.EndScrollView();
            // Footer info
            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox(L.T("vh.tip.cleanup", "💡 Tip: Versions are automatically cleaned up to keep only the 10 most recent ones."), MessageType.Info);
        }
        void PerformRestore(VersionInfo version)
        {
            try
            {
                // For now, just show the backup folder
                // In a full implementation, this would copy files back to Assets
                string backupPath = version.backupPath;
                if (Directory.Exists(backupPath))
                {
                    EditorUtility.RevealInFinder(backupPath);
                    EditorUtility.DisplayDialog(L.T("vh.restore.location.title", "Restore Location"),
                        string.Format(L.T("vh.restore.location.body", "The backup files for version #{0} are located at:\n\n{1}\n\nYou can manually copy the files back to your Assets folder if needed."), version.id, backupPath),
                        "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog(L.T("vh.restore.notfound.title", "Backup Not Found"),
                        string.Format(L.T("vh.restore.notfound.body", "The backup folder for version #{0} could not be found at:\n\n{1}"), version.id, backupPath),
                        "OK");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(L.T("vh.restore.failed.title", "Restore Failed"), string.Format(L.T("vh.restore.failed.body", "Failed to restore version: {0}"), ex.Message), "OK");
                Debug.LogError("Restore failed: " + ex.Message);
            }
        }
    }
}
#endif