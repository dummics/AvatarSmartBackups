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
using AvatarSmartBackup.Localization;

namespace AvatarSmartBackup
{
    public class RestorePreviewWindow : EditorWindow
    {
    Vector2 _scroll;
    int _versionId;
    int _resolvedVersionId;
    // Raw file list from version
    List<string> _files = new List<string>();
    List<FileDiffInfo> _diffInfos = new List<FileDiffInfo>();
    List<bool> _selected = new List<bool>();
    bool _backupBefore = true;
    bool _hideMeta = true;
    // Easy/Advanced mode bridge
    BackupSettings? _settings;
    bool _easyMode = false;
    bool _requireModeChoice = false;
    bool _easyBannerDismissed = false;
    double _nextSettingsRefresh = 0f;
    bool _showNew = true, _showChanged = true, _showSame = true;
    bool _showNamesOnly = false;
    bool _highlightMatches = true;
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
    // Manifest MD5 map (rel -> md5) se disponibile per la versione
    Dictionary<string,string>? _md5Map;
    // Async scan state
    bool _isScanning = false;
    int _scanTotal = 0, _scanProcessed = 0;
    double _scanStartTime;
    CancellationTokenSource? _scanCts;
    ConcurrentQueue<(int idx, DiffState state, long sizeVer, long sizeProj)> _scanResults = new ConcurrentQueue<(int, DiffState, long, long)>();
    bool _drainHookAdded = false;
    string? _currentVersionRoot;
    VersionInfo? _versionInfo;
    VersionDeltaMetadata? _deltaMetadata;
    string? _resolvedSnapshotRoot;
    string? _snapshotWarning;
    // (HashCache disabled fallback) – se HashCache.cs non ancora compilato nell'ambiente, usiamo MD5 diretto.

    const string PREF_FOLD_SUMMARY = "ASB_Restore_Fold_Summary";
    const string PREF_FOLD_FILTERS = "ASB_Restore_Fold_Filters";
    const string PREF_FOLD_SELECTION = "ASB_Restore_Fold_Selection";
    const string PREF_FOLD_FILES = "ASB_Restore_Fold_Files";
    const string PREF_EASY_BANNER = "ASB_Restore_EasyBanner";

    static readonly Color SummaryColorCheckpoint = new Color(0.23f, 0.46f, 0.80f, 0.18f);
    static readonly Color SummaryColorIncremental = new Color(0.45f, 0.35f, 0.78f, 0.18f);
    static readonly Color SummaryColorChanged = new Color(0.77f, 0.55f, 0.16f, 0.22f);
    static readonly Color SummaryColorRemoved = new Color(0.75f, 0.25f, 0.25f, 0.22f);
    const float SummaryBadgeWidth = 150f;
    static GUIStyle? _summaryBadgeLabelStyle;
    static GUIStyle? _summaryTypeLabelStyle;

        void OnEnable()
        {
            _foldSummary = EditorPrefs.GetBool(PREF_FOLD_SUMMARY, true);
            _foldFilters = EditorPrefs.GetBool(PREF_FOLD_FILTERS, true);
            _foldSelection = EditorPrefs.GetBool(PREF_FOLD_SELECTION, true);
            _foldFiles = EditorPrefs.GetBool(PREF_FOLD_FILES, true);
            _easyBannerDismissed = EditorPrefs.GetBool(PREF_EASY_BANNER, false);
            EnsureSettingsLoaded(force:true);
        }

        void OnDisable()
        {
            EditorPrefs.SetBool(PREF_FOLD_SUMMARY, _foldSummary);
            EditorPrefs.SetBool(PREF_FOLD_FILTERS, _foldFilters);
            EditorPrefs.SetBool(PREF_FOLD_SELECTION, _foldSelection);
            EditorPrefs.SetBool(PREF_FOLD_FILES, _foldFiles);
            EditorPrefs.SetBool(PREF_EASY_BANNER, _easyBannerDismissed);
        }
    
        void EnsureSettingsLoaded(bool force = false)
        {
            if (!force && _settings != null && EditorApplication.timeSinceStartup < _nextSettingsRefresh)
                return;

            BackupSettings? loaded = null;
            try
            {
                loaded = BackupManager.LoadSettings();
            }
            catch (Exception ex)
            {
                Log.Warn("RestorePreview: failed to load settings " + ex.Message);
                loaded = new BackupSettings { onboardingCompleted = true, easyMode = false };
            }

            _settings = loaded;
            _nextSettingsRefresh = EditorApplication.timeSinceStartup + 5f;

            var settings = _settings;
            if (settings == null)
            {
                _easyMode = false;
                _requireModeChoice = false;
                return;
            }

            bool easyFromSettings = settings.easyMode || !settings.AdvancedMode;
            if (settings.easyMode != easyFromSettings && settings.onboardingCompleted)
            {
                settings.easyMode = easyFromSettings;
                try { BackupManager.SaveSettings(settings); }
                catch (Exception ex) { Log.Warn("RestorePreview: failed to sync easy mode flag " + ex.Message); }
            }

            _requireModeChoice = !settings.onboardingCompleted;
            _easyMode = easyFromSettings;
            if (_requireModeChoice)
            {
                _easyMode = true;
                _easyBannerDismissed = false;
            }
        }

