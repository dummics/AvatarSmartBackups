#if UNITY_EDITOR
using System.Data.SQLite;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Quick test to verify System.Data.SQLite is working
    /// </summary>
    public static class QuickSQLiteTest
    {
        [MenuItem("Tools/Avatar Smart Backup/Quick Tests/⚡ Quick SQLite Test", false, 150)]
        public static void QuickTest()
        {
            try
            {
                // Quick in-memory test
                using var connection = new SQLiteConnection("Data Source=:memory:;Version=3;");
                connection.Open();
                
                using var command = new SQLiteCommand("SELECT sqlite_version()", connection);
                var version = command.ExecuteScalar() as string;
                
                Debug.Log($"✅ System.Data.SQLite is working! SQLite version: {version}");
                EditorUtility.DisplayDialog("SQLite Test", 
                    $"✅ System.Data.SQLite is working!\n\nSQLite version: {version}\n\nThe Version System 2.0 database is ready!", "Great!");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"❌ SQLite test failed: {ex.Message}");
                EditorUtility.DisplayDialog("SQLite Test Failed", 
                    $"❌ System.Data.SQLite test failed:\n\n{ex.Message}\n\nPlease check the DLL configuration.", "OK");
            }
        }
        
        [MenuItem("Tools/Avatar Smart Backup/Quick Tests/🚀 Test Version System Ready", false, 151)]
        public static void TestVersionSystemReady()
        {
            try
            {
                // Test the full Version System initialization
                using var versionManager = new AvatarSmartBackup.Versioning.VersionManager();
                var stats = versionManager.GetStats();
                
                Debug.Log($"✅ Version System 2.0 ready! Storage: {stats.StorageDirectory}");
                EditorUtility.DisplayDialog("Version System Ready", 
                    $"✅ Version System 2.0 is fully ready!\n\n" +
                    $"• Storage: {stats.StorageDirectory}\n" +
                    $"• Database: System.Data.SQLite\n" +
                    $"• Commits: {stats.TotalCommits}\n\n" +
                    $"You can now use the Version Timeline!", "Awesome!");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"❌ Version System test failed: {ex.Message}");
                EditorUtility.DisplayDialog("Version System Test Failed", 
                    $"❌ Version System test failed:\n\n{ex.Message}", "OK");
            }
        }
    }
}
#endif
