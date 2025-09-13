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
    bool _showNew = true, _showChanged = true, _showSame = true;
    string _filter = string.Empty;
    string _lastFilter = string.Empty;
    Dictionary<string,int> _extCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
    Dictionary<string,int> _assetTypeCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
    enum DiffState { Same, Changed, New, Missing }
    class FileDiffInfo { public string rel; public DiffState state; public long size; public long projectSize; public string category; }
    class CategoryGroup { public string name; public List<int> indices = new List<int>(); public bool expanded = false; }
    Dictionary<string, CategoryGroup> _categoryMap = new Dictionary<string, CategoryGroup>(StringComparer.OrdinalIgnoreCase);
    List<CategoryGroup> _categoriesOrdered = new List<CategoryGroup>();
    Dictionary<string,int> _fileIndex = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase); // rel -> index in _files
    bool _enumerationFallbackUsed = false;
    
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
            _fileIndex.Clear();
            _categoryMap.Clear();
            _categoriesOrdered.Clear();
            _enumerationFallbackUsed = false;
            string srcRoot = _versionId > 0 ? Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{_versionId:D3}") : Path.Combine(FileUtilEx.BackupRoot, "Current");
            if (!Directory.Exists(srcRoot)) return;
            // Per la cartella Current richiediamo backup.ok. Per le versioni archiviate assumiamo già completate salvo file mancante.
            var ok = Path.Combine(srcRoot, "backup.ok");
            if (_versionId <= 0 && !File.Exists(ok)) { EditorUtility.DisplayDialog("Restore", "Backup in progress or not complete.", "OK"); return; }
            int added = 0;
            void Enumerate(bool relaxed)
            {
                foreach (var src in Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories))
                {
                    string rel = BackupManager.MakeRelTo(src, srcRoot).Replace("\\", "/");
                    if (!relaxed && !rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (rel.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || rel.Equals("backup.ok", StringComparison.OrdinalIgnoreCase) || rel.Equals("version.ok", StringComparison.OrdinalIgnoreCase)) continue;
                    _files.Add(rel);
                    _selected.Add(true);
                    _fileIndex[rel] = _files.Count - 1;
    
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
                            using var sr = new StreamReader(src, Encoding.UTF8);
                            char[] buffer = new char[8 * 1024];
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
                    added++;
                }
            };
            Enumerate(relaxed:false);
            if (added == 0)
            { // fallback diagnostico (magari i file non hanno prefisso Assets/ per qualche motivo)
                Enumerate(relaxed:true);
                if (_files.Count > 0) { _enumerationFallbackUsed = true; }
            }
            BuildDiff(srcRoot);
            RebuildCategories();
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

        void RebuildCategories()
        {
            _categoryMap.Clear(); _categoriesOrdered.Clear();
            for (int i = 0; i < _diffInfos.Count; i++)
            {
                var cat = _diffInfos[i].category ?? "Other";
                if (!_categoryMap.TryGetValue(cat, out var grp))
                {
                    grp = new CategoryGroup { name = cat, expanded = false };
                    _categoryMap[cat] = grp; _categoriesOrdered.Add(grp);
                }
                grp.indices.Add(i);
            }
            _categoriesOrdered.Sort((a,b)=>string.Compare(a.name,b.name,StringComparison.OrdinalIgnoreCase));
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
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            _advanced = GUILayout.Toggle(_advanced, _advanced ? "Advanced" : "Easy", "Button", GUILayout.Width(84));
            GUILayout.Space(6);
            EditorGUILayout.LabelField($"Restore Preview {( _versionId>0?"v"+_versionId.ToString("D3"):"Current")}", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (_advanced)
            {
                _showNew = GUILayout.Toggle(_showNew, new GUIContent("N","Mostra New"), "Button", GUILayout.Width(24));
                _showChanged = GUILayout.Toggle(_showChanged, new GUIContent("C","Mostra Changed"), "Button", GUILayout.Width(24));
                _showSame = GUILayout.Toggle(_showSame, new GUIContent("S","Mostra Same"), "Button", GUILayout.Width(24));
                GUILayout.Space(6);
                _hideMeta = GUILayout.Toggle(_hideMeta, new GUIContent(".meta","Nascondi .meta"), "Button", GUILayout.Width(48));
                GUILayout.Space(6);
                _filter = GUILayout.TextField(_filter, GUI.skin.FindStyle("ToolbarSeachTextField") ?? GUI.skin.textField, GUILayout.Width(170));
            }
            EditorGUILayout.EndHorizontal();
            if (_advanced && _filter != _lastFilter)
            {
                // Auto-expand only categories containing matches; collapse others. Empty filter -> collapse all.
                if (string.IsNullOrEmpty(_filter))
                {
                    foreach (var c in _categoriesOrdered) c.expanded = false;
                }
                else
                {
                    foreach (var c in _categoriesOrdered)
                    {
                        bool any = false;
                        for (int ci=0; ci<c.indices.Count; ci++)
                        {
                            var di = _diffInfos[c.indices[ci]];
                            if (_hideMeta && di.rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                            if (di.rel.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0) { any = true; break; }
                        }
                        c.expanded = any;
                    }
                }
                _lastFilter = _filter;
            }
            if (!_advanced)
                EditorGUILayout.HelpBox("Easy Mode: ripristina tutti i file della versione. Clicca Advanced per selezionare o vedere differenze.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Advanced: usa i bottoni N/C/S per filtrare stati. Verde=New, Giallo=Changed, Grigio=Same.", MessageType.Info);
    
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
                // Costruisci lista filtrata ottimizzata evitando O(n^2)
                // Costruiamo vista per categorie con foldout + tri-state
                int totalVisible = 0; int totalSelectedVisible = 0;
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(220));
                foreach (var cat in _categoriesOrdered)
                {
                    // Calcola stato selezione e filtra elementi per questa categoria
                    int catVisible = 0; int catSelected = 0;
                    for (int ci = 0; ci < cat.indices.Count; ci++)
                    {
                        var di = _diffInfos[cat.indices[ci]];
                        if (_hideMeta && di.rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.IsNullOrEmpty(_filter) && !di.rel.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
                        catVisible++;
                        if (_selected[_fileIndex[di.rel]]) catSelected++;
                    }
                    if (catVisible == 0 && !string.IsNullOrEmpty(_filter))
                    {
                        // Se filtro attivo e nessun match, non disegniamo la categoria
                        continue;
                    }
                    totalVisible += catVisible; totalSelectedVisible += catSelected;

                    Rect foldRect = EditorGUILayout.BeginHorizontal();
                    // Tri-state logic
                    bool prevMixed = false;
                    bool newState = catSelected > 0;
                    if (catSelected > 0 && catSelected < catVisible) { prevMixed = true; }
                    EditorGUI.showMixedValue = prevMixed;
                    bool toggled = EditorGUILayout.Toggle(newState, GUILayout.Width(16));
                    EditorGUI.showMixedValue = false;
                    if (toggled != newState || (prevMixed && toggled == newState))
                    {
                        // Toggle intera categoria: se era mixed o off -> on, se era on -> off
                        bool target = !(catSelected == catVisible); // se tutti selezionati, deseleziona; altrimenti seleziona tutti
                        for (int ci = 0; ci < cat.indices.Count; ci++)
                        {
                            var di = _diffInfos[cat.indices[ci]];
                            if (_hideMeta && di.rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!string.IsNullOrEmpty(_filter) && !di.rel.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
                            _selected[_fileIndex[di.rel]] = target;
                        }
                        catSelected = target ? catVisible : 0;
                    }
                    // Foldout
                    cat.expanded = EditorGUILayout.Foldout(cat.expanded, $"{cat.name}  ({catSelected}/{catVisible})", true);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                    if (cat.expanded)
                    {
                        // Disegna elementi
                        for (int ci = 0; ci < cat.indices.Count; ci++)
                        {
                            var di = _diffInfos[cat.indices[ci]];
                            if (_hideMeta && di.rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!string.IsNullOrEmpty(_filter) && !di.rel.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!_fileIndex.TryGetValue(di.rel, out int idx)) continue;
                            EditorGUILayout.BeginHorizontal();
                            _selected[idx] = EditorGUILayout.Toggle(_selected[idx], GUILayout.Width(16));
                            using (new GuiColorScope(di.state == DiffState.New ? new Color(0.55f, 0.85f, 0.55f, 1f)
                                : di.state == DiffState.Changed ? new Color(0.95f,0.85f,0.55f,1f)
                                : di.state == DiffState.Same ? new Color(0.8f,0.8f,0.8f,0.8f)
                                : GUI.color))
                            {
                                EditorGUILayout.LabelField(di.rel, GUILayout.ExpandWidth(true));
                            }
                            EditorGUILayout.LabelField(FormatSize(di.size), GUILayout.Width(70));
                            EditorGUILayout.EndHorizontal();
                        }
                    }
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.LabelField($"Items: {totalVisible}    Selected: {totalSelectedVisible}", EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
            }
            else
            {
                // EASY MODE REDESIGN
                GUILayout.Space(6);
                int total = _diffInfos.Count;
                int changed = _diffInfos.Count(d=>d.state==DiffState.Changed);
                int news = _diffInfos.Count(d=>d.state==DiffState.New);
                long sizeTotal = 0; foreach (var d in _diffInfos) sizeTotal += d.size;
                long sizeNoMeta = 0; foreach (var d in _diffInfos) if (!d.rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) sizeNoMeta += d.size;

                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("Version Summary", EditorStyles.boldLabel);
                EditorGUILayout.Space(2);
                // Stats row
                EditorGUILayout.BeginHorizontal();
                DrawBigStat("TOTAL", total.ToString(), new Color(0.75f,0.75f,0.75f,1f));
                DrawBigStat("NEW", news.ToString(), news>0? new Color(0.40f,0.80f,0.45f,1f): new Color(0.28f,0.55f,0.30f,0.9f));
                DrawBigStat("CHANGED", changed.ToString(), changed>0? new Color(0.95f,0.80f,0.35f,1f): new Color(0.65f,0.55f,0.25f,0.9f));
                DrawBigStat("SAME", (total-news-changed).ToString(), new Color(0.55f,0.55f,0.55f,1f));
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(6);
                EditorGUILayout.LabelField($"Size (all): {FormatSize(sizeTotal)}    Size (no .meta): {FormatSize(sizeNoMeta)}", EditorStyles.miniLabel);
                GUILayout.Space(4);
                _backupBefore = EditorGUILayout.ToggleLeft(new GUIContent("Create pre-restore safety backup", "Copia l'attuale Assets/ in PreRestore/ prima di sovrascrivere"), _backupBefore);
                GUILayout.Space(6);
                GUILayout.BeginHorizontal(); GUILayout.FlexibleSpace();
                GUIStyle bigBtn = new GUIStyle(GUI.skin.button){fontSize=14, fontStyle=FontStyle.Bold, fixedHeight=36, fixedWidth=200};
                if (GUILayout.Button(new GUIContent("RESTORE ALL", "Ripristina tutti i file della versione"), bigBtn)) { SelectAllInternal(true); DoRestore(); }
                GUILayout.FlexibleSpace(); GUILayout.EndHorizontal();
                GUILayout.Space(4);
                EditorGUILayout.HelpBox("Questo sovrascrive i file esistenti con quelli della versione selezionata.", MessageType.None);
                if (_enumerationFallbackUsed)
                {
                    EditorGUILayout.HelpBox("Files enumerati in fallback (nessun prefisso Assets/ trovato). Potrebbe indicare layout anomalo della versione.", MessageType.Warning);
                }
                if (total == 0)
                {
                    EditorGUILayout.HelpBox("Nessun file rilevato in questa versione. Se è una vecchia versione legacy, potrebbe non contenere i dati copiati.", MessageType.Info);
                }
                EditorGUILayout.EndVertical();
                GUILayout.FlexibleSpace(); // spinge il box in alto se si ridimensiona
            }
    
            // Bottom bar (solo per Advanced; in Easy il pulsante grosso è nel box)
            if (_advanced)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Cancel", GUILayout.Height(22), GUILayout.Width(90))) { Close(); }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Restore Selected", "Ripristina solo i file selezionati"), GUILayout.Height(24), GUILayout.Width(160))) { DoRestore(); }
                EditorGUILayout.EndHorizontal();
            }

            // Shortcuts: Invio = restore (context aware), Esc = cancel
            if (Event.current.type == EventType.KeyDown)
            {
                if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                { if (_advanced) DoRestore(); else { SelectAllInternal(true); DoRestore(); } Event.current.Use(); }
                else if (Event.current.keyCode == KeyCode.Escape) { Close(); Event.current.Use(); }
            }
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

        void DrawBigStat(string label, string value, Color col)
        {
            var prev = GUI.color; GUI.color = col;
            GUILayout.BeginVertical("box", GUILayout.Width(90));
            var labStyle = new GUIStyle(EditorStyles.miniBoldLabel){alignment=TextAnchor.MiddleCenter};
            var valStyle = new GUIStyle(EditorStyles.label){alignment=TextAnchor.MiddleCenter, fontSize=16, fontStyle=FontStyle.Bold};
            GUILayout.Label(label, labStyle);
            GUILayout.Label(value, valStyle);
            GUILayout.EndVertical();
            GUI.color = prev;
        }
    }
}
#endif