        public static void Open(int versionId)
        {
            var w = GetWindow<RestorePreviewWindow>(true, "Restore Preview", true);
            w._versionId = versionId;
            w._resolvedVersionId = versionId;
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
            _md5Map = null;
            _versionInfo = null;
            _deltaMetadata = null;
            _resolvedSnapshotRoot = null;

            string? srcRoot;
            if (_versionId > 0)
            {
                using var vmInfo = new FileBasedVersionManager();
                var requestedInfo = vmInfo.GetVersion(_versionId);
                if (requestedInfo == null)
                {
                    EditorUtility.DisplayDialog(L.T("rp.restore.title", "Restore"), string.Format(L.T("rp.restore.notfound", "Version #{0} not found."), _versionId), "OK");
                    return;
                }
                try
                {
                    srcRoot = VersionRestoreService.PrepareSnapshot(_versionId, forceRebuild: false);
                    if (string.IsNullOrEmpty(srcRoot))
                    {
                        var message = string.Format(L.T("rp.restore.prepare.fail", "Failed to prepare snapshot for version {0}:\n{1}"), _versionId, L.T("rp.restore.snapshot.missing", "Snapshot path unavailable."));
                        EditorUtility.DisplayDialog(L.T("rp.restore.title", "Restore"), message, "OK");
                        return;
                    }
                    var prep = VersionRestoreService.LastResult;
                    _resolvedVersionId = prep?.ResolvedVersionId ?? _versionId;
                    _versionInfo = _resolvedVersionId == _versionId ? requestedInfo : vmInfo.GetVersion(_resolvedVersionId);
                    if (_versionInfo == null)
                    {
                        EditorUtility.DisplayDialog(L.T("rp.restore.title", "Restore"), string.Format(L.T("rp.restore.notfound", "Version #{0} not found."), _resolvedVersionId), "OK");
                        return;
                    }
                    _deltaMetadata = VersionRestoreService.LoadDeltaMetadata(_versionInfo) ?? new VersionDeltaMetadata();
                    _snapshotWarning = prep?.Message ?? VersionRestoreService.LastWarning;
                }
                catch (Exception ex)
                {
                    EditorUtility.DisplayDialog(L.T("rp.restore.title", "Restore"), string.Format(L.T("rp.restore.prepare.fail", "Failed to prepare snapshot for version {0}:\n{1}"), _versionId, ex.Message), "OK");
                    return;
                }
            }
            else
            {
                srcRoot = Path.Combine(FileUtilEx.BackupRoot, "Current");
                _snapshotWarning = null;
                _resolvedVersionId = _versionId;
            }
            if (string.IsNullOrEmpty(srcRoot)) return;
            if (!Directory.Exists(srcRoot)) return;
            string resolvedRoot = srcRoot;
            _currentVersionRoot = resolvedRoot;
            _resolvedSnapshotRoot = resolvedRoot;
            if (_versionId <= 0)
            {
                var ok = Path.Combine(resolvedRoot, "backup.ok");
                if (!File.Exists(ok)) { EditorUtility.DisplayDialog(L.T("rp.restore.title", "Restore"), L.T("rp.restore.incomplete", "Backup in progress or not complete."), "OK"); return; }
            }
            int added = 0;
            void Enumerate(bool relaxed)
            {
                foreach (var src in Directory.GetFiles(resolvedRoot, "*", SearchOption.AllDirectories))
                {
                    string rel = BackupManager.MakeRelTo(src, resolvedRoot).Replace("\\", "/");
                    if (rel.StartsWith("delta/", StringComparison.OrdinalIgnoreCase)) continue;
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
            }
            LoadManifestMd5(srcRoot);
            PrepareCategoriesPlaceholders();
            StartAsyncScan(srcRoot);
        }

        // Carica il manifest.json (se esiste) ed estrae la mappa rel->md5 per uso nel diff
        [Serializable] class ManifestEntryMini
        {
            public string relPath = string.Empty;
            public string md5 = string.Empty;
        }
        [Serializable] class BackupManifestMini
        {
            public List<ManifestEntryMini> entries = new List<ManifestEntryMini>();
        }
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
            EnsureSettingsLoaded();

            if (_easyMode && (!_showNew || !_showChanged || !_showSame))
            {
                _showNew = _showChanged = _showSame = true;
                MarkAllCategoryCachesDirty(true);
            }

            // Assicurati che eventuali cambi colore precedenti non contaminino tutto
            GUI.color = Color.white;
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            string versionLabel;
            if (_versionId > 0)
            {
                versionLabel = _resolvedVersionId > 0 && _resolvedVersionId != _versionId
                    ? $"v{_versionId:D3} → v{_resolvedVersionId:D3}"
                    : $"v{_versionId:D3}";
            }
            else
            {
                versionLabel = L.T("rp.current", "Current");
            }
            EditorGUILayout.LabelField(L.T("rp.title", "Restore Preview") + " " + versionLabel, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent(L.T("rp.open.folder", "Open Folder"), L.T("rp.open.folder.tt", "Open version folder")), GUILayout.Width(100)))
            {
                int rootVersion = _resolvedVersionId > 0 ? _resolvedVersionId : _versionId;
                string root = rootVersion>0? Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{rootVersion:D3}") : Path.Combine(FileUtilEx.BackupRoot, "Current");
                EditorUtility.RevealInFinder(root);
            }
            EditorGUILayout.EndHorizontal();

