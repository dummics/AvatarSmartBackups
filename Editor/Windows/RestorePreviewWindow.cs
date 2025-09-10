#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    public class RestorePreviewWindow : EditorWindow
    {
        Vector2 _scroll;
        List<string> _files = new List<string>();
        List<bool> _selected = new List<bool>();
        bool _selectAll = true;
        bool _backupBefore = true;
        bool _hideMeta = true;
        Dictionary<string,int> _extCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string,int> _assetTypeCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
    
        public static void Open()
        {
            var w = GetWindow<RestorePreviewWindow>(true, "Restore Preview", true);
            w.minSize = new Vector2(480, 320);
            w.LoadFiles();
            w.Show();
        }
    
        void LoadFiles()
        {
            _files.Clear(); _selected.Clear();
            _extCounts.Clear(); _assetTypeCounts.Clear();
            string srcRoot = Path.Combine(FileUtilEx.BackupRoot, "Current");
            if (!Directory.Exists(srcRoot)) return;
            var ok = Path.Combine(srcRoot, "backup.ok");
            if (!File.Exists(ok)) { EditorUtility.DisplayDialog("Restore", "Backup in progress or not complete.", "OK"); return; }
            foreach (var src in Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories))
            {
                string rel = MakeRelTo(src, srcRoot).Replace("\\", "/");
                if (!rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                if (rel.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || rel.Equals("backup.ok", StringComparison.OrdinalIgnoreCase)) continue;
                _files.Add(rel);
                _selected.Add(true);
    
                // estensione count
                string ext = Path.GetExtension(rel);
                if (string.IsNullOrEmpty(ext)) ext = "(no ext)";
                if (!_extCounts.ContainsKey(ext)) _extCounts[ext] = 0;
                _extCounts[ext]++;
    
                // per .asset proviamo a riconoscere tipi comuni VRC
                if (ext.Equals(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // leggi una porzione iniziale (file di testo YAML) per cercare indizi
                        using var sr = new StreamReader(src, Encoding.UTF8);
                        char[] buffer = new char[32 * 1024];
                        int read = sr.Read(buffer, 0, buffer.Length);
                        string sample = new string(buffer, 0, Math.Max(0, read));
                        string typeLabel = "Unknown .asset";
                        if (sample.IndexOf("VRCExpressionsMenu", StringComparison.OrdinalIgnoreCase) >= 0) typeLabel = "VRCExpressionsMenu";
                        else if (sample.IndexOf("VRCExpressionParameters", StringComparison.OrdinalIgnoreCase) >= 0) typeLabel = "VRCExpressionParameters";
                        else if (sample.IndexOf("VRCExpression", StringComparison.OrdinalIgnoreCase) >= 0) typeLabel = "VRCExpression (other)";
                        else if (sample.IndexOf("m_Script", StringComparison.OrdinalIgnoreCase) >= 0 && sample.IndexOf("MonoBehaviour", StringComparison.OrdinalIgnoreCase) >= 0) typeLabel = "MonoBehaviour.asset";
                        if (!_assetTypeCounts.ContainsKey(typeLabel)) _assetTypeCounts[typeLabel] = 0;
                        _assetTypeCounts[typeLabel]++;
                    }
                    catch { if (!_assetTypeCounts.ContainsKey("Unknown .asset")) _assetTypeCounts["Unknown .asset"] = 0; _assetTypeCounts["Unknown .asset"]++; }
                }
            }
        }
    
        void OnGUI()
        {
            EditorGUILayout.LabelField("Restore Preview", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Review files to restore. Uncheck items to keep existing project files. You can create a pre-restore backup of current Assets/.", MessageType.Info);
    
            // Summary
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Files in backup", EditorStyles.boldLabel);
            if (_extCounts.Count == 0) EditorGUILayout.LabelField("No files found.");
            else
            {
                EditorGUILayout.BeginHorizontal();
                // By extension
                EditorGUILayout.BeginVertical(GUILayout.MaxWidth(220));
                EditorGUILayout.LabelField("By extension", EditorStyles.miniBoldLabel);
                foreach (var kv in _extCounts.OrderByDescending(k => k.Value))
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(kv.Key.PadRight(8), GUILayout.Width(80));
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField(kv.Value.ToString(), GUILayout.Width(40));
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndVertical();
    
                // .asset types
                EditorGUILayout.BeginVertical();
                if (_assetTypeCounts.Count > 0)
                {
                    EditorGUILayout.LabelField(".asset types", EditorStyles.miniBoldLabel);
                    foreach (var kv in _assetTypeCounts.OrderByDescending(k => k.Value))
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(kv.Key, GUILayout.ExpandWidth(true));
                        EditorGUILayout.LabelField(kv.Value.ToString(), GUILayout.Width(40));
                        EditorGUILayout.EndHorizontal();
                    }
                }
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
    
            // Toolbar
            EditorGUILayout.BeginHorizontal();
            bool newSelectAll = EditorGUILayout.ToggleLeft("Select All", _selectAll, GUILayout.Width(100));
            if (newSelectAll != _selectAll)
            {
                _selectAll = newSelectAll;
                for (int i = 0; i < _selected.Count; i++) _selected[i] = _selectAll;
            }
            if (GUILayout.Button("Refresh", GUILayout.Width(80))) LoadFiles();
            GUILayout.FlexibleSpace();
            _hideMeta = EditorGUILayout.ToggleLeft("Hide .meta", _hideMeta, GUILayout.Width(100));
            _backupBefore = EditorGUILayout.ToggleLeft("Backup current Assets before restore", _backupBefore, GUILayout.Width(260));
            EditorGUILayout.EndHorizontal();
    
            // File list
            EditorGUILayout.Space(6);
            EditorGUILayout.BeginVertical("box");
            // Build filtered index map
            var visibleIdx = new List<int>(_files.Count);
            for (int i = 0; i < _files.Count; i++)
            {
                if (_hideMeta && _files[i].EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                visibleIdx.Add(i);
            }
            int selCount = visibleIdx.Count(idx => _selected[idx]);
            EditorGUILayout.LabelField($"Items: {visibleIdx.Count}    Selected: {selCount}", EditorStyles.miniLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(220));
            if (visibleIdx.Count == 0) EditorGUILayout.LabelField("No files found in Current/ to restore.");
            foreach (var i in visibleIdx)
            {
                EditorGUILayout.BeginHorizontal();
                _selected[i] = EditorGUILayout.Toggle(_selected[i], GUILayout.Width(18));
                EditorGUILayout.LabelField(_files[i], GUILayout.ExpandWidth(true));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
    
            // Bottom bar
            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel", GUILayout.Height(22), GUILayout.Width(100))) { Close(); }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Restore selected", GUILayout.Height(22), GUILayout.Width(160))) { DoRestore(); }
            EditorGUILayout.EndHorizontal();
        }
    
        void DoRestore()
        {
            string srcRoot = Path.Combine(FileUtilEx.BackupRoot, "Current");
            if (!Directory.Exists(srcRoot)) { EditorUtility.DisplayDialog("Restore", "No Current/ backup found.", "OK"); return; }
    
            // Se richiesto, fai backup corrente degli Assets prima di sovrascrivere
            if (_backupBefore)
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string safedir = Path.Combine(FileUtilEx.BackupRoot, "PreRestore", stamp);
                try { foreach (var f in Directory.GetFiles(Path.Combine(FileUtilEx.ProjectRoot, "Assets"), "*", SearchOption.AllDirectories))
                    {
                        var rel = FileUtilEx.MakeRelToProject(f).Replace("\\", "/");
                        var dst = Path.Combine(safedir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        File.Copy(f, dst, true);
                    }
                    Log.Info("Pre-restore backup created: " + safedir);
                }
                catch (Exception ex) { Log.Warn("Pre-restore backup failed: " + ex.Message); if (!EditorUtility.DisplayDialog("Backup failed", "Pre-restore backup failed. Continue restore anyway?", "Yes", "No")) return; }
            }
    
            int restored = 0;
            for (int i = 0; i < _files.Count; i++)
            {
                if (!_selected[i]) continue;
                string rel = _files[i].Replace("/", Path.DirectorySeparatorChar.ToString());
                string src = Path.Combine(srcRoot, rel);
                string dst = Path.Combine(FileUtilEx.ProjectRoot, rel);
                try { Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.Copy(src, dst, true); restored++; }
                catch (Exception ex) { Log.Warn("Restore: failed to copy " + rel + " – " + ex.Message); }
            }
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Restore", $"Restore completed. Files restored: {restored}", "OK");
            Close();
        }
    }
}
#endif
