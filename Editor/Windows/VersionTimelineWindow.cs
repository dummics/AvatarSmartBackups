#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using AvatarSmartBackup.Versioning;
using System.Threading.Tasks;
using System.IO;

namespace AvatarSmartBackup.Windows
{
    /// <summary>
    /// Git-like timeline window for version management
    /// Provides artist-friendly interface for navigating backup history
    /// </summary>
    public class VersionTimelineWindow : EditorWindow
    {
        private VersionManager _versionManager;
        private List<CommitInfo> _commits = new List<CommitInfo>();
        private List<VersionedFile> _selectedCommitFiles = new List<VersionedFile>();
        private int _selectedCommitIndex = -1;
        private Vector2 _timelineScroll;
        private Vector2 _filesScroll;
        private Vector2 _detailsScroll;
        private string _newCommitMessage = "";
        private bool _autoRefresh = true;
        private float _lastRefresh = 0f;
        
        // UI State
        private bool _showDetails = true;
        private bool _showFileContents = false;
        private string _tempRestoreDir = "";
        private HashSet<string> _selectedFiles = new HashSet<string>();
        
        [MenuItem("Tools/Avatar Smart Backup/Version Timeline")]
        public static void ShowWindow()
        {
            var window = GetWindow<VersionTimelineWindow>("Version Timeline");
            window.minSize = new Vector2(800, 600);
            window.Initialize();
        }
        
        private void Initialize()
        {
            var settings = BackupManager.LoadSettings();
            _versionManager = new VersionManager(settings);
            RefreshTimeline();
        }
        
        private void OnEnable()
        {
            if (_versionManager == null)
            {
                Initialize();
            }
        }
        
        private void OnDisable()
        {
            CleanupTempRestore();
        }
        
        private void OnGUI()
        {
            if (_versionManager == null)
            {
                EditorGUILayout.HelpBox("Version system not initialized. Please check backup settings.", MessageType.Error);
                return;
            }
            
            DrawToolbar();
            
            EditorGUILayout.BeginHorizontal();
            
            // Left panel: Timeline
            EditorGUILayout.BeginVertical(GUILayout.Width(350));
            DrawTimelinePanel();
            EditorGUILayout.EndVertical();
            
            // Right panel: Details
            EditorGUILayout.BeginVertical();
            DrawDetailsPanel();
            EditorGUILayout.EndVertical();
            
            EditorGUILayout.EndHorizontal();
            
            // Auto-refresh
            if (_autoRefresh && Time.realtimeSinceStartup - _lastRefresh > 5f)
            {
                RefreshTimeline();
                _lastRefresh = Time.realtimeSinceStartup;
            }
        }
        
        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            
            // Create commit section
            EditorGUILayout.LabelField("Create:", GUILayout.Width(50));
            
