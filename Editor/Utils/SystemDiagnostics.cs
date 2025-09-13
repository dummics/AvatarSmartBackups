#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Consolidated system diagnostics - visible only in debug mode
    /// </summary>
    public static class SystemDiagnostics
    {
        // Only show debug menu items when debug mode is enabled
        [MenuItem("Tools/Avatar Smart Backup/Debug Tests/Test SQLite Connection", false, 900)]
        public static void TestSQLiteConnection()
        {
            var settings = BackupManager.LoadSettings();
            if (!settings.debugMode)
            {
                EditorUtility.DisplayDialog("Debug Mode Required", 
                    "Debug tests are only available when Debug Mode is enabled in Advanced Settings.", "OK");
                return;
            }

            try
            {
                // Test basic SQLite-net functionality
                using var connection = new SQLite.SQLiteConnection(":memory:");
                
                // Create simple test table
                connection.Execute("CREATE TABLE test (id INTEGER, name TEXT)");
                connection.Execute("INSERT INTO test VALUES (1, 'Hello SQLite-net')");
                
                var result = connection.ExecuteScalar<string>("SELECT name FROM test WHERE id = 1");
                
                if (result == "Hello SQLite-net")
                {
                    EditorUtility.DisplayDialog("SQLite Test", 
                        "✅ SQLite-net is working correctly!\n\n" +
                        "The database backend is ready for version tracking.", "OK");
                    Debug.Log("✅ SQLite-net test passed successfully");
                }
                else
                {
                    throw new System.Exception("Query returned unexpected result");
                }
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("SQLite Test Failed", 
                    $"❌ SQLite-net test failed:\n\n{ex.Message}", "OK");
                Debug.LogError($"❌ SQLite-net test failed: {ex.Message}");
            }
        }

        [MenuItem("Tools/Avatar Smart Backup/Debug Tests/Test Version System", false, 901)]
        public static void TestVersionSystem()
        {
            var settings = BackupManager.LoadSettings();
            if (!settings.debugMode)
            {
                EditorUtility.DisplayDialog("Debug Mode Required", 
                    "Debug tests are only available when Debug Mode is enabled in Advanced Settings.", "OK");
                return;
            }

            try
            {
                // Test the new simple version manager
                using var versionManager = new AvatarSmartBackup.Versioning.SimpleVersionManager();
                
                // Test database connectivity
                if (!versionManager.TestConnection())
                {
                    throw new System.Exception("Database connection failed");
                }

                // Test recording a version
                int versionId = versionManager.RecordBackupAsVersion(
                    "Test version", 
                    System.IO.Path.Combine(FileUtilEx.BackupRoot, "test"), 
                    5, 
                    1024 * 1024
                );

                if (versionId <= 0)
                {
                    throw new System.Exception("Failed to record test version");
                }

                // Test getting stats
                var stats = versionManager.GetStats();
                
                EditorUtility.DisplayDialog("Version System Test", 
                    "✅ Simple Version System is working!\n\n" +
                    $"• Database: Connected\n" +
                    $"• Test version: Created (ID: {versionId})\n" +
                    $"• Total versions: {stats.TotalVersions}\n" +
                    $"• Storage: {stats.DisplaySize}\n\n" +
                    "Version tracking is ready for transparent backup versioning!", "Great!");
                
                Debug.Log($"✅ Version System test passed - Version ID: {versionId}");
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Version System Test Failed", 
                    $"❌ Version System test failed:\n\n{ex.Message}", "OK");
                Debug.LogError($"❌ Version System test failed: {ex.Message}");
            }
        }

        [MenuItem("Tools/Avatar Smart Backup/Debug Tests/Show System Info", false, 902)]
        public static void ShowSystemInfo()
        {
            var settings = BackupManager.LoadSettings();
            if (!settings.debugMode)
            {
                EditorUtility.DisplayDialog("Debug Mode Required", 
                    "Debug tests are only available when Debug Mode is enabled in Advanced Settings.", "OK");
                return;
            }

            var info = new System.Text.StringBuilder();
            info.AppendLine($"Unity Version: {Application.unityVersion}");
            info.AppendLine($"Platform: {Application.platform}");
            info.AppendLine($"Scripting Runtime: {System.Environment.Version}");
            info.AppendLine($"SQLite-net Available: {IsSQLiteNetAvailable()}");
            info.AppendLine($"Backup Root: {FileUtilEx.BackupRoot}");
            
            EditorUtility.DisplayDialog("System Information", info.ToString(), "OK");
            Debug.Log($"System Info:\n{info}");
        }

        private static bool IsSQLiteNetAvailable()
        {
            try
            {
                using var conn = new SQLite.SQLiteConnection(":memory:");
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Validate menu items only show in debug mode
        [MenuItem("Tools/Avatar Smart Backup/Debug Tests/Test SQLite Connection", true)]
        [MenuItem("Tools/Avatar Smart Backup/Debug Tests/Test Version System", true)]
        [MenuItem("Tools/Avatar Smart Backup/Debug Tests/Show System Info", true)]
        public static bool ValidateDebugMenus()
        {
            var settings = BackupManager.LoadSettings();
            return settings?.debugMode == true;
        }
    }
}
#endif