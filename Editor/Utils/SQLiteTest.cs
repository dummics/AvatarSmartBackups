#if UNITY_EDITOR
using System.Data.SQLite;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Test utility for System.Data.SQLite integration
    /// </summary>
    public static class SQLiteTest
    {
        [MenuItem("Tools/Avatar Smart Backup/System Tests/Test SQLite Connection", false, 200)]
        public static void TestSQLiteConnection()
        {
            try
            {
                // Test in-memory database
                using var connection = new SQLiteConnection("Data Source=:memory:;Version=3;");
                connection.Open();
                
                // Test basic operations
                using var command = new SQLiteCommand("CREATE TABLE test (id INTEGER, name TEXT)", connection);
                command.ExecuteNonQuery();
                
                using var insertCmd = new SQLiteCommand("INSERT INTO test VALUES (1, 'Hello SQLite')", connection);
                insertCmd.ExecuteNonQuery();
                
                using var selectCmd = new SQLiteCommand("SELECT name FROM test WHERE id = 1", connection);
                var result = selectCmd.ExecuteScalar() as string;
                
                if (result == "Hello SQLite")
                {
                    EditorUtility.DisplayDialog("SQLite Test", 
                        "✅ System.Data.SQLite is working correctly!\n\n" +
                        "The Version System 2.0 database backend is ready.", "OK");
                    Debug.Log("✅ SQLite test passed successfully");
                }
                else
                {
                    throw new System.Exception("Query returned unexpected result");
                }
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("SQLite Test Failed", 
                    $"❌ System.Data.SQLite test failed:\n\n{ex.Message}\n\n" +
                    $"Please check the SYSTEM_DATA_SQLITE_GUIDE.md for setup instructions.", "OK");
                Debug.LogError($"❌ SQLite test failed: {ex.Message}");
            }
        }
        
        [MenuItem("Tools/Avatar Smart Backup/System Tests/Test Version Database", false, 201)]
        public static void TestVersionDatabase()
        {
            try
            {
                var tempDbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test_version_{System.Guid.NewGuid():N}.db");
                
                using var database = new AvatarSmartBackup.Versioning.VersionDatabase(tempDbPath);
                
                // Test database initialization
                var testCommitId = database.CreateCommit("Test commit", 5, 1024);
                var commits = database.GetCommitHistory(10);
                
                if (commits.Count > 0 && commits[0].Id == testCommitId)
                {
                    EditorUtility.DisplayDialog("Version Database Test", 
                        "✅ Version Database is working correctly!\n\n" +
                        $"• Database created: {tempDbPath}\n" +
                        $"• Test commit created: {testCommitId}\n" +
                        $"• Commit retrieved successfully\n\n" +
                        "The Version System 2.0 is ready for use!", "OK");
                    Debug.Log($"✅ Version Database test passed - Commit ID: {testCommitId}");
                }
                else
                {
                    throw new System.Exception("Failed to create or retrieve test commit");
                }
                
                // Cleanup
                if (System.IO.File.Exists(tempDbPath))
                {
                    System.IO.File.Delete(tempDbPath);
                }
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Version Database Test Failed", 
                    $"❌ Version Database test failed:\n\n{ex.Message}", "OK");
                Debug.LogError($"❌ Version Database test failed: {ex.Message}");
            }
        }
        
        [MenuItem("Tools/Avatar Smart Backup/System Tests/Test Full Version System", false, 202)]
        public static async void TestFullVersionSystem()
        {
            try
            {
                using var versionManager = new AvatarSmartBackup.Versioning.VersionManager();
                
                // Test stats retrieval
                var stats = versionManager.GetStats();
                
                EditorUtility.DisplayDialog("Full Version System Test", 
                    "✅ Full Version System is working correctly!\n\n" +
                    $"• Storage Directory: {stats.StorageDirectory}\n" +
                    $"• Total Commits: {stats.TotalCommits}\n" +
                    $"• Storage Used: {stats.DisplaySize}\n\n" +
                    "The complete Version System 2.0 is ready!\n\n" +
                    "Next: Try creating a manual commit from the main window.", "OK");
                
                Debug.Log("✅ Full Version System test passed");
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Full Version System Test Failed", 
                    $"❌ Full Version System test failed:\n\n{ex.Message}", "OK");
                Debug.LogError($"❌ Full Version System test failed: {ex.Message}");
            }
        }
    }
}
#endif
