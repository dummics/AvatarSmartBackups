#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Essential menu items for Avatar Smart Backup
    /// Keeps the UI clean and focused on core functionality
    /// </summary>
    public static class BackupMenuItems
    {
        // Main window - primary entry point
        [MenuItem("Tools/Avatar Smart Backup", false, 0)]
        public static void OpenMainWindow()
        {
            AvatarSmartBackupWindow.Open();
        }

        // Quick access to storage folder
        [MenuItem("Tools/Avatar Smart Backup/Open Storage Folder", false, 100)]
        public static void OpenStorageFolder()
        {
            if (System.IO.Directory.Exists(FileUtilEx.BackupRoot))
            {
                EditorUtility.RevealInFinder(FileUtilEx.BackupRoot);
            }
            else
            {
                EditorUtility.DisplayDialog("Storage Folder", 
                    "Storage folder not found. Run a backup first to create the folder structure.", "OK");
            }
        }

        // Documentation/Help
        [MenuItem("Tools/Avatar Smart Backup/Help", false, 200)]
        public static void ShowHelp()
        {
            EditorUtility.DisplayDialog("Avatar Smart Backup Help", 
                "Avatar Smart Backup - Automatic Safety System\n\n" +
                "• Automatic backups run in the background\n" +
                "• Version history lets you restore previous states\n" +
                "• Designed for VRChat creators and Unity artists\n\n" +
                "Main Window: Configure settings and view backup status\n" +
                "Storage Folder: Access your backup files directly\n\n" +
                "Everything runs automatically - just keep creating!", "OK");
        }

        // Note: Debug Tests menu items are defined in SystemDiagnostics.cs
        // and only appear when debugMode is enabled in settings
    }
}
#endif