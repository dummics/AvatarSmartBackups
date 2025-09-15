#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
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
    // New foldout-based UI (removes Easy/Advanced dichotomy)
    bool _showNew = true, _showChanged = true, _showSame = true;
    bool _foldSummary = true, _foldFilters = true, _foldSelection = true, _foldFiles = true;
    string _filter = string.Empty;
    string _lastFilter = string.Empty;
    Dictionary<string,int> _extCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
    Dictionary<string,int> _assetTypeCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
    enum DiffState { Same, Changed, New, Missing }
    class FileDiffInfo { public string rel; public DiffState state; public long size; public long projectSize; public string category; }
    class CategoryGroup { public string name; public List<int> indices = new List<int>(); public bool expanded = false; }
    class CategoryViewCache { public List<int> visibleIndices = new List<int>(); public int selectedCount; public int newCount; public int changedCount; public int totalVisible; public bool dirty = true; }
    Dictionary<string, CategoryViewCache> _categoryCache = new Dictionary<string, CategoryViewCache>(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, CategoryGroup> _categoryMap = new Dictionary<string, CategoryGroup>(StringComparer.OrdinalIgnoreCase);
    List<CategoryGroup> _categoriesOrdered = new List<CategoryGroup>();
    Dictionary<string,int> _fileIndex = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase); // rel -> index in _files
    bool _enumerationFallbackUsed = false;
    // Manifest MD5 map (rel -> md5) se disponibile per la versione
    Dictionary<string,string> _md5Map = null;
    // Async scan state
    bool _isScanning = false;
    int _scanTotal = 0, _scanProcessed = 0;
    double _scanStartTime;
    CancellationTokenSource _scanCts;
    ConcurrentQueue<(int idx, DiffState state, long sizeVer, long sizeProj)> _scanResults = new ConcurrentQueue<(int, DiffState, long, long)>();
    bool _drainHookAdded = false;
    string _currentVersionRoot;
    double _lastAutoRefreshRequest = -1;
    // (HashCache disabled fallback) – se HashCache.cs non ancora compilato nell'ambiente, usiamo MD5 diretto.

    const string PREF_FOLD_SUMMARY = "ASB_Restore_Fold_Summary";
    const string PREF_FOLD_FILTERS = "ASB_Restore_Fold_Filters";
    const string PREF_FOLD_SELECTION = "ASB_Restore_Fold_Selection";
    const string PREF_FOLD_FILES = "ASB_Restore_Fold_Files";

        void OnEnable()
        {
            _foldSummary = EditorPrefs.GetBool(PREF_FOLD_SUMMARY, true);
            _foldFilters = EditorPrefs.GetBool(PREF_FOLD_FILTERS, true);
            _foldSelection = EditorPrefs.GetBool(PREF_FOLD_SELECTION, true);
            _foldFiles = EditorPrefs.GetBool(PREF_FOLD_FILES, true);
        }

        void OnDisable()
        {
            EditorPrefs.SetBool(PREF_FOLD_SUMMARY, _foldSummary);
            EditorPrefs.SetBool(PREF_FOLD_FILTERS, _foldFilters);
            EditorPrefs.SetBool(PREF_FOLD_SELECTION, _foldSelection);
            EditorPrefs.SetBool(PREF_FOLD_FILES, _foldFiles);
        }
    
        public static void Open(int versionId)
        {
            var w = GetWindow<RestorePreviewWindow>(true, "Restore Preview", true);
            w._versionId = versionId;
            w.minSize = new Vector2(760, 520);
            w.LoadFiles();
            w.Show();
        }
        // legacy fallback
        public static void Open() => Open(-1);
    
        void LoadFiles()
        {
            CancelScan();
            _files.Clear(); _selected.Clear();
            _extCounts.Clear(); _assetTypeCounts.Clear();
            _diffInfos.Clear();
            _fileIndex.Clear();
            _categoryMap.Clear();
            _categoriesOrdered.Clear();
            _enumerationFallbackUsed = false;
            _md5Map = null;
            string srcRoot = _versionId > 0 ? Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{_versionId:D3}") : Path.Combine(FileUtilEx.BackupRoot, "Current");
            _currentVersionRoot = srcRoot;
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
            LoadManifestMd5(srcRoot);
            PrepareCategoriesPlaceholders();
            StartAsyncScan(srcRoot);
        }

        // Carica il manifest.json (se esiste) ed estrae la mappa rel->md5 per uso nel diff
        [Serializable] class ManifestEntryMini { public string relPath; public string md5; }
        [Serializable] class BackupManifestMini { public List<ManifestEntryMini> entries; }
        void LoadManifestMd5(string root)
        {
            try
            {
                string man = Path.Combine(root, "manifest.json");
                if (!File.Exists(man)) return;
                var json = File.ReadAllText(man, Encoding.UTF8);
                var mini = JsonUtility.FromJson<BackupManifestMini>(json);
                if (mini?.entries == null) return;
                _md5Map = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in mini.entries)
                {
                    if (string.IsNullOrEmpty(e?.relPath)) continue;
                    if (!string.IsNullOrEmpty(e.md5))
                    {
                        string rel = e.relPath.Replace("\\", "/");
                        // Il manifest non include le .meta, quindi la mappa resterà vuota per quelle.
                        _md5Map[rel] = e.md5;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("RestorePreview: impossibile caricare manifest md5: "+ex.Message);
            }
        }

        void PrepareCategoriesPlaceholders()
        {
            // Pre-costruisce categorie e placeholder diff infos (stato iniziale Unknown -> Same di default finché calcolato)
            _diffInfos.Clear();
            for (int i=0;i<_files.Count;i++)
            {
                string rel = _files[i];
                string category = Classify(rel);
                var info = new FileDiffInfo { rel = rel, state = DiffState.Same, size = 0, projectSize = 0, category = category };
                _diffInfos.Add(info);
            }
            RebuildCategories();
        }

        void StartAsyncScan(string versionRoot)
        {
            CancelScan();
            _scanCts = new CancellationTokenSource();
            _isScanning = true; _scanProcessed = 0; _scanTotal = _files.Count; _scanStartTime = EditorApplication.timeSinceStartup;
            _scanResults = new ConcurrentQueue<(int, DiffState, long, long)>();
            if (!_drainHookAdded)
            {
                _drainHookAdded = true;
                EditorApplication.update += DrainScanResults;
            }
            var token = _scanCts.Token;
            Task.Run(() =>
            {
                for (int i=0;i<_files.Count;i++)
                {
                    if (token.IsCancellationRequested) break;
                    var rel = _files[i];
                    string versionFile = Path.Combine(versionRoot, rel.Replace("/", Path.DirectorySeparatorChar.ToString()));
                    string projectFile = Path.Combine(FileUtilEx.ProjectRoot, rel.Replace("/", Path.DirectorySeparatorChar.ToString()));
                    long sizeVer = 0; long sizeProj = 0; try { sizeVer = new FileInfo(versionFile).Length; } catch { }
                    if (File.Exists(projectFile)) { try { sizeProj = new FileInfo(projectFile).Length; } catch { } }
                    DiffState st;
                    if (!File.Exists(projectFile)) st = DiffState.New;
                    else if (sizeProj != sizeVer) st = DiffState.Changed;
                    else
                    {
                        bool decided = false; DiffState tmp = DiffState.Same;
                        if (_md5Map != null && _md5Map.TryGetValue(rel, out var verMd5) && !string.IsNullOrEmpty(verMd5))
                        {
                            try
                            {
                                string projMd5 = GetProjectFileMd5(projectFile);
                                tmp = string.Equals(verMd5, projMd5, StringComparison.OrdinalIgnoreCase) ? DiffState.Same : DiffState.Changed;
                                decided = true;
                            }
                            catch { }
                        }
                        if (!decided)
                        {
                            bool identical = FilesAreIdentical(versionFile, projectFile);
                            tmp = identical ? DiffState.Same : DiffState.Changed;
                        }
                        st = tmp;
                    }
                    _scanResults.Enqueue((i, st, sizeVer, sizeProj));
                }
            }, token);
        }

        void DrainScanResults()
        {
            if (!_isScanning) return;
            int perFrame = 200; int applied = 0;
            while (applied < perFrame && _scanResults.TryDequeue(out var item))
            {
                applied++;
                _scanProcessed++;
                var di = _diffInfos[item.idx];
                bool changedState = di.state != item.state;
                di.state = item.state; di.size = item.sizeVer; di.projectSize = item.sizeProj;
                if (changedState && _categoryCache.TryGetValue(di.category, out var cvc)) cvc.dirty = true;
            }
            if (applied > 0) Repaint();
            if (_scanProcessed >= _scanTotal)
            {
                _isScanning = false;
                _scanCts?.Cancel();
                Repaint();
            }
        }

        void CancelScan()
        {
            if (_scanCts != null)
            {
                try { _scanCts.Cancel(); } catch { }
                _scanCts = null;
            }
            _isScanning = false;
        }

        // Confronto binario chunked per evitare carichi eccessivi in memoria.
        // Ritorna true se i file sono identici, false se differiscono (o se si verifica un'eccezione durante il confronto).
        bool FilesAreIdentical(string pathA, string pathB)
        {
            try
            {
                if (!File.Exists(pathA) || !File.Exists(pathB)) return false; // se manca uno, non sono identici
                var fiA = new FileInfo(pathA); var fiB = new FileInfo(pathB);
                if (fiA.Length != fiB.Length) return false;
                const int BUF = 64 * 1024; // 64KB
                byte[] bufferA = new byte[BUF];
                byte[] bufferB = new byte[BUF];
                using (var fa = new FileStream(pathA, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var fb = new FileStream(pathB, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int readA;
                    while ((readA = fa.Read(bufferA, 0, BUF)) > 0)
                    {
                        int readB = fb.Read(bufferB, 0, BUF);
                        if (readA != readB) return false; // inconsistenza improbabile
                        for (int i = 0; i < readA; i++) if (bufferA[i] != bufferB[i]) return false;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        void RebuildCategories()
        {
            _categoryMap.Clear(); _categoriesOrdered.Clear();
            _categoryCache.Clear();
            for (int i = 0; i < _diffInfos.Count; i++)
            {
                var cat = _diffInfos[i].category ?? "Other";
                if (!_categoryMap.TryGetValue(cat, out var grp))
                {
                    grp = new CategoryGroup { name = cat, expanded = false };
                    _categoryMap[cat] = grp; _categoriesOrdered.Add(grp);
                    _categoryCache[cat] = new CategoryViewCache();
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

        string GetProjectFileMd5(string absPath)
        {
            // Uso diretto di HashCache (deve essere nello stesso assembly). Fallback solo se eccezione.
            try { return FileUtilEx.MD5Of(absPath); } catch { return string.Empty; }
        }
    
        void OnGUI()
        {
            // Assicurati che eventuali cambi colore precedenti non contaminino tutto
            GUI.color = Color.white;
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Restore Preview {( _versionId>0?"v"+_versionId.ToString("D3"):"Current")}", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("Open Folder","Apri cartella versione"), GUILayout.Width(100)))
            {
                string root = _versionId>0? Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{_versionId:D3}") : Path.Combine(FileUtilEx.BackupRoot, "Current");
                EditorUtility.RevealInFinder(root);
            }
            EditorGUILayout.EndHorizontal();

            if (_isScanning)
            {
                float p = _scanTotal > 0 ? (float)_scanProcessed / _scanTotal : 0f;
                EditorGUILayout.HelpBox($"Scanning files... {_scanProcessed}/{_scanTotal} ({p*100f:0.0}%)", MessageType.Info);
                Rect r = GUILayoutUtility.GetRect(4, 18);
                EditorGUI.ProgressBar(r, p, "Building diff");
                GUILayout.Space(4);
            }

            if (_filter != _lastFilter)
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
            EditorGUILayout.HelpBox("Scegli cosa ripristinare. Espandi le sezioni: Summary (panoramica), Filters, Selection, Files. Il backup di sicurezza si trova nella barra finale.", MessageType.Info);

            // SUMMARY FOLDOUT (FIRST)
            _foldSummary = EditorGUILayout.BeginFoldoutHeaderGroup(_foldSummary, $"Summary");
            if (_foldSummary)
            {
                int total = _diffInfos.Count; int news = _diffInfos.Count(d=>d.state==DiffState.New); int changed = _diffInfos.Count(d=>d.state==DiffState.Changed); int same = _diffInfos.Count(d=>d.state==DiffState.Same);
                EditorGUILayout.LabelField($"Files: {total}   New: {news}   Changed: {changed}   Same: {same}", EditorStyles.miniLabel);
                EditorGUILayout.BeginHorizontal();
                DrawBigStat("NEW", news.ToString(), new Color(0.40f,0.80f,0.45f,1f));
                DrawBigStat("CHANGED", changed.ToString(), new Color(0.95f,0.80f,0.35f,1f));
                DrawBigStat("SAME", same.ToString(), new Color(0.55f,0.55f,0.55f,1f));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Restore ALL","Ripristina tutti i file"), GUILayout.Width(120), GUILayout.Height(28))) { SelectAllInternal(true); DoRestore(); }
                EditorGUILayout.EndHorizontal();
                if (_extCounts.Count > 0)
                {
                    GUILayout.Space(4);
                    EditorGUILayout.LabelField("Top extensions", EditorStyles.miniBoldLabel);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.BeginVertical(GUILayout.MaxWidth(160));
                    foreach (var kv in _extCounts.OrderByDescending(k=>k.Value).Take(6)) EditorGUILayout.LabelField($"{kv.Key} {kv.Value}", EditorStyles.miniLabel);
                    EditorGUILayout.EndVertical();
                    if (_assetTypeCounts.Count > 0)
                    {
                        EditorGUILayout.BeginVertical(GUILayout.MaxWidth(180));
                        EditorGUILayout.LabelField(".asset types", EditorStyles.miniBoldLabel);
                        foreach (var kv in _assetTypeCounts.OrderByDescending(k=>k.Value).Take(6)) EditorGUILayout.LabelField($"{kv.Key} {kv.Value}", EditorStyles.miniLabel);
                        EditorGUILayout.EndVertical();
                    }
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // FILTERS FOLDOUT
            _foldFilters = EditorGUILayout.BeginFoldoutHeaderGroup(_foldFilters, "Filters");
            if (_foldFilters)
            {
                EditorGUILayout.BeginHorizontal();
                bool newShowNew = GUILayout.Toggle(_showNew, new GUIContent("New","Mostra New"), "Button", GUILayout.Width(60));
                bool newShowChanged = GUILayout.Toggle(_showChanged, new GUIContent("Changed","Mostra Changed"), "Button", GUILayout.Width(70));
                bool newShowSame = GUILayout.Toggle(_showSame, new GUIContent("Same","Mostra Same"), "Button", GUILayout.Width(60));
                bool newHideMeta = GUILayout.Toggle(_hideMeta, new GUIContent("Hide .meta","Nascondi .meta"), "Button", GUILayout.Width(80));
                if (newShowNew!=_showNew || newShowChanged!=_showChanged || newShowSame!=_showSame || newHideMeta!=_hideMeta)
                { _showNew=newShowNew; _showChanged=newShowChanged; _showSame=newShowSame; _hideMeta=newHideMeta; foreach (var kv in _categoryCache) kv.Value.dirty=true; }
                if (GUILayout.Button("Reset", GUILayout.Width(60))) { _showNew=_showChanged=_showSame=true; _hideMeta=true; _filter=""; foreach (var kv in _categoryCache) kv.Value.dirty=true; }
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField("Search:", GUILayout.Width(48));
                _filter = GUILayout.TextField(_filter, GUILayout.Width(220));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // SELECTION TOOLS FOLDOUT
            _foldSelection = EditorGUILayout.BeginFoldoutHeaderGroup(_foldSelection, "Selection");
            if (_foldSelection)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Select All", GUILayout.Width(90))) { SelectAllInternal(true); }
                if (GUILayout.Button("Select New+Changed", GUILayout.Width(140))) { ApplySelectStates(d=> d.state==DiffState.New || d.state==DiffState.Changed, true); }
                if (GUILayout.Button("Deselect All", GUILayout.Width(100))) { SelectAllInternal(false); }
                if (GUILayout.Button("Invert", GUILayout.Width(70))) { for (int i=0;i<_selected.Count;i++) _selected[i]=!_selected[i]; }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Refresh", GUILayout.Width(70))) LoadFiles();
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

    
            // (old filters/selection sections removed above)
    
            // FILES FOLDOUT
            _foldFiles = EditorGUILayout.BeginFoldoutHeaderGroup(_foldFiles, "Files");
            if (_foldFiles)
            {
                int totalVisible = 0; int totalSelectedVisible = 0;
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(200));
                int groupsDrawn = 0;
                foreach (var cat in _categoriesOrdered)
                {
                    if (!_categoryCache.TryGetValue(cat.name, out var cache)) { cache = new CategoryViewCache(); _categoryCache[cat.name]=cache; cache.dirty=true; }
                    if (cache.dirty)
                    {
                        cache.visibleIndices.Clear(); cache.selectedCount=0; cache.newCount=0; cache.changedCount=0; cache.totalVisible=0;
                        foreach (var idxRaw in cat.indices)
                        {
                            var di = _diffInfos[idxRaw];
                            if (_hideMeta && di.rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                            if ((!_showNew && di.state==DiffState.New) || (!_showChanged && di.state==DiffState.Changed) || (!_showSame && di.state==DiffState.Same)) continue;
                            if (!string.IsNullOrEmpty(_filter) && !di.rel.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
                            cache.visibleIndices.Add(idxRaw);
                            cache.totalVisible++;
                            if (_selected[idxRaw]) cache.selectedCount++;
                            if (di.state==DiffState.New) cache.newCount++; else if (di.state==DiffState.Changed) cache.changedCount++;
                        }
                        cache.dirty = false;
                    }
                    if (cache.totalVisible==0) continue;
                    totalVisible += cache.totalVisible; totalSelectedVisible += cache.selectedCount; groupsDrawn++;

                    Rect rowRect = EditorGUILayout.BeginHorizontal();
                    // Tri-state per categoria
                    bool prevMixed = cache.selectedCount>0 && cache.selectedCount<cache.totalVisible;
                    EditorGUI.showMixedValue = prevMixed;
                    bool catToggleState = cache.selectedCount>0;
                    bool newToggleState = EditorGUILayout.Toggle(catToggleState, GUILayout.Width(16));
                    EditorGUI.showMixedValue = false;
                    if (newToggleState != catToggleState || (prevMixed && newToggleState==catToggleState))
                    {
                        bool target = !(cache.selectedCount == cache.totalVisible);
                        foreach (var vi in cache.visibleIndices) _selected[vi] = target;
                        cache.selectedCount = target? cache.totalVisible:0;
                    }
                    string badge = string.Empty;
                    if (cache.newCount>0) badge += $" <color=#41CC55>+{cache.newCount}</color>";
                    if (cache.changedCount>0) badge += $" <color=#E6C24A>Δ{cache.changedCount}</color>";
                    GUIStyle foldStyle = new GUIStyle(EditorStyles.foldoutHeader){richText=true};
                    // Simula foldout full-row cliccabile
                    Rect foldLabelRect = GUILayoutUtility.GetRect(new GUIContent("tmp"), foldStyle, GUILayout.ExpandWidth(true));
                    string foldText = $"{cat.name} ({cache.selectedCount}/{cache.totalVisible}){badge}";
                    cat.expanded = EditorGUI.Foldout(foldLabelRect, cat.expanded, foldText, true, foldStyle);
                    // Row click (escluso toggle): se clic dentro label rect e non sul toggle, toggla
                    if (Event.current.type==EventType.MouseDown && rowRect.Contains(Event.current.mousePosition) && !new Rect(rowRect.x,rowRect.y,16,rowRect.height).Contains(Event.current.mousePosition))
                    { cat.expanded = !cat.expanded; Event.current.Use(); }
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                    if (cat.expanded)
                    {
                        foreach (var idxRaw in cache.visibleIndices)
                        {
                            var di = _diffInfos[idxRaw];
                            if (!_fileIndex.TryGetValue(di.rel, out int realIdx)) continue; // should exist
                            EditorGUILayout.BeginHorizontal();
                            _selected[realIdx] = EditorGUILayout.Toggle(_selected[realIdx], GUILayout.Width(16));
                            if (_selected[realIdx]) cache.selectedCount = Math.Min(cache.selectedCount+0, cache.selectedCount); // placeholder keep counts consistent if later we recalc lazily
                            using (new GuiColorScope(di.state == DiffState.New ? new Color(0.55f,0.85f,0.55f,1f)
                                : di.state == DiffState.Changed ? new Color(0.95f,0.85f,0.55f,1f)
                                : di.state == DiffState.Same ? new Color(0.8f,0.8f,0.8f,0.8f)
                                : GUI.color))
                            {
                                string labelText = di.rel;
                                if (!string.IsNullOrEmpty(_filter))
                                {
                                    int pos = di.rel.IndexOf(_filter, StringComparison.OrdinalIgnoreCase);
                                    if (pos >= 0)
                                    {
                                        var before = di.rel.Substring(0,pos);
                                        var match = di.rel.Substring(pos,_filter.Length);
                                        var after = di.rel.Substring(pos+_filter.Length);
                                        labelText = before + "<b><color=#FFFFFF>" + match + "</color></b>" + after;
                                    }
                                }
                                GUIStyle rich = new GUIStyle(EditorStyles.label){richText=true};
                                EditorGUILayout.LabelField(labelText, rich, GUILayout.ExpandWidth(true));
                            }
                            EditorGUILayout.LabelField(FormatSize(di.size), GUILayout.Width(70));
                            EditorGUILayout.EndHorizontal();
                        }
                    }
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.LabelField($"Items: {totalVisible}    Selected: {totalSelectedVisible}", EditorStyles.miniLabel);
                if (totalVisible==0) EditorGUILayout.HelpBox("Nessun file corrisponde ai filtri correnti.", MessageType.Info);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // Footer
            GUILayout.Space(4);
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            if (GUILayout.Button("Cancel", GUILayout.Width(90))) { Close(); }
            GUILayout.FlexibleSpace();
            _backupBefore = GUILayout.Toggle(_backupBefore, new GUIContent("Safety Backup","Crea copia PreRestore prima"), GUILayout.Width(110));
            int selectedCount = 0; long selectedSize = 0; for (int i=0;i<_diffInfos.Count;i++) if (_selected[i]) { selectedCount++; selectedSize += _diffInfos[i].size; }
            GUILayout.Label($"Selected: {selectedCount} files ({FormatSize(selectedSize)})", EditorStyles.miniLabel);
            if (GUILayout.Button(new GUIContent("Restore Selected","Ripristina i file selezionati"), GUILayout.Width(140), GUILayout.Height(24))) { DoRestore(); }
            EditorGUILayout.EndHorizontal();

            // Shortcuts: Invio = restore (context aware), Esc = cancel
            if (Event.current.type == EventType.KeyDown)
            {
                if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                { DoRestore(); Event.current.Use(); }
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

        void ApplySelectStates(Func<FileDiffInfo,bool> predicate, bool val)
        { for (int i=0;i<_diffInfos.Count;i++) if (predicate(_diffInfos[i])) _selected[i] = val; }

        void RequestRescan()
        {
            if (_isScanning) return; // evitiamo sovrapposizioni
            if (string.IsNullOrEmpty(_currentVersionRoot) || !System.IO.Directory.Exists(_currentVersionRoot)) return;
            StartAsyncScan(_currentVersionRoot);
        }
    }
}
#endif
