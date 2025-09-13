#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Linq;

namespace AvatarSmartBackup.Debugging
{
    public static class AssemblyInspector
    {
        [MenuItem("Tools/Avatar Smart Backup/Debug Tests/Test JSON Version Manager")] 
        public static void TestJsonVersionManager()
        {
            try
            {
                using var vm = new AvatarSmartBackup.Versioning.JsonVersionManager();
                
                // Test recording a version
                int id = vm.RecordBackupAsVersion("Test Version", "C:\\Test\\Path", 42, 1024 * 1024);
                Debug.Log($"Recorded version with ID: {id}");
                
                // Test getting versions
                var versions = vm.GetVersions();
                Debug.Log($"Total versions: {versions.Count}");
                
                // Test stats
                var stats = vm.GetStats();
                Debug.Log($"Stats - Total: {stats.TotalVersions}, Size: {stats.TotalSizeBytes} bytes");
                
                Debug.Log("JSON Version Manager test completed successfully!");
            }
            catch (Exception ex)
            {
                Debug.LogError($"JSON Version Manager test failed: {ex.Message}");
            }
        }
    }
}
#endif