            if (_versionInfo != null)
            {
                var infoMsg = _versionInfo.isCheckpoint
                    ? L.T("rp.info.full", "Full checkpoint: files are ready for direct restore.")
                    : string.Format(L.T("rp.info.incr", "Incremental version: automatically reconstructed from checkpoint #{0}."), _versionInfo.checkpointId);
                var diffCount = _deltaMetadata?.changedEntries?.Count ?? _versionInfo.changedFileCount;
                var removedCount = _deltaMetadata?.removedEntries?.Count ?? _versionInfo.removedFileCount;
                if (diffCount > 0 || removedCount > 0)
                    infoMsg += "\n" + string.Format(L.T("rp.info.changes", "Recorded changes: {0} (removed: {1})."), diffCount,removedCount);
                if (!string.IsNullOrEmpty(_snapshotWarning))
                    infoMsg += "\n" + _snapshotWarning;
                EditorGUILayout.HelpBox(infoMsg, MessageType.Info);
            }

            if (_requireModeChoice)
            {
                DrawModeChoiceGate();
                return;
            }

            if (_isScanning)
            {
                float p = _scanTotal > 0 ? (float)_scanProcessed / _scanTotal : 0f;
                EditorGUILayout.HelpBox(string.Format(L.T("rp.scan.progress", "Scanning files... {0}/{1} ({2:0.0}%)"), _scanProcessed, _scanTotal, p*100f), MessageType.Info);
                Rect r = GUILayoutUtility.GetRect(4, 18);
                EditorGUI.ProgressBar(r, p, L.T("rp.scan.bar", "Building diff"));
                GUILayout.Space(4);
            }

            if (_easyMode && !_easyBannerDismissed)
            {
                DrawEasyModeBanner();
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
                MarkAllCategoryCachesDirty(true);
            }

            EditorGUILayout.HelpBox(_easyMode
                ? L.T("rp.help.easy", "Pick the files you need. Summary, quick filters and the file list are below. The safety backup toggle stays at the bottom.")
                : L.T("rp.help", "Choose what to restore. Expand sections: Summary, Filters, Selection, Files. The safety backup toggle is in the footer."), MessageType.Info);

