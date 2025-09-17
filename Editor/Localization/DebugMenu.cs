#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup.Localization
{
    public static class DebugMenu
    {
        const string Root = "Avatar Smart Backup/Debug/";

        [MenuItem(Root + "Open Backup Folder", priority = 200)]
        public static void OpenBackupFolder()
        {
            EditorUtility.RevealInFinder(FileUtilEx.BackupRoot);
        }

        [MenuItem(Root + "Open Log Folder", priority = 201)]
        public static void OpenLogFolder()
        {
            string logDir = Log.GetLogDirectory();
            if (Directory.Exists(logDir)) EditorUtility.RevealInFinder(logDir);
            else EditorUtility.DisplayDialog(L.T("dlg.log.title", "Log Folder"), L.T("dlg.log.notfound", "Log folder not found."), "OK");
        }

        [MenuItem(Root + "Toggle Scheduler", priority = 202)]
        public static void ToggleScheduler()
        {
            if (Session.IsRunning) TimerService.PauseTimer();
            else TimerService.StartTimerIfNeeded(BackupManager.LoadSettings());
        }
    }
}
#endif
