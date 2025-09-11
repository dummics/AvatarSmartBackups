#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using AvatarSmartBackup.Versioning;
using AvatarSmartBackup.Windows;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Editor menu items for version system testing and access
    /// </summary>
    public static class VersionSystemMenu
    {
        [MenuItem("Tools/Avatar Smart Backup/Version System/Open Timeline", false, 100)]
        public static void OpenVersionTimeline()
        {
            VersionTimelineWindow.ShowWindow();
        }
        
        [MenuItem("Tools/Avatar Smart Backup/Version System/Create Manual Commit", false, 101)]
        public static async void CreateManualCommit()
        {
            try
            {
                string message = EditorInputDialog.Show("Create Version Commit", "Enter commit message:", "Manual commit");
                if (string.IsNullOrEmpty(message)) return;
                
                using var versionManager = new VersionManager();
                var commitId = await versionManager.CreateManualCommitAsync(message);
                
                if (commitId > 0)
                {
                    EditorUtility.DisplayDialog("Version System", $"Commit created successfully!\nCommit ID: {commitId}", "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("Version System", "No changes detected. No commit created.", "OK");
                }
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Error", $"Failed to create commit:\n{ex.Message}", "OK");
            }
        }
        
        [MenuItem("Tools/Avatar Smart Backup/Version System/Show Statistics", false, 102)]
        public static void ShowVersionStatistics()
        {
            try
            {
                using var versionManager = new VersionManager();
                var stats = versionManager.GetStats();
                
                var message = $"Version System Statistics:\n\n" +
                             $"• Total Commits: {stats.TotalCommits}\n" +
                             $"• Storage Used: {stats.DisplaySize}\n" +
                             $"• Time Span: {stats.TimeSpan?.Days ?? 0} days\n" +
                             $"• Storage Directory: {stats.StorageDirectory}\n" +
                             $"• Oldest Commit: {stats.OldestCommit?.ToString("yyyy-MM-dd HH:mm") ?? "None"}\n" +
                             $"• Latest Commit: {stats.NewestCommit?.ToString("yyyy-MM-dd HH:mm") ?? "None"}";
                
                EditorUtility.DisplayDialog("Version System Statistics", message, "OK");
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Error", $"Failed to retrieve statistics:\n{ex.Message}", "OK");
            }
        }
        
        [MenuItem("Tools/Avatar Smart Backup/Version System/Test Auto Commit", false, 103)]
        public static async void TestAutoCommit()
        {
            try
            {
                using var versionManager = new VersionManager();
                var commitId = await versionManager.CreateAutomaticCommitAsync("Menu test");
                
                if (commitId > 0)
                {
                    EditorUtility.DisplayDialog("Version System", $"Auto commit created successfully!\nCommit ID: {commitId}", "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("Version System", "No changes detected. No commit created.", "OK");
                }
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Error", $"Failed to create auto commit:\n{ex.Message}", "OK");
            }
        }
        
        [MenuItem("Tools/Avatar Smart Backup/Version System/Open Storage Folder", false, 104)]
        public static void OpenStorageFolder()
        {
            try
            {
                using var versionManager = new VersionManager();
                var stats = versionManager.GetStats();
                var path = stats.StorageDirectory;
                
                if (System.IO.Directory.Exists(path))
                {
                    EditorUtility.RevealInFinder(path);
                }
                else
                {
                    EditorUtility.DisplayDialog("Version System", $"Storage directory does not exist:\n{path}", "OK");
                }
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Error", $"Failed to open storage folder:\n{ex.Message}", "OK");
            }
        }
    }
    
    /// <summary>
    /// Simple input dialog for Unity Editor
    /// </summary>
    public class EditorInputDialog : EditorWindow
    {
        public string inputText = "";
        public string promptText = "";
        public string titleText = "";
        private System.Action<string> onResult;
        
        public static string Show(string title, string prompt, string defaultText = "")
        {
            var window = GetWindow<EditorInputDialog>(true, title);
            window.titleText = title;
            window.promptText = prompt;
            window.inputText = defaultText;
            window.minSize = new Vector2(300, 120);
            window.maxSize = new Vector2(400, 120);
            window.ShowModal();
            
            // Wait for result
            string result = null;
            window.onResult = (text) => result = text;
            
            // Simple blocking wait
            while (window != null && result == null)
            {
                System.Threading.Thread.Sleep(50);
            }
            
            return result ?? "";
        }
        
        void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(promptText, EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(5);
            
            GUI.SetNextControlName("InputField");
            inputText = EditorGUILayout.TextField(inputText);
            
            EditorGUILayout.Space(10);
            
            EditorGUILayout.BeginHorizontal();
            
            if (GUILayout.Button("OK"))
            {
                onResult?.Invoke(inputText);
                Close();
            }
            
            if (GUILayout.Button("Cancel"))
            {
                onResult?.Invoke("");
                Close();
            }
            
            EditorGUILayout.EndHorizontal();
            
            // Auto focus input field
            if (Event.current.type == EventType.Repaint)
            {
                GUI.FocusControl("InputField");
            }
            
            // Handle Enter key
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return)
            {
                onResult?.Invoke(inputText);
                Close();
            }
        }
    }
}
#endif
