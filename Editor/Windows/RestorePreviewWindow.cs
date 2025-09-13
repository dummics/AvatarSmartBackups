#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    public class RestorePreviewWindow : EditorWindow
    {
    Vector2 _scroll;
    int _versionId;
    // Raw file list from version
    List<string> _files = new List<string>();
    List<FileDiffInfo> _diffInfos = new List<FileDiffInfo>();
    List<bool> _selected = new List<bool>();
    bool _selectAll = true;
    bool _backupBefore = true;
    bool _hideMeta = true;
    bool _advanced = false; // Easy mode default
    bool _groupByCategory = true;
    string _filter = string.Empty;
    Dictionary<string,int> _extCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
    Dictionary<string,int> _assetTypeCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
    enum DiffState { Same, Changed, New, Missing }
    class FileDiffInfo { public string rel; public DiffState state; public long size; public long projectSize; public string category; }
    
        public static void Open(int versionId)
        {
            var w = GetWindow<RestorePreviewWindow>(true, "Restore Preview", true);
            w._versionId = versionId;
            w.minSize = new Vector2(480, 320);
            w.LoadFiles();
            w.Show();
        }
        // legacy fallback
        public static void Open() => Open(-1);
    
        void LoadFiles()
        {
            _files.Clear(); _selected.Clear();
            _extCounts.Clear(); _assetTypeCounts.Clear();
            _diffInfos.Clear();
            string srcRoot = _versionId > 0 ? Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{_versionId:D3}") : Path.Combine(FileUtilEx.BackupRoot, "Current");
            if (!Directory.Exists(srcRoot)) return;
            // Per la cartella Current richiediamo backup.ok. Per le versioni archiviate assumiamo già completate salvo file mancante.
            var ok = Path.Combine(srcRoot, "backup.ok");
            if (_versionId <= 0 && !File.Exists(ok)) { EditorUtility.DisplayDialog("Restore", "Backup in progress or not complete.", "OK"); return; }
            foreach (var src in Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories))
            {
                string rel = BackupManager.MakeRelTo(src, srcRoot).Replace("\\", "/");
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
            BuildDiff(srcRoot);
        }

        void BuildDiff(string versionRoot)
        {
            string projectRoot = Path.Combine(FileUtilEx.ProjectRoot, "Assets").Replace("\\", "/");
            var projectSet = new HashSet<string>(Directory.GetFiles(projectRoot, "*", SearchOption.AllDirectories)
                .Select(p => "Assets/" + FileUtilEx.MakeRelToProject(p).Replace("\\", "/")).Where(r => !r.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)));
            foreach (var rel in _files)
            {
                string versionFile = Path.Combine(versionRoot, rel.Replace("/", Path.DirectorySeparatorChar.ToString()));
                string projectFile = Path.Combine(FileUtilEx.ProjectRoot, rel.Replace("/", Path.DirectorySeparatorChar.ToString()));
                long sizeVer = 0; long sizeProj = 0; try { sizeVer = new FileInfo(versionFile).Length; } catch { }
                if (File.Exists(projectFile)) { try { sizeProj = new FileInfo(projectFile).Length; } catch { } }
                DiffState st;
                if (!File.Exists(projectFile)) st = DiffState.New;
                else if (sizeProj != sizeVer) st = DiffState.Changed; else st = DiffState.Same;
                string category = Classify(rel);
                _diffInfos.Add(new FileDiffInfo { rel = rel, state = st, size = sizeVer, projectSize = sizeProj, category = category });
            }
        }

        string Classify(string rel)
        {
            string ext = Path.GetExtension(rel).ToLowerInvariant();
            if (ext == ".controller") return "Animator";
            if (ext == ".anim") return "Animation";
            if (ext == ".prefab") return "Prefab";
            if (ext == ".asset") return "Asset";
            if (ext == ".mat") return "Material";
            if (ext == ".unity") return "Scene";
            return string.IsNullOrEmpty(ext) ? "Other" : ext.Trim('.').ToUpperInvariant();
        }
    
        void OnGUI()
        {
            // Assicurati che eventuali cambi colore precedenti non contaminino tutto
            GUI.color = Color.white;
            EditorGUILayout.LabelField($"Restore Preview {( _versionId>0?"v"+_versionId.ToString("D3"):"Current")}", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            _advanced = GUILayout.Toggle(_advanced, _advanced ? "Advanced" : "Easy", "Button", GUILayout.Width(80));
            GUILayout.Space(6);
            if (_advanced)
                _groupByCategory = GUILayout.Toggle(_groupByCategory, "Group", "Button", GUILayout.Width(60));
            GUILayout.FlexibleSpace();
            if (_advanced)
                _filter = GUILayout.TextField(_filter, GUI.skin.FindStyle("ToolbarSeachTextField") ?? GUI.skin.textField, GUILayout.Width(160));
            EditorGUILayout.EndHorizontal();
            if (!_advanced)
                EditorGUILayout.HelpBox("Easy Mode: ripristina tutti i file della versione. Clicca Advanced per selezionare o vedere differenze.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Advanced Mode: vedi differenze vs progetto attuale. Stato: New (verde), Changed (giallo), Same (grigio).", MessageType.Info);
    
            if (_advanced)
            {
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("Summary", EditorStyles.boldLabel);
                int total = _diffInfos.Count;
                int news = _diffInfos.Count(d => d.state == DiffState.New);
                int changed = _diffInfos.Count(d => d.state == DiffState.Changed);
                int same = _diffInfos.Count(d => d.state == DiffState.Same);
                EditorGUILayout.LabelField($"Files: {total}   New: {news}   Changed: {changed}   Same: {same}", EditorStyles.miniLabel);
                if (_extCounts.Count > 0)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.BeginVertical(GUILayout.MaxWidth(180));
                    EditorGUILayout.LabelField("By ext", EditorStyles.miniBoldLabel);
                    foreach (var kv in _extCounts.OrderByDescending(k => k.Value).Take(8))
                        EditorGUILayout.LabelField($"{kv.Key}  {kv.Value}", EditorStyles.miniLabel);
                    EditorGUILayout.EndVertical();
                    if (_assetTypeCounts.Count > 0)
                    {
                        EditorGUILayout.BeginVertical();
                        EditorGUILayout.LabelField(".asset types", EditorStyles.miniBoldLabel);
                        foreach (var kv in _assetTypeCounts.OrderByDescending(k => k.Value).Take(8))
                            EditorGUILayout.LabelField($"{kv.Key}  {kv.Value}", EditorStyles.miniLabel);
                        EditorGUILayout.EndVertical();
                    }
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndVertical();
            }
    
            if (_advanced)
            {
                EditorGUILayout.BeginHorizontal();
                bool newSelectAll = EditorGUILayout.ToggleLeft("Select All", _selectAll, GUILayout.Width(100));
                if (newSelectAll != _selectAll)
                { _selectAll = newSelectAll; for (int i = 0; i < _selected.Count; i++) _selected[i] = _selectAll; }
                if (GUILayout.Button("Refresh", GUILayout.Width(80))) LoadFiles();
                GUILayout.FlexibleSpace();
                _hideMeta = EditorGUILayout.ToggleLeft("Hide .meta", _hideMeta, GUILayout.Width(90));
                _backupBefore = EditorGUILayout.ToggleLeft("Pre-backup", _backupBefore, GUILayout.Width(90));
                EditorGUILayout.EndHorizontal();
            }
    
            if (_advanced)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.BeginVertical("box");
                var visibleIdx = new List<int>(_files.Count);
                for (int i = 0; i < _files.Count; i++)
                {
                    if (_hideMeta && _files[i].EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(_filter) && !_files[i].Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
                    visibleIdx.Add(i);
                }
                int selCount = visibleIdx.Count(idx => _selected[idx]);
                EditorGUILayout.LabelField($"Items: {visibleIdx.Count}    Selected: {selCount}", EditorStyles.miniLabel);
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(220));
                IEnumerable<FileDiffInfo> ordered;
                if (_groupByCategory)
                    ordered = _diffInfos.Where(d => visibleIdx.Contains(_files.IndexOf(d.rel))).OrderBy(d => d.category).ThenBy(d => d.rel);
                else
                    ordered = _diffInfos.Where(d => visibleIdx.Contains(_files.IndexOf(d.rel))).OrderBy(d => d.rel);
                string currentCat = null;
                foreach (var d in ordered)
                {
                    int i = _files.IndexOf(d.rel);
                    if (i < 0) continue;
                    if (_groupByCategory && currentCat != d.category)
                    { currentCat = d.category; EditorGUILayout.LabelField(currentCat, EditorStyles.miniBoldLabel); }
                    EditorGUILayout.BeginHorizontal();
                    _selected[i] = EditorGUILayout.Toggle(_selected[i], GUILayout.Width(16));
                    GUIContent gc = new GUIContent(d.rel, d.state.ToString());
                    // Colore solo per l'etichetta diff senza inquinare resto UI
                    using (new GuiColorScope(d.state == DiffState.New ? new Color(0.55f, 0.85f, 0.55f, 1f)
                        : d.state == DiffState.Changed ? new Color(0.95f,0.85f,0.55f,1f)
                        : d.state == DiffState.Same ? new Color(0.8f,0.8f,0.8f,0.8f)
                        : GUI.color))
                    {
                        EditorGUILayout.LabelField(gc, GUILayout.ExpandWidth(true));
                    }
                    EditorGUILayout.LabelField(FormatSize(d.size), GUILayout.Width(70));
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }
            else
            {
                // Easy summary (no list)
                EditorGUILayout.Space(8);
                int total = _diffInfos.Count;
                int changed = _diffInfos.Count(d=>d.state==DiffState.Changed);
                int news = _diffInfos.Count(d=>d.state==DiffState.New);
                EditorGUILayout.LabelField($"Totale file: {total}   Nuovi: {news}   Modificati: {changed}", EditorStyles.miniLabel);
            }
    
            // Bottom bar
            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel", GUILayout.Height(22), GUILayout.Width(90))) { Close(); }
            GUILayout.FlexibleSpace();
            if (!_advanced)
            {
                if (GUILayout.Button(new GUIContent("Restore All", "Ripristina tutti i file della versione"), GUILayout.Height(24), GUILayout.Width(140))) { SelectAllInternal(true); DoRestore(); }
            }
            else
            {
                if (GUILayout.Button(new GUIContent("Restore Selected", "Ripristina solo i file selezionati"), GUILayout.Height(24), GUILayout.Width(160))) { DoRestore(); }
            }
            EditorGUILayout.EndHorizontal();
        }
    
        void DoRestore()
        {
            string srcRoot = _versionId > 0 ? Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{_versionId:D3}") : Path.Combine(FileUtilEx.BackupRoot, "Current");
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
                if (_advanced && !_selected[i]) continue; // in easy mode ignora selezione (tutto)
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

        void SelectAllInternal(bool val)
        { for (int i=0;i<_selected.Count;i++) _selected[i] = val; _selectAll = val; }

        string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("F1") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("F1") + " MB";
            double gb = mb / 1024.0;
            return gb.ToString("F2") + " GB";
        }

        struct GuiColorScope : IDisposable
        {
            Color _prev;
            public GuiColorScope(Color c) { _prev = GUI.color; GUI.color = c; }
            public void Dispose() { GUI.color = _prev; }
        }
    }
}
#endif
