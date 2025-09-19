#if UNITY_EDITOR
using System;
using UnityEditor;
using AvatarSmartBackup.Backup;

namespace AvatarSmartBackup.Shared
{
    internal sealed class VersionsCard
    {
        readonly BackupWindowContext _context;

        public VersionsCard(BackupWindowContext context)
        {
            _context = context;
        }

        public void Draw()
        {
            _context.RecordLayoutMarker("VersionsOverview");
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.overview.latest.title", "Latest Version"), EditorStyles.boldLabel);
            _context.EnsureVersionsCache();
            var latest = _context.GetLatestVersionCached();
            if (latest != null)
            {
                EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.overview.latest.label", "Latest Version:"), EditorStyles.miniBoldLabel);
                string desc = _context.SanitizeInlineLabel(latest.description, AvatarSmartBackup.Localization.L.T("ui.overview.noDescription", "(no description)"));
                EditorGUILayout.LabelField($"# {latest.id}  {desc}", EditorStyles.miniLabel);
                var created = _context.ParseCreatedUtc(latest);
                if (created != DateTime.MinValue)
                    EditorGUILayout.LabelField($"{AvatarSmartBackup.Localization.L.T("ui.overview.created", "Created:")} {created:yyyy-MM-dd HH:mm:ss}", EditorStyles.miniLabel);
                {
                    var filesLbl = AvatarSmartBackup.Localization.L.T("ui.overview.filesSize", "Files");
                    var sizeLbl = AvatarSmartBackup.Localization.L.T("ui.overview.size", "Size");
                    EditorGUILayout.LabelField($"{filesLbl}: {latest.fileCount}  {sizeLbl}: {_context.FormatSize(latest.totalSizeBytes)}", EditorStyles.miniLabel);
                }
                if (GUILayout.Button(new GUIContent(AvatarSmartBackup.Localization.L.T("ui.overview.gotoVersions", "Go to Versions"), AvatarSmartBackup.Localization.L.T("tt.overview.gotoVersions", "Open Versions tab")), GUILayout.Width(140)))
                {
                    _context.Settings._activeTab = 1;
                }
            }
            else
            {
                EditorGUILayout.LabelField(AvatarSmartBackup.Localization.L.T("ui.overview.none", "No versions available"), EditorStyles.miniLabel);
            }
            EditorGUILayout.EndVertical();
        }
    }
}
#endif