            // SUMMARY FOLDOUT (FIRST)
            _foldSummary = EditorGUILayout.BeginFoldoutHeaderGroup(_foldSummary, L.T("rp.fold.summary", "Summary"));
            if (_foldSummary)
            {
                int total = _diffInfos.Count; int news = _diffInfos.Count(d=>d.state==DiffState.New); int changed = _diffInfos.Count(d=>d.state==DiffState.Changed); int same = _diffInfos.Count(d=>d.state==DiffState.Same);
                if (_versionInfo != null)
                {
                    DrawVersionTypeBadge();
                    EditorGUILayout.BeginHorizontal();
                    if (_versionInfo.changedFileCount > 0)
                        DrawSummaryMiniBadge(SummaryColorChanged, string.Format(AvatarSmartBackup.Localization.L.T("vc.summary.changed", "Changed: {0}"), _versionInfo.changedFileCount));
                    if (_versionInfo.removedFileCount > 0)
                        DrawSummaryMiniBadge(SummaryColorRemoved, string.Format(AvatarSmartBackup.Localization.L.T("vc.summary.removed", "Removed: {0}"), _versionInfo.removedFileCount));
                    long changedBytes = _deltaMetadata?.changedBytes ?? 0;
                    if (changedBytes > 0)
                        DrawSummaryMiniBadge(SummaryColorChanged, string.Format(AvatarSmartBackup.Localization.L.T("vc.summary.bytes.changed", "Delta: {0}"), FormatSize(changedBytes)));
                    long removedBytes = _deltaMetadata?.removedBytes ?? 0;
                    if (removedBytes > 0)
                        DrawSummaryMiniBadge(SummaryColorRemoved, string.Format(AvatarSmartBackup.Localization.L.T("vc.summary.bytes.removed", "Removed bytes: {0}"), FormatSize(removedBytes)));
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.LabelField(string.Format(L.T("rp.stats", "Files: {0}   New: {1}   Changed: {2}   Same: {3}"), total, news, changed, same), EditorStyles.miniLabel);
                EditorGUILayout.BeginHorizontal();
                DrawBigStat(L.T("rp.stat.new", "NEW"), news.ToString(), new Color(0.40f,0.80f,0.45f,1f));
                DrawBigStat(L.T("rp.stat.changed", "CHANGED"), changed.ToString(), new Color(0.95f,0.80f,0.35f,1f));
                DrawBigStat(L.T("rp.stat.same", "SAME"), same.ToString(), new Color(0.55f,0.55f,0.55f,1f));
                GUILayout.FlexibleSpace();
                if (!_easyMode && GUILayout.Button(new GUIContent(L.T("rp.restore.all", "Restore ALL"), L.T("rp.restore.all.tt", "Restore all files")), GUILayout.Width(120), GUILayout.Height(28)))
                {
                    SelectAllInternal(true);
                    DoRestore();
                }
                EditorGUILayout.EndHorizontal();
                if (!_easyMode && _extCounts.Count > 0)
                {
                    GUILayout.Space(4);
                    EditorGUILayout.LabelField(L.T("rp.top.extensions", "Top extensions"), EditorStyles.miniBoldLabel);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.BeginVertical(GUILayout.MaxWidth(160));
                    foreach (var kv in _extCounts.OrderByDescending(k=>k.Value).Take(6)) EditorGUILayout.LabelField($"{kv.Key} {kv.Value}", EditorStyles.miniLabel);
                    EditorGUILayout.EndVertical();
                    if (_assetTypeCounts.Count > 0)
                    {
                        EditorGUILayout.BeginVertical(GUILayout.MaxWidth(180));
                        EditorGUILayout.LabelField(L.T("rp.asset.types", ".asset types"), EditorStyles.miniBoldLabel);
                        foreach (var kv in _assetTypeCounts.OrderByDescending(k=>k.Value).Take(6)) EditorGUILayout.LabelField($"{kv.Key} {kv.Value}", EditorStyles.miniLabel);
                        EditorGUILayout.EndVertical();
                    }
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // FILTERS FOLDOUT
            _foldFilters = EditorGUILayout.BeginFoldoutHeaderGroup(_foldFilters, L.T("rp.fold.filters", "Filters"));
            if (_foldFilters)
            {
                if (_easyMode)
                    DrawEasyFilters();
                else
                    DrawAdvancedFilters();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // SELECTION TOOLS FOLDOUT
            _foldSelection = EditorGUILayout.BeginFoldoutHeaderGroup(_foldSelection, L.T("rp.fold.selection", "Selection"));
            if (_foldSelection)
            {
                if (_easyMode)
                    DrawEasySelectionTools();
                else
                    DrawAdvancedSelectionTools();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();


            // (old filters/selection sections removed above)

            // FILES FOLDOUT
            _foldFiles = EditorGUILayout.BeginFoldoutHeaderGroup(_foldFiles, "Files");
            if (_foldFiles)
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(200));
                bool anyGroupVisible = false;
                foreach (var cat in _categoriesOrdered)
                {
                    if (!_categoryCache.TryGetValue(cat.name, out var cache)) { cache = new CategoryViewCache(); _categoryCache[cat.name]=cache; cache.dirty=true; }
                    if (cache.dirty)
                    {
                        RefreshCategoryView(cat, cache);
                    }
                    if (cache.totalVisible==0) continue;
                    anyGroupVisible = true;

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
                        cache.dirty = true;
                        RefreshCategoryView(cat, cache);
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
                        bool recalcAfterLoop = false;
                        foreach (var idxRaw in cache.visibleIndices)
                        {
                            var di = _diffInfos[idxRaw];
                            if (!_fileIndex.TryGetValue(di.rel, out int realIdx)) continue; // should exist
                            EditorGUILayout.BeginHorizontal();
                            bool prevSelected = _selected[realIdx];
                            bool newSelected = EditorGUILayout.Toggle(prevSelected, GUILayout.Width(16));
                            if (newSelected != prevSelected)
                            {
                                _selected[realIdx] = newSelected;
                                recalcAfterLoop = true;
                            }
                            using (new GuiColorScope(di.state == DiffState.New ? new Color(0.55f,0.85f,0.55f,1f)
                                : di.state == DiffState.Changed ? new Color(0.95f,0.85f,0.55f,1f)
                                : di.state == DiffState.Same ? new Color(0.8f,0.8f,0.8f,0.8f)
                                : GUI.color))
                            {
                                string labelSource = _showNamesOnly ? Path.GetFileName(di.rel) : di.rel;
                                if (string.IsNullOrEmpty(labelSource))
                                    labelSource = di.rel;

                                string labelText = labelSource;
                                if (_highlightMatches && !string.IsNullOrEmpty(_filter))
                                {
                                    int pos = labelSource.IndexOf(_filter, StringComparison.OrdinalIgnoreCase);
                                    if (pos >= 0)
                                    {
                                        var before = labelSource.Substring(0, pos);
                                        var match = labelSource.Substring(pos, _filter.Length);
                                        var after = labelSource.Substring(pos + _filter.Length);
                                        labelText = before + "<b><color=#FFFFFF>" + match + "</color></b>" + after;
                                    }
                                }

                                var rich = new GUIStyle(EditorStyles.label) { richText = _highlightMatches };
                                EditorGUILayout.LabelField(labelText, rich, GUILayout.ExpandWidth(true));
                            }
                            EditorGUILayout.LabelField(FormatSize(di.size), GUILayout.Width(70));
                            EditorGUILayout.EndHorizontal();
                        }
                        if (recalcAfterLoop)
                        {
                            cache.dirty = true;
                            RefreshCategoryView(cat, cache);
                        }
                    }
                }
                EditorGUILayout.EndScrollView();
                ComputeVisibleTotals(out int totalVisible, out int totalSelectedVisible);
                EditorGUILayout.LabelField(string.Format(L.T("rp.items.selected", "Items: {0}    Selected: {1}"), totalVisible,totalSelectedVisible), EditorStyles.miniLabel);
                if (!anyGroupVisible || totalVisible==0) EditorGUILayout.HelpBox(L.T("rp.no.files", "No files match the current filters."), MessageType.Info);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();


            // Footer
            GUILayout.Space(4);
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            if (GUILayout.Button(L.T("rp.cancel", "Cancel"), GUILayout.Width(90))) { Close(); }
            GUILayout.FlexibleSpace();
            _backupBefore = GUILayout.Toggle(_backupBefore, new GUIContent(L.T("rp.safety", "Safety Backup"), L.T("rp.safety.tt", "Make a safety copy before restoring")), GUILayout.Width(140));
            int selectedCount = 0; long selectedSize = 0; for (int i=0;i<_diffInfos.Count;i++) if (_selected[i]) { selectedCount++; selectedSize += _diffInfos[i].size; }
            GUILayout.Label(string.Format(L.T("rp.selected.summary", "Selected: {0} files ({1})"), selectedCount, FormatSize(selectedSize)), EditorStyles.miniLabel);
            if (!_easyMode && GUILayout.Button(new GUIContent(L.T("rp.restore.copy", "Restore as Copy"), L.T("rp.restore.copy.tt", "Copy selected files to another folder")), GUILayout.Width(150), GUILayout.Height(24))) { DoRestore(true); }
            if (GUILayout.Button(new GUIContent(L.T("rp.restore.selected", "Restore Selected"), L.T("rp.restore.selected.tt", "Restore selected files")), GUILayout.Width(140), GUILayout.Height(24))) { DoRestore(); }
            EditorGUILayout.EndHorizontal();

            // Shortcuts: Invio = restore (context aware), Esc = cancel
            if (Event.current.type == EventType.KeyDown)
            {
                if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                { DoRestore(); Event.current.Use(); }
                else if (Event.current.keyCode == KeyCode.Escape) { Close(); Event.current.Use(); }
            }
        }

        void DrawEasyModeBanner()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(L.T("rp.easy.banner", "Easy mode hides advanced filters. Switch to Advanced for full control when you need it."), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(L.T("ui.easy.switch", "Switch to Advanced"), GUILayout.Width(180)))
            {
                PromptSwitchToAdvanced();
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.T("rp.easy.banner.hide", "Hide tip"), GUILayout.Width(80)))
            {
                _easyBannerDismissed = true;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        void DrawModeChoiceGate()
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(L.T("rp.mode.gate.body", "Choose how you want to restore files before continuing."), MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(L.T("ui.mode.easy", "Easy"), GUILayout.Width(120), GUILayout.Height(28)))
            {
                CompleteModeChoice(true);
            }
            if (GUILayout.Button(L.T("ui.mode.advanced", "Advanced"), GUILayout.Width(120), GUILayout.Height(28)))
            {
                CompleteModeChoice(false);
            }
            EditorGUILayout.EndHorizontal();
            if (GUILayout.Button(L.T("onboarding.learn", "Learn more"), GUILayout.Width(160)))
            {
                EditorUtility.DisplayDialog(
                    L.T("onboarding.title", "Welcome to Avatar Smart Backup"),
                    L.T("onboarding.learn.body", "Easy mode keeps the essentials. Advanced mode exposes all tools."),
                    "OK");
            }
        }

        void CompleteModeChoice(bool easy)
        {
            _settings ??= BackupManager.LoadSettings();
            if (_settings == null)
                return;

            _settings.easyMode = easy;
            _settings.AdvancedMode = !easy;
            _settings.onboardingCompleted = true;
            BackupManager.SaveSettings(_settings);
            TimerService.InvalidateSettingsCache();

            _requireModeChoice = false;
            _easyMode = easy;
            _easyBannerDismissed = false;
            _nextSettingsRefresh = 0f;

            EditorUtility.DisplayDialog(
                L.T("rp.mode.choice.title", "Mode updated"),
                easy ? L.T("rp.mode.choice.easy", "Easy mode is active. You can switch later from the Backup window.") : L.T("rp.mode.choice.advanced", "Advanced mode is active. Full controls are now available."),
                "OK");
        }

        void PromptSwitchToAdvanced()
        {
            _settings ??= BackupManager.LoadSettings();
            if (_settings == null)
                return;

            bool confirm = EditorUtility.DisplayDialog(
                L.T("rp.mode.switch.title", "Switch to Advanced"),
                L.T("rp.mode.switch.body", "Advanced mode unlocks all filters and batch tools. Continue?"),
                L.T("rp.yes", "Yes"),
                L.T("rp.no", "No"));
            if (!confirm)
                return;

            CompleteModeChoice(false);
        }

        void DrawEasyFilters()
        {
            EditorGUILayout.HelpBox(L.T("rp.easy.filters.note", "Quick filter only. Advanced toggles live in Advanced mode."), MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            bool newHideMeta = EditorGUILayout.ToggleLeft(new GUIContent(L.T("rp.filter.hideMeta", "Hide .meta"), L.T("rp.filter.hideMeta.tt", "Hide .meta")), _hideMeta, GUILayout.Width(160));
            if (newHideMeta != _hideMeta)
            {
                _hideMeta = newHideMeta;
                MarkAllCategoryCachesDirty(true);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(L.T("rp.search", "Search:"), GUILayout.Width(60));
            string newFilter = GUILayout.TextField(_filter, GUILayout.Width(220));
            if (newFilter != _filter)
            {
                _filter = newFilter;
            }
            EditorGUILayout.EndHorizontal();
        }

        void DrawAdvancedFilters()
        {
            EditorGUILayout.BeginHorizontal();
            bool newShowNew = GUILayout.Toggle(_showNew, new GUIContent(L.T("rp.filter.new", "New"), L.T("rp.filter.new.tt", "Show New")), "Button", GUILayout.Width(60));
            bool newShowChanged = GUILayout.Toggle(_showChanged, new GUIContent(L.T("rp.filter.changed", "Changed"), L.T("rp.filter.changed.tt", "Show Changed")), "Button", GUILayout.Width(70));
            bool newShowSame = GUILayout.Toggle(_showSame, new GUIContent(L.T("rp.filter.same", "Same"), L.T("rp.filter.same.tt", "Show Same")), "Button", GUILayout.Width(60));
            bool newHideMeta = GUILayout.Toggle(_hideMeta, new GUIContent(L.T("rp.filter.hideMeta", "Hide .meta"), L.T("rp.filter.hideMeta.tt", "Hide .meta")), "Button", GUILayout.Width(80));
            bool newNamesOnly = GUILayout.Toggle(_showNamesOnly, new GUIContent(L.T("rp.filter.namesOnly", "Names"), L.T("rp.filter.namesOnly.tt", "Display only file names")), "Button", GUILayout.Width(80));
            bool newHighlight = GUILayout.Toggle(_highlightMatches, new GUIContent(L.T("rp.filter.highlight", "Highlight"), L.T("rp.filter.highlight.tt", "Highlight search matches")), "Button", GUILayout.Width(80));
            if (newShowNew!=_showNew || newShowChanged!=_showChanged || newShowSame!=_showSame || newHideMeta!=_hideMeta)
            {
                _showNew=newShowNew;
                _showChanged=newShowChanged;
                _showSame=newShowSame;
                _hideMeta=newHideMeta;
                MarkAllCategoryCachesDirty(true);
            }
            if (newNamesOnly != _showNamesOnly || newHighlight != _highlightMatches)
            {
                _showNamesOnly = newNamesOnly;
                _highlightMatches = newHighlight;
            }
            if (GUILayout.Button(L.T("rp.reset", "Reset"), GUILayout.Width(60)))
            {
                _showNew=_showChanged=_showSame=true;
                _hideMeta=true;
                _showNamesOnly=false;
                _highlightMatches=true;
                _filter="";
                MarkAllCategoryCachesDirty(true);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(L.T("rp.search", "Search:"), GUILayout.Width(48));
            _filter = GUILayout.TextField(_filter, GUILayout.Width(220));
            EditorGUILayout.EndHorizontal();
        }

        void DrawEasySelectionTools()
        {
            EditorGUILayout.HelpBox(L.T("rp.easy.selection.note", "Quick actions shown. Advanced batch tools are available in Advanced mode."), MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(L.T("rp.select.all", "Select All"), GUILayout.Width(90))) { SelectAllInternal(true); }
            if (GUILayout.Button(L.T("rp.deselect.all", "Deselect All"), GUILayout.Width(110))) { SelectAllInternal(false); }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.T("rp.refresh", "Refresh"), GUILayout.Width(80))) LoadFiles();
            EditorGUILayout.EndHorizontal();
        }

        void DrawAdvancedSelectionTools()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(L.T("rp.select.all", "Select All"), GUILayout.Width(90))) { SelectAllInternal(true); }
            if (GUILayout.Button(L.T("rp.select.newchanged", "Select New+Changed"), GUILayout.Width(140))) { ApplySelectStates(d=> d.state==DiffState.New || d.state==DiffState.Changed, true); }
            if (GUILayout.Button(L.T("rp.deselect.all", "Deselect All"), GUILayout.Width(100))) { SelectAllInternal(false); }
            if (GUILayout.Button(L.T("rp.invert", "Invert"), GUILayout.Width(70))) { for (int i=0;i<_selected.Count;i++) _selected[i]=!_selected[i]; MarkAllCategoryCachesDirty(true); }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.T("rp.refresh", "Refresh"), GUILayout.Width(70))) LoadFiles();
            EditorGUILayout.EndHorizontal();
        }

        void DoRestore(bool restoreAsCopy = false)
        {
            string? srcRoot;
            if (_versionId > 0)
            {
                try
                {
                    srcRoot = _resolvedSnapshotRoot;
                    if (string.IsNullOrEmpty(srcRoot) || !Directory.Exists(srcRoot))
                    {
                        srcRoot = VersionRestoreService.PrepareSnapshot(_versionId, forceRebuild: false);
                        if (string.IsNullOrEmpty(srcRoot))
                        {
                            var message = string.Format(L.T("rp.restore.prepare.fail", "Failed to prepare snapshot for version {0}:\n{1}"), _versionId, L.T("rp.restore.snapshot.missing", "Snapshot path unavailable."));
                            EditorUtility.DisplayDialog(L.T("rp.restore.title", "Restore"), message, "OK");
                            return;
                        }
                    }
                    var prep = VersionRestoreService.LastResult;
                    _resolvedVersionId = prep?.ResolvedVersionId ?? _versionId;
                    _snapshotWarning = prep?.Message ?? VersionRestoreService.LastWarning;
                    _resolvedSnapshotRoot = srcRoot;
                    _currentVersionRoot = srcRoot;
                    if (_versionInfo == null || _versionInfo.id != _resolvedVersionId)
                    {
                        using var vmInfo = new FileBasedVersionManager();
                        _versionInfo = vmInfo.GetVersion(_resolvedVersionId);
                    }
                }
                catch (Exception ex)
                {
                    EditorUtility.DisplayDialog(L.T("rp.restore.title", "Restore"), string.Format(L.T("rp.restore.prepareVersion.fail", @"Failed to prepare version data:
{0}"), ex.Message), "OK");
                    return;
                }
            }
            else
            {
                srcRoot = Path.Combine(FileUtilEx.BackupRoot, "Current");
                if (!Directory.Exists(srcRoot))
                {
                    EditorUtility.DisplayDialog(L.T("rp.restore.title", "Restore"), L.T("rp.current.notfound", "No Current/ backup found."), "OK");
                    return;
                }
                var ok = Path.Combine(srcRoot, "backup.ok");
                if (!File.Exists(ok))
                {
                    EditorUtility.DisplayDialog(L.T("rp.restore.title", "Restore"), L.T("rp.restore.incomplete", "Backup in progress or not complete."), "OK");
                    return;
                }
                _snapshotWarning = null;
            }

            if (string.IsNullOrEmpty(srcRoot))
            {
                EditorUtility.DisplayDialog(L.T("rp.restore.title", "Restore"), L.T("rp.version.notfound", "Version files not found."), "OK");
                return;
            }
            if (!Directory.Exists(srcRoot))
            {
                EditorUtility.DisplayDialog(L.T("rp.restore.title", "Restore"), L.T("rp.version.notfound", "Version files not found."), "OK");
                return;
            }

            string activeSrcRoot = srcRoot;

            string targetRoot;
            if (restoreAsCopy)
            {
                targetRoot = EditorUtility.OpenFolderPanel(L.T("rp.restore.copy.select", "Choose destination folder"), FileUtilEx.ProjectRoot, string.Empty);
                if (string.IsNullOrEmpty(targetRoot))
                    return;
            }
            else
            {
                targetRoot = FileUtilEx.ProjectRoot;
            }

            _currentVersionRoot = activeSrcRoot;

            if (!restoreAsCopy && _backupBefore)
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string safedir = Path.Combine(FileUtilEx.BackupRoot, "PreRestore", stamp);
                try
                {
                    foreach (var f in Directory.GetFiles(Path.Combine(FileUtilEx.ProjectRoot, "Assets"), "*", SearchOption.AllDirectories))
                    {
                        var rel = FileUtilEx.MakeRelToProject(f).Replace("\\", "/");
                        var dst = Path.Combine(safedir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        File.Copy(f, dst, true);
                    }
                    Log.Info("Pre-restore backup created: " + safedir);
                }
                catch (Exception ex)
                {
                    Log.Warn("Pre-restore backup failed: " + ex.Message);
                    if (!EditorUtility.DisplayDialog(L.T("rp.backup.failed.title", "Backup failed"), L.T("rp.backup.failed.body", "Pre-restore backup failed. Continue restore anyway?"), L.T("rp.yes", "Yes"), L.T("rp.no", "No")))
                        return;
                }
            }

            int restored = 0;
            for (int i = 0; i < _files.Count; i++)
            {
                if (!_selected[i]) continue;
                string rel = _files[i].Replace("/", Path.DirectorySeparatorChar.ToString());
                string src = Path.Combine(activeSrcRoot, rel);
                string dst = Path.Combine(targetRoot, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(src, dst, true);
                    restored++;
                }
                catch (Exception ex)
                {
                    Log.Warn("Restore: failed to copy " + rel + " - " + ex.Message);
                }
            }

            if (!restoreAsCopy)
            {
                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog(L.T("rp.restore.title", "Restore"), string.Format(L.T("rp.restore.done", "Restore completed. Files restored: {0}"), restored), "OK");
                Close();
            }
            else
            {
                EditorUtility.DisplayDialog(L.T("rp.restore.title", "Restore"), string.Format(L.T("rp.restore.copy.done", @"Files copied: {0}
Destination: {1}"), restored, targetRoot), "OK");
                if (!string.IsNullOrEmpty(targetRoot))
                    EditorUtility.RevealInFinder(targetRoot);
            }
        }

        void SelectAllInternal(bool val)
        {
            for (int i=0;i<_selected.Count;i++)
                _selected[i] = val;
            MarkAllCategoryCachesDirty(true);
        }

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

        void DrawSummaryMiniBadge(Color tint, string text)
        {
            var rect = GUILayoutUtility.GetRect(SummaryBadgeWidth, 20f, GUILayout.MaxWidth(SummaryBadgeWidth));
            EditorGUI.DrawRect(rect, tint);
            var labelStyle = _summaryBadgeLabelStyle ??= new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
            GUI.Label(rect, text, labelStyle);
        }

        void DrawVersionTypeBadge()
        {
            if (_versionInfo == null)
                return;

            var rect = EditorGUILayout.GetControlRect(false, 22f);
            var tint = _versionInfo.isCheckpoint ? SummaryColorCheckpoint : SummaryColorIncremental;
            EditorGUI.DrawRect(rect, tint);
            var typeLabelStyle = _summaryTypeLabelStyle ??= new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white }
            };
            string label = _versionInfo.isCheckpoint
                ? AvatarSmartBackup.Localization.L.T("vc.summary.checkpoint", "Full checkpoint (complete snapshot).")
                : string.Format(AvatarSmartBackup.Localization.L.T("vc.summary.incremental", "Incremental version (based on checkpoint #{0})."), _versionInfo.checkpointId > 0 ? _versionInfo.checkpointId.ToString() : "--");
            GUI.Label(new Rect(rect.x + 8f, rect.y + 3f, rect.width - 16f, rect.height - 6f), label, typeLabelStyle);
        }

        void RefreshCategoryView(CategoryGroup cat, CategoryViewCache cache)
        {
            if (cat == null || cache == null) return;
            cache.visibleIndices.Clear();
            cache.selectedCount = 0;
            cache.newCount = 0;
            cache.changedCount = 0;
            cache.totalVisible = 0;
            foreach (var idxRaw in cat.indices)
            {
                if (idxRaw < 0 || idxRaw >= _diffInfos.Count) continue;
                var di = _diffInfos[idxRaw];
                if (_hideMeta && di.rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                if ((!_showNew && di.state==DiffState.New) || (!_showChanged && di.state==DiffState.Changed) || (!_showSame && di.state==DiffState.Same)) continue;
                if (!string.IsNullOrEmpty(_filter) && di.rel.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                cache.visibleIndices.Add(idxRaw);
                cache.totalVisible++;
                if (idxRaw < _selected.Count && _selected[idxRaw]) cache.selectedCount++;
                if (di.state==DiffState.New) cache.newCount++;
                else if (di.state==DiffState.Changed) cache.changedCount++;
            }
            cache.dirty = false;
        }

        void ComputeVisibleTotals(out int totalVisible, out int totalSelectedVisible)
        {
            totalVisible = 0;
            totalSelectedVisible = 0;
            foreach (var cat in _categoriesOrdered)
            {
                if (!_categoryCache.TryGetValue(cat.name, out var cache)) continue;
                if (cache.dirty) RefreshCategoryView(cat, cache);
                totalVisible += cache.totalVisible;
                totalSelectedVisible += cache.selectedCount;
            }
        }

        void MarkAllCategoryCachesDirty(bool refreshNow)
        {
            foreach (var cat in _categoriesOrdered)
            {
                if (!_categoryCache.TryGetValue(cat.name, out var cache)) continue;
                cache.dirty = true;
                if (refreshNow) RefreshCategoryView(cat, cache);
            }
        }

        void ApplySelectStates(Func<FileDiffInfo,bool> predicate, bool val)
        {
            for (int i=0;i<_diffInfos.Count;i++)
                if (predicate(_diffInfos[i])) _selected[i] = val;
            MarkAllCategoryCachesDirty(true);
        }

        void RequestRescan()
        {
            if (_versionId > 0)
            {
                try
                {
                    var snapshotRoot = VersionRestoreService.PrepareSnapshot(_versionId, forceRebuild: true);
                    if (string.IsNullOrEmpty(snapshotRoot))
                    {
                        Log.Warn("Restore rescan failed: snapshot path unavailable.");
                        return;
                    }
                    _resolvedSnapshotRoot = snapshotRoot;
                    var prep = VersionRestoreService.LastResult;
                    _resolvedVersionId = prep?.ResolvedVersionId ?? _versionId;
                    _snapshotWarning = prep?.Message ?? VersionRestoreService.LastWarning;
                    _currentVersionRoot = _resolvedSnapshotRoot;
                    using var vmInfo = new FileBasedVersionManager();
                    _versionInfo = vmInfo.GetVersion(_resolvedVersionId);
                    _deltaMetadata = VersionRestoreService.LoadDeltaMetadata(_versionInfo) ?? new VersionDeltaMetadata();
                }
                catch (Exception ex)
                {
                    Log.Warn("Restore rescan failed: " + ex.Message);
                    return;
                }
            }
            if (_isScanning) return; // evitiamo sovrapposizioni
            if (string.IsNullOrEmpty(_currentVersionRoot) || !System.IO.Directory.Exists(_currentVersionRoot)) return;
            StartAsyncScan(_currentVersionRoot);
        }
    }
}
#endif
