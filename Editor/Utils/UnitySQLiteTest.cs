#if UNITY_EDITOR
using Mono.Data.Sqlite;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Test utility for Unity's built-in SQLite (Mono.Data.Sqlite)
    /// </summary>
    public static class UnitySQLiteTest
    {
        [MenuItem("Tools/Avatar Smart Backup/System Tests/Test Unity SQLite", false, 195)]
        public static void TestUnitySQLite()
        {
            try
            {
                // Test in-memory database with Unity's SQLite
                using var connection = new SqliteConnection("URI=file::memory:");
                connection.Open();
                
                // Test basic operations
                using var command = new SqliteCommand("CREATE TABLE test (id INTEGER, name TEXT)", connection);
                command.ExecuteNonQuery();
                
                using var insertCmd = new SqliteCommand("INSERT INTO test VALUES (1, 'Hello Unity SQLite')", connection);
                insertCmd.ExecuteNonQuery();
                
                using var selectCmd = new SqliteCommand("SELECT name FROM test WHERE id = 1", connection);
                var result = selectCmd.ExecuteScalar() as string;
                
                if (result == "Hello Unity SQLite")
                {
                    EditorUtility.DisplayDialog("Unity SQLite Test", 
                        "✅ Unity's built-in SQLite (Mono.Data.Sqlite) is working perfectly!\n\n" +
                        "This is much more compatible than System.Data.SQLite.\n" +
                        "We can migrate the Version System to use this instead.", "OK");
                    Debug.Log("✅ Unity SQLite test passed successfully");
                }
                else
                {
                    throw new System.Exception("Query returned unexpected result");
                }
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Unity SQLite Test Failed", 
                    $"❌ Unity SQLite test failed:\n\n{ex.Message}", "OK");
                Debug.LogError($"❌ Unity SQLite test failed: {ex.Message}");
            }
        }
    }
}
#endif