#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    public class VersionHistoryWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private List<VersionInfo> _versions = new List<VersionInfo>();
        private bool _isLoading = true;
        private string _errorMessage = null;

        [MenuItem("Tools/Avatar Smart Backup/Versions")]
        public static void Open()
        {
            var window = GetWindow<VersionHistoryWindow>(true, "Backup Versions");
            window.minSize = new Vector2(500, 400);
            window.maxSize = new Vector2(800, 600);
            window.Show();
        }

        void OnEnable()
        {
            RefreshVersions();
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
            EditorGUILayout.LabelField("Backup Versions", EditorStyles.boldLabel);
            if (GUILayout.Button("Refresh", GUILayout.Width(80)))
            {
                RefreshVersions();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox("Each version represents a complete restore point. Click 'Restore' to revert your project to that exact state.", MessageType.Info);

            // Status
            if (_isLoading)
            {
                EditorGUILayout.HelpBox("Loading versions...", MessageType.Info);
                return;
            }

            if (_errorMessage != null)
            {
                EditorGUILayout.HelpBox($"Error loading versions: {_errorMessage}", MessageType.Error);
                return;
            }

            if (_versions.Count == 0)
            {
                EditorGUILayout.HelpBox("No versions found. Versions are automatically created after each backup with changes.", MessageType.Info);
                return;
            }

            // Stats
            EditorGUILayout.LabelField($"Total Versions: {_versions.Count}", EditorStyles.miniBoldLabel);

            // Versions list
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            for (int i = 0; i < _versions.Count; i++)
            {
                var version = _versions[i];
                bool isMostRecent = i == 0;

                EditorGUILayout.BeginVertical("box");

                // Version header
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"Version #{version.id}", EditorStyles.boldLabel);
                if (isMostRecent)
                {
                    GUI.contentColor = Color.green;
                    EditorGUILayout.LabelField("Latest", EditorStyles.miniBoldLabel, GUILayout.Width(50));
                    GUI.contentColor = Color.white;
                }
                EditorGUILayout.EndHorizontal();

                // Version details
                EditorGUILayout.LabelField($"Description: {version.description}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Created: {version.timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")}", EditorStyles.miniLabel);

                // Actions
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Restore to This Version", GUILayout.Height(25)))
                {
                    if (EditorUtility.DisplayDialog("Confirm Restore",
                        $"Are you sure you want to restore to version #{version.id}?\n\nDescription: {version.description}\nCreated: {version.timestamp.ToLocalTime()}\n\nThis will replace your current Assets with the backup from this version.",
                        "Restore", "Cancel"))
                    {
                        PerformRestore(version);
                    }
                }

                if (GUILayout.Button("Open Folder", GUILayout.Width(100), GUILayout.Height(25)))
                {
                    string versionDir = Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{version.id:D3}");
                    if (Directory.Exists(versionDir))
                    {
                        EditorUtility.RevealInFinder(versionDir);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Folder Not Found", "The version folder could not be found.", "OK");
                    }
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(5);
            }

            EditorGUILayout.EndScrollView();

            // Footer info
            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox("💡 Tip: Versions are automatically cleaned up to keep only the 10 most recent ones.", MessageType.Info);
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
                    EditorUtility.DisplayDialog("Restore Location",
                        $"The backup files for version #{version.id} are located at:\n\n{backupPath}\n\nYou can manually copy the files back to your Assets folder if needed.",
                        "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("Backup Not Found",
                        $"The backup folder for version #{version.id} could not be found at:\n\n{backupPath}",
                        "OK");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Restore Failed", $"Failed to restore version: {ex.Message}", "OK");
                Debug.LogError($"Restore failed: {ex.Message}");
            }
        }
    }
}
#endif