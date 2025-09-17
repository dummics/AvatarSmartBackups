#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Consolidated system diagnostics - visible only in advanced mode
    /// </summary>
    public static class SystemDiagnostics
    {
        // Only show debug menu items when advanced mode is enabled
    //[MenuItem("Tools/Avatar Smart Backup Debug/Tests/Test Versioning (File-Based)", false, 900)]
        public static void TestFileVersioning()
        {
            var settings = BackupManager.LoadSettings();
            if (!settings.DiagnosticsEnabled)
            {
                EditorUtility.DisplayDialog("Debug Mode Required", 
                    "Debug tests are only available when Debug Mode is enabled in Advanced Settings.", "OK");
                return;
            }
 
            try
            {
                // FileBasedVersionManager nel namespace AvatarSmartBackup
                using var vm = new FileBasedVersionManager();
                string current = System.IO.Path.Combine(FileUtilEx.BackupRoot, "Current");
                if (!System.IO.Directory.Exists(current))
                {
                    EditorUtility.DisplayDialog("Versioning Test", "No Current/ backup yet. Run a backup first.", "OK");
                    return;
                }
                bool ok = vm.CreateVersion("Test manual version", current, settings, forceCheckpoint: true);
                var list = vm.GetVersions();
                EditorUtility.DisplayDialog("Versioning Test", ok
                    ? $"✅ Created test version. Total versions: {list.Count}" 
                    : "⚠ Failed to create test version.", "OK");
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Versioning Test Failed", $"❌ {ex.Message}", "OK");
                Debug.LogError("Versioning test failed: " + ex);
            }
        }

  //  [MenuItem("Tools/Avatar Smart Backup Debug/Tests/Show System Info", false, 901)]
        public static void ShowSystemInfo()
        {
            var settings = BackupManager.LoadSettings();
            if (!settings.DiagnosticsEnabled)
            {
                EditorUtility.DisplayDialog("Debug Mode Required", 
                    "Debug tests are only available when Debug Mode is enabled in Advanced Settings.", "OK");
                return;
            }

            var info = new System.Text.StringBuilder();
            info.AppendLine($"Unity Version: {Application.unityVersion}");
            info.AppendLine($"Platform: {Application.platform}");
            info.AppendLine($"Scripting Runtime: {System.Environment.Version}");
            info.AppendLine("SQLite (legacy) removed: true");
            info.AppendLine($"Backup Root: {FileUtilEx.BackupRoot}");
            
            EditorUtility.DisplayDialog("System Information", info.ToString(), "OK");
            Debug.Log($"System Info:\n{info}");
        }
        // Validate menu items only show in advanced mode
    //[MenuItem("Tools/Avatar Smart Backup Debug/Tests/Test Versioning (File-Based)", true)]
   // [MenuItem("Tools/Avatar Smart Backup Debug/Tests/Show System Info", true)]
        public static bool ValidateDebugMenus()
        {
            var settings = BackupManager.LoadSettings();
            return settings?.DiagnosticsEnabled == true;
        }
    }
}
#endif
