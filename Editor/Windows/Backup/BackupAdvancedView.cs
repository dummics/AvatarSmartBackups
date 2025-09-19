#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup.Backup
{
    internal class BackupAdvancedView
    {
        readonly BackupWindowContext _context;
        readonly BackupSharedView _shared;

        public BackupAdvancedView(BackupWindowContext context, BackupSharedView shared)
        {
            _context = context;
            _shared = shared;
        }

        public void Draw()
        {
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("window.main.header", "Avatar Smart Backup"), EditorStyles.boldLabel);
            using (_context.Info(AvatarSmartBackup.Localization.L.T("window.main.autobackup.title", "Automatic Safety Copies"), AvatarSmartBackup.Localization.L.T("window.main.autobackup.help", "Automatic safety copies run in the background. Use 'Backup Now' to force one."), MessageType.Info))
            {
                GUILayout.Space(2);
            }

            _shared.DrawModeSelector();
            _context.Settings.AdvancedMode = !_context.Settings.easyMode;

            _shared.DrawSchedulerSection();
            _shared.DrawPrimaryActions();
            _shared.DrawVersionsOverview();
            EditorGUILayout.Space();
            _shared.DrawAdvancedOverview();
            _shared.DrawAdvancedSettings();
        }
    }
}
#endif