            if (GUILayout.Button("Auto Commit", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                CreateAutoCommit();
            }
            
            _newCommitMessage = EditorGUILayout.TextField(_newCommitMessage, EditorStyles.toolbarTextField, GUILayout.Width(200));
            
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_newCommitMessage)))
            {
                if (GUILayout.Button("Manual Commit", EditorStyles.toolbarButton, GUILayout.Width(100)))
                {
                    CreateManualCommit();
                }
            }
            
            GUILayout.FlexibleSpace();
            
            // Refresh and settings
            _autoRefresh = GUILayout.Toggle(_autoRefresh, "Auto Refresh", EditorStyles.toolbarButton);
            
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                RefreshTimeline();
            }
            
            if (GUILayout.Button("Cleanup", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                CleanupVersions();
            }
            
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawTimelinePanel()
        {
            EditorGUILayout.LabelField("Timeline", EditorStyles.boldLabel);
            
            if (_commits.Count == 0)
            {
                EditorGUILayout.HelpBox("No commits found. Create your first commit using the toolbar above.", MessageType.Info);
                return;
            }
            
            _timelineScroll = EditorGUILayout.BeginScrollView(_timelineScroll, "box");
            
            for (int i = 0; i < _commits.Count; i++)
            {
                var commit = _commits[i];
                bool isSelected = i == _selectedCommitIndex;
                
                // Timeline entry background
                var rect = EditorGUILayout.BeginVertical(isSelected ? "selectionRect" : "box");
                
                if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
                {
                    _selectedCommitIndex = i;
                    LoadCommitFiles();
                    Event.current.Use();
                }
                
                // Commit header
                EditorGUILayout.BeginHorizontal();
                
                // Timeline dot
                GUIStyle dotStyle = new GUIStyle();
                dotStyle.normal.background = EditorGUIUtility.whiteTexture;
                dotStyle.fixedWidth = 8;
                dotStyle.fixedHeight = 8;
                dotStyle.margin = new RectOffset(5, 5, 6, 0);
                
                Color originalColor = GUI.backgroundColor;
                GUI.backgroundColor = isSelected ? Color.yellow : Color.gray;
                GUILayout.Box("", dotStyle);
                GUI.backgroundColor = originalColor;
                
                // Commit info
                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField(commit.DisplayName, EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"{commit.DisplayTime} • {commit.FileCount} files • {commit.DisplaySize}", EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                
                EditorGUILayout.EndHorizontal();
                
                // Timeline line (except for last item)
                if (i < _commits.Count - 1)
                {
                    var lineRect = GUILayoutUtility.GetRect(2, 10);
                    lineRect.x += 8;
                    lineRect.width = 2;
                    EditorGUI.DrawRect(lineRect, Color.gray);
                }
                
                EditorGUILayout.EndVertical();
                
                GUILayout.Space(2);
            }
            
            EditorGUILayout.EndScrollView();
        }
        
        private void DrawDetailsPanel()
        {
            if (_selectedCommitIndex < 0 || _selectedCommitIndex >= _commits.Count)
            {
                EditorGUILayout.HelpBox("Select a commit from the timeline to view details.", MessageType.Info);
                return;
            }
            
            var selectedCommit = _commits[_selectedCommitIndex];
            
            EditorGUILayout.LabelField("Commit Details", EditorStyles.boldLabel);
            
            // Commit info box
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Hash:", selectedCommit.Hash);
            EditorGUILayout.LabelField("Message:", selectedCommit.Message);
            EditorGUILayout.LabelField("Time:", selectedCommit.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            EditorGUILayout.LabelField("Files:", $"{selectedCommit.FileCount} files ({selectedCommit.DisplaySize})");
            EditorGUILayout.EndVertical();
            
            GUILayout.Space(5);
            
            // Action buttons
            EditorGUILayout.BeginHorizontal();
            
            if (GUILayout.Button("Restore All Files", GUILayout.Height(25)))
            {
                RestoreAllFiles();
            }
            
            if (GUILayout.Button("Restore Selected", GUILayout.Height(25)))
            {
                RestoreSelectedFiles();
            }
            
            if (GUILayout.Button("Preview in Temp", GUILayout.Height(25)))
            {
                PreviewInTemp();
            }
            
            EditorGUILayout.EndHorizontal();
            
            GUILayout.Space(5);
            
            // Files list
            EditorGUILayout.LabelField("Files in Commit", EditorStyles.boldLabel);
            
            _filesScroll = EditorGUILayout.BeginScrollView(_filesScroll, "box");
            
            if (_selectedCommitFiles.Count == 0)
            {
                EditorGUILayout.LabelField("Loading files...", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                // File list with checkboxes
                foreach (var file in _selectedCommitFiles)
                {
                    if (file.Action == "delete") continue; // Skip deleted files
                    
                    EditorGUILayout.BeginHorizontal();
                    
                    bool wasSelected = _selectedFiles.Contains(file.Path);
                    bool isSelected = EditorGUILayout.Toggle(wasSelected, GUILayout.Width(20));
                    
                    if (isSelected != wasSelected)
                    {
                        if (isSelected)
                            _selectedFiles.Add(file.Path);
                        else
                            _selectedFiles.Remove(file.Path);
                    }
                    
                    // File icon based on extension
                    string icon = GetFileIcon(file.Path);
                    GUIContent fileContent = new GUIContent($"{icon} {file.Path}", GetFileTooltip(file));
                    EditorGUILayout.LabelField(fileContent);
                    
                    // Action indicator
                    GUIStyle actionStyle = new GUIStyle(EditorStyles.miniLabel);
                    actionStyle.normal.textColor = GetActionColor(file.Action);
                    EditorGUILayout.LabelField(file.Action.ToUpper(), actionStyle, GUILayout.Width(60));
                    
                    EditorGUILayout.EndHorizontal();
                }
            }
            
            EditorGUILayout.EndScrollView();
            
            // Temp restore info
            if (!string.IsNullOrEmpty(_tempRestoreDir))
            {
                EditorGUILayout.Space();
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("Temporary Restore:", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(_tempRestoreDir, EditorStyles.wordWrappedMiniLabel);
                
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Open Folder"))
                {
                    EditorUtility.RevealInFinder(_tempRestoreDir);
                }
                if (GUILayout.Button("Cleanup Temp"))
                {
                    CleanupTempRestore();
                }
                EditorGUILayout.EndHorizontal();
                
                EditorGUILayout.EndVertical();
            }
        }
        
        private void CreateAutoCommit()
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    var commitId = await _versionManager.CreateAutomaticCommitAsync("Manual auto commit");
                    if (commitId > 0)
                    {
                        await UnityMainThread.InvokeAsync(() =>
                        {
                            RefreshTimeline();
                            Log.Info($"Auto commit created: {commitId}");
                        });
                    }
                    else
                    {
                        await UnityMainThread.InvokeAsync(() => 
                            Log.Info("No changes detected for auto commit"));
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to create auto commit: {ex.Message}");
            }
        }
        
        private void CreateManualCommit()
        {
            if (string.IsNullOrWhiteSpace(_newCommitMessage)) return;
            
            try
            {
                string message = _newCommitMessage;
                _newCommitMessage = "";
                
                _ = Task.Run(async () =>
                {
                    var commitId = await _versionManager.CreateManualCommitAsync(message);
                    await UnityMainThread.InvokeAsync(() =>
                    {
                        RefreshTimeline();
                        Log.Info($"Manual commit created: {commitId}");
                    });
                });
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to create manual commit: {ex.Message}");
            }
        }
        
        private void RefreshTimeline()
        {
            try
            {
                _commits = _versionManager.GetTimeline(50);
                if (_selectedCommitIndex >= _commits.Count)
                {
                    _selectedCommitIndex = -1;
                    _selectedCommitFiles.Clear();
                }
                Repaint();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to refresh timeline: {ex.Message}");
            }
        }
        
        private void LoadCommitFiles()
        {
            if (_selectedCommitIndex < 0) return;
            
            try
            {
                var commit = _commits[_selectedCommitIndex];
                _selectedCommitFiles = _versionManager.GetCommitFiles(commit.Id);
                _selectedFiles.Clear();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load commit files: {ex.Message}");
            }
        }
        
        private void RestoreAllFiles()
        {
            if (_selectedCommitIndex < 0) return;
            
            var commit = _commits[_selectedCommitIndex];
            var allFiles = _selectedCommitFiles.Where(f => f.Action != "delete").Select(f => f.Path).ToList();
            
            if (EditorUtility.DisplayDialog("Restore All Files", 
                $"This will restore {allFiles.Count} files from commit '{commit.DisplayName}'. A backup of current state will be created.\n\nContinue?", 
                "Restore", "Cancel"))
            {
                PerformRestore(commit.Id, allFiles);
            }
        }
        
        private void RestoreSelectedFiles()
        {
            if (_selectedCommitIndex < 0 || _selectedFiles.Count == 0) return;
            
            var commit = _commits[_selectedCommitIndex];
            
            if (EditorUtility.DisplayDialog("Restore Selected Files", 
                $"This will restore {_selectedFiles.Count} selected files from commit '{commit.DisplayName}'. A backup of current state will be created.\n\nContinue?", 
                "Restore", "Cancel"))
            {
                PerformRestore(commit.Id, _selectedFiles.ToList());
            }
        }
        
        private void PreviewInTemp()
        {
            if (_selectedCommitIndex < 0) return;
            
            var commit = _commits[_selectedCommitIndex];
            var filesToPreview = _selectedFiles.Count > 0 ? _selectedFiles.ToList() : 
                _selectedCommitFiles.Where(f => f.Action != "delete").Select(f => f.Path).ToList();
            
            CleanupTempRestore();
            
            _ = Task.Run(async () =>
            {
                try
                {
                    _tempRestoreDir = await _versionManager.RestoreToTempAsync(commit.Id, filesToPreview);
                    await UnityMainThread.InvokeAsync(() =>
                    {
                        Log.Info($"Files previewed in: {_tempRestoreDir}");
                        Repaint();
                    });
                }
                catch (Exception ex)
                {
                    await UnityMainThread.InvokeAsync(() => 
                        Log.Error($"Failed to preview files: {ex.Message}"));
                }
            });
        }
        
        private void PerformRestore(long commitId, List<string> filePaths)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _versionManager.RestoreToProjectAsync(commitId, filePaths, true);
                    
                    await UnityMainThread.InvokeAsync(() =>
                    {
                        if (result.IsSuccess)
                        {
                            Log.Info($"Restore completed: {result.RestoredFiles.Count} files restored");
                            if (result.BackupCommitId.HasValue)
                            {
                                Log.Info($"Pre-restore backup created: {result.BackupCommitId}");
                            }
                            AssetDatabase.Refresh();
                            RefreshTimeline();
                        }
                        else
                        {
                            string errorMsg = result.Error ?? $"Some files failed to restore: {result.FailedFiles.Count}";
                            Log.Error($"Restore failed: {errorMsg}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    await UnityMainThread.InvokeAsync(() => 
                        Log.Error($"Restore operation failed: {ex.Message}"));
                }
            });
        }
        
        private void CleanupVersions()
        {
            if (EditorUtility.DisplayDialog("Cleanup Old Versions", 
                "This will remove old versions according to retention policy. This action cannot be undone.\n\nContinue?", 
                "Cleanup", "Cancel"))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _versionManager.CleanupAsync();
                        await UnityMainThread.InvokeAsync(() =>
                        {
                            RefreshTimeline();
                            Log.Info("Version cleanup completed");
                        });
                    }
                    catch (Exception ex)
                    {
                        await UnityMainThread.InvokeAsync(() => 
                            Log.Error($"Cleanup failed: {ex.Message}"));
                    }
                });
            }
        }
        
        private void CleanupTempRestore()
        {
            if (!string.IsNullOrEmpty(_tempRestoreDir) && Directory.Exists(_tempRestoreDir))
            {
                try
                {
                    Directory.Delete(_tempRestoreDir, true);
                }
                catch { }
                finally
                {
                    _tempRestoreDir = "";
                }
            }
        }
        
        private string GetFileIcon(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLower();
            return ext switch
            {
                ".controller" => "🎮",
                ".anim" => "🎭",
                ".unity" => "🌍",
                ".mat" => "🎨",
                ".asset" => "📄",
                ".cs" => "📜",
                ".dll" => "⚙️",
                ".prefab" => "🧩",
                _ => "📁"
            };
        }
        
        private string GetFileTooltip(VersionedFile file)
        {
            return $"Path: {file.Path}\nSize: {FormatBytes(file.Size)}\nAction: {file.Action}\nHash: {file.Hash[..8]}...";
        }
        
        private Color GetActionColor(string action)
        {
            return action switch
            {
                "add" => Color.green,
                "modify" => Color.yellow,
                "delete" => Color.red,
                "unchanged" => Color.gray,
                _ => Color.white
            };
        }
        
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }
    }
    
    /// <summary>
    /// Helper for cross-thread Unity operations
    /// </summary>
    internal static class UnityMainThread
    {
        public static Task InvokeAsync(Action action)
        {
            var tcs = new TaskCompletionSource<bool>();
            
            EditorApplication.delayCall += () =>
            {
                try
                {
                    action();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            };
            
            return tcs.Task;
        }
    }
}
#endif
