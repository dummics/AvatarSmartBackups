#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Diagnostic utility for SQLite issues
    /// </summary>
    public static class SQLiteDiagnostics
    {
        [MenuItem("Tools/Avatar Smart Backup/System Tests/SQLite Diagnostics", false, 190)]
        public static void RunSQLiteDiagnostics()
        {
            try
            {
                Debug.Log("=== SQLite Diagnostics ===");
                
                // Test 1: Check if System.Data.SQLite assembly loads
                Debug.Log("Test 1: Loading System.Data.SQLite assembly...");
                var sqliteAssembly = System.Reflection.Assembly.LoadFrom(
                    System.IO.Path.Combine(UnityEngine.Application.dataPath, 
                    "AvatarSmartBackups/Plugins/SQLite/System.Data.SQLite.dll"));
                Debug.Log($"✅ Assembly loaded: {sqliteAssembly.FullName}");
                
                // Test 2: Check SQLiteConnection type
                Debug.Log("Test 2: Getting SQLiteConnection type...");
                var connectionType = sqliteAssembly.GetType("System.Data.SQLite.SQLiteConnection");
                Debug.Log($"✅ SQLiteConnection type found: {connectionType?.FullName}");
                
                // Test 3: Try to create instance
                Debug.Log("Test 3: Creating SQLiteConnection instance...");
                var connectionString = "Data Source=:memory:;Version=3;";
                var connection = Activator.CreateInstance(connectionType, connectionString);
                Debug.Log($"✅ SQLiteConnection instance created");
                
                // Test 4: Try using directive approach
                Debug.Log("Test 4: Testing using directive approach...");
                using var sqlConnection = new System.Data.SQLite.SQLiteConnection("Data Source=:memory:;Version=3;");
                Debug.Log($"✅ Using directive works");
                
                EditorUtility.DisplayDialog("SQLite Diagnostics", 
                    "✅ All SQLite diagnostic tests passed!\n\n" +
                    "System.Data.SQLite should be working correctly.\n" +
                    "Check Console for detailed logs.", "OK");
                    
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ SQLite Diagnostic failed at: {ex.Message}");
                Debug.LogError($"Stack trace: {ex.StackTrace}");
                
                // Check if it's a native library issue
                if (ex.Message.Contains("e_sqlite3") || ex.Message.Contains("sqlite3.dll"))
                {
                    EditorUtility.DisplayDialog("SQLite Diagnostics - Native Library Issue", 
                        $"❌ SQLite native library issue detected:\n\n{ex.Message}\n\n" +
                        "This is a Unity/Windows compatibility issue.\n" +
                        "Recommendation: Use Unity's built-in SQLite instead.", "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("SQLite Diagnostics Failed", 
                        $"❌ SQLite diagnostic test failed:\n\n{ex.Message}", "OK");
                }
            }
        }
        
        [MenuItem("Tools/Avatar Smart Backup/System Tests/Check Unity SQLite", false, 191)]
        public static void CheckUnitySQLite()
        {
            try
            {
                Debug.Log("=== Unity Built-in SQLite Check ===");
                
                // Unity should have Mono.Data.Sqlite available
                var unityDataAssembly = System.Reflection.Assembly.Load("Mono.Data.Sqlite");
                Debug.Log($"✅ Unity Mono.Data.Sqlite found: {unityDataAssembly.FullName}");
                
                var connectionType = unityDataAssembly.GetType("Mono.Data.Sqlite.SqliteConnection");
                Debug.Log($"✅ Unity SqliteConnection type: {connectionType?.FullName}");
                
                EditorUtility.DisplayDialog("Unity SQLite Check", 
                    "✅ Unity's built-in SQLite (Mono.Data.Sqlite) is available!\n\n" +
                    "This is a more compatible option for Unity projects.", "OK");
                    
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ Unity SQLite check failed: {ex.Message}");
                EditorUtility.DisplayDialog("Unity SQLite Check Failed", 
                    $"❌ Unity built-in SQLite not available:\n\n{ex.Message}", "OK");
            }
        }
    }
}
#endif