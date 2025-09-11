#if UNITY_EDITOR
using System.Data.SQLite;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Test for .NET Standard System.Data.SQLite
    /// </summary>
    public static class NetStandardSQLiteTest
    {
        [MenuItem("Tools/Avatar Smart Backup/System Tests/Test .NET Standard SQLite", false, 190)]
        public static void TestNetStandardSQLite()
        {
            try
            {
                Debug.Log("Testing .NET Standard System.Data.SQLite...");
                
                // Test in-memory database
                using var connection = new SQLiteConnection("Data Source=:memory:;Version=3;");
                connection.Open();
                Debug.Log("✅ SQLite connection opened successfully");
                
                // Test basic operations
                using var command = new SQLiteCommand("CREATE TABLE test (id INTEGER, name TEXT)", connection);
                command.ExecuteNonQuery();
                Debug.Log("✅ Table created successfully");
                
                using var insertCmd = new SQLiteCommand("INSERT INTO test VALUES (1, 'Hello .NET Standard SQLite')", connection);
                insertCmd.ExecuteNonQuery();
                Debug.Log("✅ Data inserted successfully");
                
                using var selectCmd = new SQLiteCommand("SELECT name FROM test WHERE id = 1", connection);
                var result = selectCmd.ExecuteScalar() as string;
                Debug.Log($"✅ Data retrieved: {result}");
                
                if (result == "Hello .NET Standard SQLite")
                {
                    EditorUtility.DisplayDialog("SQLite Test Success", 
                        "✅ .NET Standard System.Data.SQLite is working perfectly!\n\n" +
                        "The version system can now use the SQLite database.\n\n" +
                        "Next: Try the Version Database test.", "OK");
                    Debug.Log("✅ .NET Standard SQLite test passed completely!");
                }
                else
                {
                    throw new System.Exception($"Query returned unexpected result: {result}");
                }
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("SQLite Test Failed", 
                    $"❌ .NET Standard SQLite test failed:\n\n{ex.Message}\n\n" +
                    "Check Console for detailed error information.", "OK");
                Debug.LogError($"❌ .NET Standard SQLite test failed: {ex.Message}");
                Debug.LogError($"Stack trace: {ex.StackTrace}");
            }
        }
    }
}
#endif