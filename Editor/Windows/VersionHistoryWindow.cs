#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using AvatarSmartBackup.Versioning;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Simple version history viewer - designed for VRChat creators
    /// Shows all backup versions with easy restore functionality
    /// </summary>
    public class VersionHistoryWindow : EditorWindow
    {
        private Vector2 _scrollPos;
        private List<BackupVersion> _versions = new List<BackupVersion>();
        private SimpleVersionStats _stats;
        private bool _isLoading = true;
        private string _errorMessage = null;
        private BackupVersion _selectedVersion = null;
        
        // UI state
        private bool _showAdvancedInfo = false;
        private string _searchFilter = "";

        public static void Open()
        {
            var window = GetWindow<VersionHistoryWindow>(true, "Safety History", true);
            window.minSize = new Vector2(500, 400);
            window.maxSize = new Vector2(800, 800);
            window.Show();
            window.RefreshVersions();
        }

        void OnEnable()
        {
            RefreshVersions();
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Safety History", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Your backup versions - each one is a complete restore point. Click any version to restore your project to that exact state.", MessageType.Info);

            // Header with stats and refresh
            DrawHeader();
            
            EditorGUILayout.Space(5);
            
            // Search and filters
            DrawFilters();
            
            EditorGUILayout.Space(5);
            
            // Main content
            if (_isLoading)
            {
                EditorGUILayout.LabelField("Loading versions...", EditorStyles.centeredGreyMiniLabel);
            }
            else if (!string.IsNullOrEmpty(_errorMessage))
            {
                EditorGUILayout.HelpBox($"Error loading versions: {_errorMessage}", MessageType.Warning);
                if (GUILayout.Button("Try Again"))
                {
                    RefreshVersions();
                }
            }
            else if (_versions.Count == 0)
            {
                DrawEmptyState();
            }
            else
            {
                DrawVersionList();
            }
            
            EditorGUILayout.Space(10);
            DrawFooter();
        }

        void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            
            if (_stats != null)
            {
                EditorGUILayout.LabelField($"📊 {_stats.DisplayVersions} • {_stats.DisplaySize} total", EditorStyles.boldLabel);
            }
            
            GUILayout.FlexibleSpace();
            
            if (GUILayout.Button(new GUIContent("🔄", "Refresh version list"), GUILayout.Width(30)))
            {
                RefreshVersions();
            }
            
            if (GUILayout.Button(new GUIContent("📁", "Open backup folder"), GUILayout.Width(30)))
            {
                EditorUtility.RevealInFinder(FileUtilEx.BackupRoot);
            }
            
            EditorGUILayout.EndHorizontal();
        }

        void DrawFilters()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
            string newFilter = EditorGUILayout.TextField(_searchFilter);
            if (newFilter != _searchFilter)
            {
                _searchFilter = newFilter;
                // Filter will be applied in DrawVersionList
            }
            
            GUILayout.FlexibleSpace();
            _showAdvancedInfo = EditorGUILayout.ToggleLeft("Show details", _showAdvancedInfo, GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();
        }

        void DrawEmptyState()
        {
            EditorGUILayout.Space(20);
            EditorGUILayout.LabelField("No versions yet", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Versions will appear here after your first backup", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.Space(20);
            
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Create First Backup", GUILayout.Width(150), GUILayout.Height(25)))
            {
                var settings = BackupManager.LoadSettings();
                BackupManager.RunBackupNow(settings, showToast: true, reason: "manual", showProgressUI: true);
                EditorUtility.DisplayDialog("Backup Started", 
                    "Your first backup is running!\n\nOnce completed, it will appear here as your first version.", "OK");
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        void DrawVersionList()
        {
            var filteredVersions = _versions;
            
            // Apply search filter
            if (!string.IsNullOrEmpty(_searchFilter))
            {
                filteredVersions = _versions.Where(v => 
                    v.Description.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    v.GetDateTime().ToString("yyyy-MM-dd HH:mm").Contains(_searchFilter)
                ).ToList();
            }

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            for (int i = 0; i < filteredVersions.Count; i++)
            {
                var version = filteredVersions[i];
                DrawVersionItem(version, i == 0); // First item is most recent
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawVersionItem(BackupVersion version, bool isMostRecent)
        {
            var bgColor = _selectedVersion?.Id == version.Id ? new Color(0.3f, 0.5f, 1f, 0.3f) : Color.clear;
            
            using (new EditorGUILayout.HorizontalScope("box"))
            {
                if (bgColor != Color.clear)
                {
                    var rect = GUILayoutUtility.GetLastRect();
                    EditorGUI.DrawRect(rect, bgColor);
                }

                EditorGUILayout.BeginVertical();

                // Main info line
                EditorGUILayout.BeginHorizontal();
                
                // Version indicator
                string versionIcon = isMostRecent ? "⭐" : "📄";
                EditorGUILayout.LabelField($"{versionIcon} #{version.Id}", GUILayout.Width(60));
                
                // Description and timestamp
                var dateTime = version.GetDateTime().ToLocalTime();
                string timeAgo = GetTimeAgo(dateTime);
                EditorGUILayout.LabelField($"{version.Description} • {dateTime:MMM dd, HH:mm} ({timeAgo})", EditorStyles.boldLabel);
                
                GUILayout.FlexibleSpace();
                
                // Restore button
                bool canRestore = !BackupManager.IsBusy;
                using (new EditorGUI.DisabledScope(!canRestore))
                {
                    if (GUILayout.Button(new GUIContent("🔄 Restore", canRestore ? "Restore project to this version" : "Cannot restore while backup is running"), GUILayout.Width(80)))
                    {
                        RestoreToVersion(version);
                    }
                }
                
                EditorGUILayout.EndHorizontal();

                // Additional info if enabled
                if (_showAdvancedInfo)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"Files: {version.FileCount} • Size: {version.GetDisplaySize()}", EditorStyles.miniLabel);
                    if (version.HasSnapshot)
                    {
                        EditorGUILayout.LabelField("📦 Snapshot available", EditorStyles.miniLabel, GUILayout.Width(120));
                    }
                    EditorGUILayout.EndHorizontal();
                    
                    if (!string.IsNullOrEmpty(version.BackupPath))
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField($"Path: {version.BackupPath}", EditorStyles.miniLabel);
                        if (GUILayout.Button("📁", GUILayout.Width(25)))
                        {
                            var fullPath = System.IO.Path.Combine(FileUtilEx.BackupRoot, version.BackupPath);
                            if (System.IO.Directory.Exists(fullPath))
                            {
                                EditorUtility.RevealInFinder(fullPath);
                            }
                            else
                            {
                                EditorUtility.DisplayDialog("Path Not Found", $"Backup folder no longer exists:\n{fullPath}", "OK");
                            }
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                }

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space(2);
        }

        void DrawFooter()
        {
            EditorGUILayout.BeginHorizontal("box");
            
            EditorGUILayout.LabelField("💡 Tip: Each backup automatically becomes a restore point", EditorStyles.miniLabel);
            
            GUILayout.FlexibleSpace();
            
            if (GUILayout.Button("Close"))
            {
                Close();
            }
            
            EditorGUILayout.EndHorizontal();
        }

        void RefreshVersions()
        {
            _isLoading = true;
            _errorMessage = null;
            
            try
            {
                using var versionManager = new SimpleVersionManager();
                _versions = versionManager.GetRecentVersions(100); // Get last 100 versions
                _stats = versionManager.GetStats();
                _isLoading = false;
            }
            catch (Exception ex)
            {
                _errorMessage = ex.Message;
                _isLoading = false;
                _versions.Clear();
            }
            
            Repaint();
        }

        void RestoreToVersion(BackupVersion version)
        {
            if (BackupManager.IsBusy)
            {
                EditorUtility.DisplayDialog("Backup In Progress", 
                    "Cannot restore while a backup is running. Please wait for the current backup to complete.", "OK");
                return;
            }

            var result = EditorUtility.DisplayDialogComplex("Restore Confirmation",
                $"Restore project to version #{version.Id}?\n\n" +
                $"Version: {version.Description}\n" +
                $"Date: {version.GetDateTime().ToLocalTime():yyyy-MM-dd HH:mm}\n" +
                $"Files: {version.FileCount} ({version.GetDisplaySize()})\n\n" +
                "This will replace your current Assets with this version's files.\n" +
                "A backup of your current state will be created first.",
                "Restore", "Cancel", "Preview Files");

            switch (result)
            {
                case 0: // Restore
                    PerformRestore(version);
                    break;
                case 1: // Cancel
                    break;
                case 2: // Preview Files
                    ShowPreview(version);
                    break;
            }
        }

        void PerformRestore(BackupVersion version)
        {
            try
            {
                // Use the new simple restore functionality
                _ = SimpleRestore.RestoreToVersionAsync(version, showProgress: true);
                
                // Close this window after starting restore
                Close();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Restore Failed", 
                    $"Failed to start restore:\n\n{ex.Message}", "OK");
            }
        }

        void ShowPreview(BackupVersion version)
        {
            SimpleRestore.PreviewVersion(version);
        }

        string GetTimeAgo(DateTime dateTime)
        {
            var timeSpan = DateTime.Now - dateTime;
            
            if (timeSpan.TotalMinutes < 1) return "just now";
            if (timeSpan.TotalMinutes < 60) return $"{(int)timeSpan.TotalMinutes}m ago";
            if (timeSpan.TotalHours < 24) return $"{(int)timeSpan.TotalHours}h ago";
            if (timeSpan.TotalDays < 7) return $"{(int)timeSpan.TotalDays}d ago";
            return dateTime.ToString("MMM dd");
        }
    }
}
#endif