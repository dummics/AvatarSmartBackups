#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Linq;
using System;

namespace AvatarSmartBackup.Debugging
{
    public static class AssemblyInspector
    {
   // [MenuItem("Tools/Avatar Smart Backup Debug/Tests/Test File-Based Version Manager")] 
        public static void TestFileBasedVersionManager()
        {
            try
            {
                using var vm = new FileBasedVersionManager();
                
                // Test creating a version
                string currentDir = System.IO.Path.Combine(FileUtilEx.BackupRoot, "Current");
                bool created = vm.CreateVersion("Test Version", currentDir, settings: null, forceCheckpoint: true);
                Debug.Log($"Version creation result: {created}");
                
                // Test getting versions
                var versions = vm.GetVersions();
                Debug.Log($"Total versions: {versions.Count}");
                foreach (var v in versions)
                {
                    Debug.Log($"Version {v.id}: {v.description} at {v.timestamp}");
                }
                
                // Test cleanup
                vm.CleanupOldVersions(5);
                
                Debug.Log("File-Based Version Manager test completed!");
            }
            catch (Exception ex)
            {
                Debug.LogError($"File-Based Version Manager test failed: {ex.Message}");
            }
        }
    }
}
#endif
