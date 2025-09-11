#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using AvatarSmartBackup.Versioning;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Simple restore functionality for version history
    /// Designed to be safe and user-friendly for VRChat creators
    /// </summary>
    public static class SimpleRestore
    {
        /// <summary>
        /// Restore project to a specific version
        /// Creates a safety backup first, then replaces current files
        /// </summary>
        public static async Task<bool> RestoreToVersionAsync(BackupVersion version, bool showProgress = true)
        {
            try
            {
                if (BackupManager.IsBusy)
                {
                    EditorUtility.DisplayDialog("Backup In Progress", 
                        "Cannot restore while backup is running. Please wait.", "OK");
                    return false;
                }

                var sourcePath = Path.Combine(FileUtilEx.BackupRoot, version.BackupPath);
                if (!Directory.Exists(sourcePath))
                {
                    EditorUtility.DisplayDialog("Version Not Found", 
                        $"The backup files for version #{version.Id} could not be found.\n\n" +
                        $"Expected location: {sourcePath}", "OK");
                    return false;
                }

                // Step 1: Create safety backup of current state
                if (showProgress)
                {
                    EditorUtility.DisplayProgressBar("Restoring Version", "Creating safety backup...", 0.1f);
                }

                var settings = BackupManager.LoadSettings();
                await BackupManager.RunBackupNowAsync(settings, false, "pre-restore", false, false);

                // Step 2: Restore files
                if (showProgress)
                {
                    EditorUtility.DisplayProgressBar("Restoring Version", "Restoring files...", 0.5f);
                }

                await RestoreFilesAsync(sourcePath, showProgress);

                // Step 3: Refresh Unity
                if (showProgress)
                {
                    EditorUtility.DisplayProgressBar("Restoring Version", "Refreshing Unity...", 0.9f);
                }

                AssetDatabase.Refresh();

                if (showProgress)
                {
                    EditorUtility.ClearProgressBar();
                }

                EditorUtility.DisplayDialog("Restore Completed", 
                    $"Successfully restored to version #{version.Id}\n\n" +
                    $"{version.Description}\n" +
                    $"{version.GetDateTime().ToLocalTime():yyyy-MM-dd HH:mm}\n\n" +
                    "A safety backup of your previous state was created.", "OK");

                return true;
            }
            catch (Exception ex)
            {
                if (showProgress)
                {
                    EditorUtility.ClearProgressBar();
                }

                EditorUtility.DisplayDialog("Restore Failed", 
                    $"Failed to restore version #{version.Id}:\n\n{ex.Message}", "OK");
                
                Debug.LogError($"Restore failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Restore files from backup to Assets folder
        /// </summary>
        static async Task RestoreFilesAsync(string sourcePath, bool showProgress)
        {
            await Task.Run(() =>
            {
                var assetsPath = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
                
                // Get all files to restore
                var sourceFiles = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
                int totalFiles = sourceFiles.Length;
                int processedFiles = 0;

                foreach (var sourceFile in sourceFiles)
                {
                    try
                    {
                        // Skip manifest and backup control files
                        var fileName = Path.GetFileName(sourceFile);
                        if (fileName == "manifest.json" || fileName == "backup.ok")
                        {
                            processedFiles++;
                            continue;
                        }

                        // Calculate relative path and target
                        var relativePath = Path.GetRelativePath(sourcePath, sourceFile);
                        var targetFile = Path.Combine(assetsPath, relativePath);

                        // Ensure target directory exists
                        var targetDir = Path.GetDirectoryName(targetFile);
                        if (!Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        // Copy file
                        File.Copy(sourceFile, targetFile, overwrite: true);
                        
                        processedFiles++;

                        // Update progress periodically
                        if (showProgress && processedFiles % 10 == 0)
                        {
                            float progress = 0.5f + (processedFiles / (float)totalFiles) * 0.4f; // 50% to 90%
                            MainThread.Invoke(() =>
                            {
                                EditorUtility.DisplayProgressBar("Restoring Version", 
                                    $"Restoring files... {processedFiles}/{totalFiles}", progress);
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to restore file {sourceFile}: {ex.Message}");
                    }
                }

                Debug.Log($"Restore completed: {processedFiles}/{totalFiles} files processed");
            });
        }

        /// <summary>
        /// Preview files in a version without restoring
        /// </summary>
        public static void PreviewVersion(BackupVersion version)
        {
            try
            {
                var sourcePath = Path.Combine(FileUtilEx.BackupRoot, version.BackupPath);
                if (!Directory.Exists(sourcePath))
                {
                    EditorUtility.DisplayDialog("Version Not Found", 
                        $"The backup files for version #{version.Id} could not be found.", "OK");
                    return;
                }

                // Get file list
                var files = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
                var fileList = new System.Text.StringBuilder();
                
                fileList.AppendLine($"Version #{version.Id} - {version.Description}");
                fileList.AppendLine($"Date: {version.GetDateTime().ToLocalTime():yyyy-MM-dd HH:mm}");
                fileList.AppendLine($"Files: {version.FileCount} ({version.GetDisplaySize()})");
                fileList.AppendLine();
                fileList.AppendLine("Files in this version:");
                
                int count = 0;
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(sourcePath, file);
                    if (relativePath != "manifest.json" && relativePath != "backup.ok")
                    {
                        fileList.AppendLine($"• {relativePath}");
                        count++;
                        
                        // Limit display to avoid huge dialogs
                        if (count >= 50)
                        {
                            fileList.AppendLine($"... and {files.Length - count - 2} more files");
                            break;
                        }
                    }
                }

                EditorUtility.DisplayDialog("Version Preview", fileList.ToString(), "OK");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Preview Failed", 
                    $"Failed to preview version #{version.Id}:\n\n{ex.Message}", "OK");
            }
        }
    }
}
#endif