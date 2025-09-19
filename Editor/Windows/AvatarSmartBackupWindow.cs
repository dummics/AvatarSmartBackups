#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using AvatarSmartBackup.Backup;
using AvatarSmartBackup.Config;

namespace AvatarSmartBackup
{
    public class AvatarSmartBackupWindow : EditorWindow
    {
        BackupWindowContext? _context;
        BackupSharedView? _sharedView;
        BackupVersionsView? _versionsView;

        static bool _forceBackupTabOnOpen;

        [MenuItem("Avatar Smart Backup/Open", false, 0)]
        public static void Open()
        {
            _forceBackupTabOnOpen = true;
            var w = GetWindow<AvatarSmartBackupWindow>(true, AvatarSmartBackup.Localization.L.T("window.main.title", "Avatar Smart Backup"));
            w.minSize = new Vector2(320, 320);
            w.Show();
        }

        [MenuItem("Avatar Smart Backup/Restart Onboarding", false, 20)]
        public static void RestartOnboarding()
        {
            var settings = BackupManager.LoadSettings();
            settings.onboardingCompleted = false;
            BackupManager.SaveSettings(settings);
            TimerService.InvalidateSettingsCache();
            Open();
        }

        [MenuItem("Avatar Smart Backup/About", false, 100)]
        public static void ShowAbout()
        {
            var packageName = "Avatar Smart Backup";
            var version = "0.2.0";
            var description = "Incremental backup for VRChat projects.";
            var author = "Dummics";
            var packageId = "dum.incb.system";

            var message = $"{packageName}\n\n" +
                         $"Version: {version}\n" +
                         $"Package ID: {packageId}\n" +
                         $"Author: {author}\n\n" +
                         $"{description}";

            UnityEditor.EditorUtility.DisplayDialog("About Avatar Smart Backup", message, "OK");
        }

        void OnEnable()
        {
            _context = new BackupWindowContext(this);
            _context.HandleOnboarding(_forceBackupTabOnOpen);
            if (_forceBackupTabOnOpen)
                _forceBackupTabOnOpen = false;
        }

        void OnDisable()
        {
            _context?.Dispose();
            _context = null;
            _sharedView = null;
            _versionsView = null;
        }

        BackupWindowContext Context
        {
            get
            {
                if (_context == null)
                {
                    _context = new BackupWindowContext(this);
                }
                return _context;
            }
        }

        void EnsureViews()
        {
            var context = Context;
            _sharedView ??= new BackupSharedView(context);
            _versionsView ??= new BackupVersionsView(context);
        }

        void DrawBackupTab()
        {
            if (_sharedView == null)
                return;

            var currentMode = Context.Settings.easyMode ? BackupLayoutSchema.LayoutMode.Easy : BackupLayoutSchema.LayoutMode.Advanced;
            foreach (var block in BackupLayoutSchema.MainWindowBlocks)
            {
                if (!block.Supports(currentMode))
                    continue;

                switch (block.Id)
                {
                    case BackupLayoutSchema.MainWindowBlock.Header:
                        _sharedView.DrawHeader();
                        break;
                    case BackupLayoutSchema.MainWindowBlock.AutomaticInfo:
                        _sharedView.DrawAutomaticBackupInfo(currentMode);
                        break;
                    case BackupLayoutSchema.MainWindowBlock.ModeSelector:
                        _sharedView.DrawModeSelector();
                        break;
                    case BackupLayoutSchema.MainWindowBlock.ModeSync:
                        _sharedView.SyncModeFlags(currentMode);
                        break;
                    case BackupLayoutSchema.MainWindowBlock.EasyDashboard:
                        _sharedView.DrawEasyDashboard();
                        break;
                    case BackupLayoutSchema.MainWindowBlock.Scheduler:
                        _sharedView.DrawSchedulerSection();
                        break;
                    case BackupLayoutSchema.MainWindowBlock.PrimaryActions:
                        _sharedView.DrawPrimaryActions();
                        break;
                    case BackupLayoutSchema.MainWindowBlock.VersionsOverview:
                        _sharedView.DrawVersionsOverview();
                        break;
                    case BackupLayoutSchema.MainWindowBlock.BodySpacing:
                        _sharedView.DrawBodySpacing();
                        break;
                    case BackupLayoutSchema.MainWindowBlock.AdvancedOverview:
                        _sharedView.DrawAdvancedOverview();
                        break;
                    case BackupLayoutSchema.MainWindowBlock.AdvancedSettings:
                        _sharedView.DrawAdvancedSettings();
                        break;
                }
            }
        }

        void OnGUI()
        {
            EnsureViews();
            var context = Context;

            try
            {
                EditorGUI.BeginChangeCheck();
                string[] tabs = { AvatarSmartBackup.Localization.L.T("ui.tabs.backup", "Backup"), AvatarSmartBackup.Localization.L.T("ui.tabs.versions", "Versions") };
                if (!context.Settings._uiTabInitialized)
                {
                    context.Settings._activeTab = 0;
                    context.Settings._uiTabInitialized = true;
                }
                context.Settings._activeTab = GUILayout.Toolbar(context.Settings._activeTab, tabs);
                EditorGUILayout.Space(4);

                context.Scroll = EditorGUILayout.BeginScrollView(context.Scroll);
                if (context.Settings._activeTab == 0)
                {
                    DrawBackupTab();
                }
                else
                {
                    _versionsView!.Draw();
                }
                EditorGUILayout.EndScrollView();

                bool changed = EditorGUI.EndChangeCheck();
                context.SaveSettingsIfChanged(changed);
            }
            catch (Exception ex)
            {
                try { EditorGUILayout.EndScrollView(); } catch { }
                GUILayout.Label("UI error: " + ex.Message, EditorStyles.helpBox);
                Repaint();
            }
        }
    }
}
#endif
