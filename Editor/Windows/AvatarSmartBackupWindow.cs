#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.IO.Compression;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using AvatarSmartBackup; // explicit

namespace AvatarSmartBackup
{
    public class AvatarSmartBackupWindow : EditorWindow
    {
        Vector2 _scroll;
        Vector2 _versionsScroll;
        BackupSettings _settings;
        // Include/Exclude UI temp fields
        string _newIncludePattern = string.Empty;
        string _newExcludePattern = string.Empty;
        string _includePrefix = string.Empty;
        string _excludePrefix = string.Empty;
        UnityEngine.Object _includeFolderObj;
        UnityEngine.Object _excludeFolderObj;
    // Signature caching for gating manual version creation
    struct BackupSignature { public long size; public int count; }
    BackupSignature? _cachedCurrentSig; double _cachedCurrentSigTime;
    GUIStyle _selectedTitleStyle; // enlarged style for selected version title
    List<VersionInfo> _cachedVersions; // cached index
    int _renamingId = -1; string _renameBuffer = string.Empty; // rename state
    int _selectedVersionId = -1; // selected version id (default latest)
    // Doppio click tracking per apertura rapida preview
    int _lastClickId = -1; double _lastClickTime = -1;
    double _lastManualRunTime = -1; // throttle manual backup
    // Textures (Resources)
    static Texture2D _texExplorer; static bool _texTried;
    void EnsureTextures()
    {
        if (!_texTried)
        {
            _texExplorer = Resources.Load<Texture2D>("explorerIcon");
            _texTried = true;
        }
    }
    
        [MenuItem("Tools/Avatar Smart Backup")]
        public static void Open()
        {
            var w = GetWindow<AvatarSmartBackupWindow>(true, "Avatar Smart Backup");
            w.minSize = new Vector2(320, 320);
            w.Show();
        }
        [MenuItem("Tools/Avatar Smart Backup/Open", false, 0)]
        public static void OpenAlternative()
        {
            Open();
        }
    
        void OnEnable() => _settings = BackupManager.LoadSettings();
        void OnDisable() { BackupManager.SaveSettings(_settings); TimerService.InvalidateSettingsCache(); }
    
        void OnGUI()
        {
            if (_settings == null) _settings = BackupManager.LoadSettings();

            // Tabs
            string[] tabs = { "Backup", "Versions" };
            if (!_settings._uiTabInitialized) { _settings._activeTab = 0; _settings._uiTabInitialized = true; }
            _settings._activeTab = GUILayout.Toolbar(_settings._activeTab, tabs);
            EditorGUILayout.Space(4);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_settings._activeTab == 0) DrawBackupTab(); else DrawVersionsTab();
            EditorGUILayout.EndScrollView();

            if (GUI.changed)
            {
                BackupManager.SaveSettings(_settings);
                TimerService.InvalidateSettingsCache();
            }
        }
    
    void DrawBackupTab()
    {
        EditorGUILayout.LabelField("Avatar Smart Backup", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Automatic safety copies in background. Usa 'Backup Now' per forzare (throttle applicato).", MessageType.Info);

        DrawSchedulerSection();
        DrawPrimaryActions();

        EditorGUILayout.Space();
        bool newAdvanced = EditorGUILayout.ToggleLeft(new GUIContent("Advanced Mode", "Mostra opzioni avanzate (filtri, performance, verify, retention)."), _settings.AdvancedMode);
        if (newAdvanced != _settings.AdvancedMode)
        {
            _settings.AdvancedMode = newAdvanced;
            TimerService.InvalidateSettingsCache();
        }

        if (_settings.AdvancedMode)
        {
            DrawAdvancedOverview();
            DrawAdvancedSettings();
        }
    }

    void DrawSchedulerSection()
    {
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Automatic Backups", EditorStyles.boldLabel);

        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        bool running = Session.IsRunning;
        Color prev = GUI.backgroundColor;
        GUI.backgroundColor = running ? new Color(0.25f, 0.55f, 0.25f, 1f) : new Color(0.45f, 0.2f, 0.2f, 1f);
        if (GUILayout.Button(new GUIContent(running ? "Automatic Backups: ON" : "Automatic Backups: OFF", "Toggle background backup scheduler"), GUILayout.Width(220), GUILayout.Height(30)))
        {
            if (running) TimerService.PauseTimer();
            else TimerService.StartTimerIfNeeded(_settings);
        }
        GUI.backgroundColor = prev;
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField($"Next: {Session.NextRunUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "--"}    Last: {Session.LastBackupUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "never"}");

        if (_settings.AdvancedMode)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Interval (s)", GUILayout.Width(110));
            int newInterval = Mathf.Clamp(EditorGUILayout.IntField(_settings.intervalMinutes, GUILayout.Width(60)), 1, 119);
            if (newInterval != _settings.intervalMinutes)
                _settings.intervalMinutes = newInterval;
            EditorGUILayout.EndHorizontal();

            int effCopy = BackupManager.EffectiveCopyMBps(_settings);
            if (_settings.lastBackupBytes > 0 && effCopy > 0)
            {
                double secNeeded = _settings.lastBackupBytes / (effCopy * 1024.0 * 1024.0);
                if (secNeeded > _settings.intervalMinutes)
                {
                    double minutesNeeded = secNeeded / 60.0;
                    double mb = _settings.lastBackupBytes / (1024.0 * 1024.0);
                    EditorGUILayout.HelpBox($"At {effCopy} MB/s, backing up {mb:0.0} MB takes ~{minutesNeeded:0.0} min, exceeding the interval.", MessageType.Warning);
                }
            }
        }
        else
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"Interval: {_settings.intervalMinutes} min (Advanced Mode per modificare)", EditorStyles.miniLabel);
        }

        EditorGUILayout.EndVertical();
    }

    void DrawPrimaryActions()
    {
        const double BackupManualCooldownSeconds = 30;
        double now = EditorApplication.timeSinceStartup;
        bool throttle = !_settings.AdvancedMode;
        bool canManual = true;
        string tooltip = "Esegui subito un backup (cooldown 30s)";
        if (throttle && _lastManualRunTime > 0 && now - _lastManualRunTime < BackupManualCooldownSeconds)
        {
            canManual = false;
            double rem = BackupManualCooldownSeconds - (now - _lastManualRunTime);
            tooltip = $"Attendi {rem:0}s prima di un altro backup manuale";
        }

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(!canManual || BackupManager.IsBusy))
        {
            string backupTooltip = _settings.AdvancedMode ? "Esegui subito un backup (nessun cooldown in Advanced Mode)" : tooltip;
            if (GUILayout.Button(new GUIContent("Backup Now", backupTooltip)))
            {
                _lastManualRunTime = now;
                BackupManager.RunBackupNow(_settings, showToast: true, reason: "manual", showProgressUI: true);
            }
        }

        EnsureVersionsCache();
        var latest = GetLatestVersionCached();
        using (new EditorGUI.DisabledScope(latest == null))
        {
            string restoreTooltip = latest == null ? "Nessuna versione disponibile" : "Apri la versione più recente per ripristinare o ispezionare";
            if (GUILayout.Button(new GUIContent("Preview & Restore latest", restoreTooltip)))
            {
                if (latest != null)
                    RestorePreviewWindow.Open(latest.id);
            }
        }

        if (GUILayout.Button(new GUIContent("Open Backup Folder", "Apri la cartella dei backup")))
        {
            EditorUtility.RevealInFinder(FileUtilEx.BackupRoot);
        }
        EditorGUILayout.EndHorizontal();
    }

    void DrawAdvancedOverview()
    {
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Advanced Tools", EditorStyles.boldLabel);

        EnsureVersionsCache();
        var latest = GetLatestVersionCached();
        if (latest != null)
        {
            EditorGUILayout.LabelField("Latest Version:", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField($"# {latest.id}  {latest.description}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Files: {latest.fileCount}  Size: {FormatSize(latest.totalSizeBytes)}", EditorStyles.miniLabel);
            if (GUILayout.Button(new GUIContent("Go to Versions", "Open Versions tab"), GUILayout.Width(140)))
            {
                _settings._activeTab = 1;
            }
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(new GUIContent("Open Log Folder", "Apri la cartella dei log per assistenza"), GUILayout.Width(150)))
        {
            string logDir = Log.GetLogDirectory();
            if (Directory.Exists(logDir)) EditorUtility.RevealInFinder(logDir);
            else EditorUtility.DisplayDialog("Log Folder", "Log folder not found.", "OK");
        }
        if (GUILayout.Button(new GUIContent("Refresh Versions", "Ricarica l'elenco delle versioni"), GUILayout.Width(150)))
        {
            _cachedVersions = null;
            EnsureVersionsCache();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndVertical();
    }

    void DrawVersionsTab()
    {
    EditorGUILayout.LabelField("Versions", EditorStyles.boldLabel);
    EditorGUILayout.HelpBox("Restore points automatically created quando ci sono cambi. Usa la stella per mantenerle, clic per selezionare, doppio click per anteprima.", MessageType.Info);

        // Versions list (in-place selection, no reordering)
        EnsureVersionsCache();
        var latest = GetLatestVersionCached();
        if (_selectedVersionId < 0 && latest != null) _selectedVersionId = latest.id;

        // Toolbar line
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(new GUIContent("Refresh", "Reload versions from disk"), GUILayout.Width(70))) _cachedVersions = null;
        if (_settings.AdvancedMode)
        {
            GUI.enabled = !BackupManager.IsBusy;
            bool allowManualVersion = CanCreateManualVersion(out string reasonBlock);
            using (new EditorGUI.DisabledScope(!allowManualVersion))
            {
                if (GUILayout.Button(new GUIContent("Create Version", allowManualVersion ? "Create a manual restore point" : reasonBlock), GUILayout.Width(110)))
                {
                    using var vm = new FileBasedVersionManager();
                    vm.CreateVersion("Manual", Path.Combine(FileUtilEx.BackupRoot, "Current"));
                    _cachedVersions = null; EnsureVersionsCache();
                }
            }
            GUI.enabled = true;
        }
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        if (_cachedVersions == null || _cachedVersions.Count == 0)
        {
            EditorGUILayout.HelpBox("No versions yet.", MessageType.Info);
            return;
        }

        _versionsScroll = EditorGUILayout.BeginScrollView(_versionsScroll);
        int rowIndex = 0;
        Event e = Event.current;
    int? pendingTogglePin = null; // differiamo mutazioni per evitare rottura layout
    int? pendingDelete = null;
        foreach (var v in _cachedVersions.OrderByDescending(v => v.pinned).ThenByDescending(v => v.timestamp))
        {
            // Hard reset di sicurezza per evitare stato disabled ereditato
            GUI.enabled = true;
            bool isSelected = v.id == _selectedVersionId;
            if (_selectedTitleStyle == null)
                _selectedTitleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = EditorStyles.boldLabel.fontSize + 1 };

            EnsureTextures();
            // Begin card content
            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(v.pinned ? "ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã¢â‚¬Â¹Ãƒâ€¦Ã¢â‚¬Å“ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â¦" : "ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã¢â‚¬Â¹Ãƒâ€¦Ã¢â‚¬Å“ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â ", v.pinned ? "Rimuovi dai preferiti" : "Mantieni (non eliminare)"), GUILayout.Width(24))) pendingTogglePin = v.id;
            Rect starRect = GUILayoutUtility.GetLastRect(); // valido: c'ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¨ appena stato un controllo disegnato
            string title = $"#{v.id}  {(string.IsNullOrEmpty(v.description) ? "(no description)" : v.description)}";
            if (v.incomplete) title += "  (writing...)";
            if (latest!=null && latest.id==v.id) title = "Latest ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ " + title;
            EditorGUILayout.LabelField(title, isSelected ? _selectedTitleStyle : EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            Rect folderBtnRect = GUILayoutUtility.GetRect(20, 18, GUILayout.Width(20));
            if (_texExplorer != null && Event.current.type == EventType.Repaint)
                GUI.DrawTexture(folderBtnRect, _texExplorer, ScaleMode.ScaleToFit, true);
            else if (Event.current.type == EventType.Repaint && _texExplorer == null)
            { var style = EditorStyles.miniLabel; var pc = GUI.color; GUI.color = new Color(1,1,1,0.35f); GUI.Label(folderBtnRect, "ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â°ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¸ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã¢â‚¬Å“ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡", style); GUI.color = pc; }
            if (GUI.Button(folderBtnRect, GUIContent.none, GUIStyle.none)) { string dir = Path.Combine(FileUtilEx.BackupRoot, "Versions", $"v{v.id:D3}"); if (Directory.Exists(dir)) EditorUtility.RevealInFinder(dir); else EditorUtility.DisplayDialog("Version", "Folder not found", "OK"); }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField($"Created: {v.timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Files: {v.fileCount}    Size: {FormatSize(v.totalSizeBytes)}", EditorStyles.miniLabel);
            if (isSelected)
            {
                GUILayout.Space(4);
                EditorGUILayout.BeginVertical("box");
                if (_renamingId == v.id)
                {
                    EditorGUILayout.BeginHorizontal(); GUI.SetNextControlName("RenameField"); _renameBuffer = EditorGUILayout.TextField(_renameBuffer);
                    if (GUILayout.Button("Save", GUILayout.Width(50))) CommitRename(v);
                    if (GUILayout.Button("Cancel", GUILayout.Width(60))) { _renamingId=-1; _renameBuffer=string.Empty; }
                    EditorGUILayout.EndHorizontal(); if (e.isKey && e.keyCode == KeyCode.Return) CommitRename(v);
                }
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("Restore", "Apri anteprima e procedi al ripristino"), GUILayout.Height(22))) RestorePreviewWindow.Open(v.id);
                using (new EditorGUI.DisabledScope(v.pinned))
                { if (GUILayout.Button(new GUIContent("Delete", v.pinned ? "Versione preferita protetta" : "Elimina versione"), GUILayout.Height(22), GUILayout.Width(70))) { if (!v.pinned && EditorUtility.DisplayDialog("Delete Version", $"Delete version #{v.id}?", "Delete", "Cancel")) pendingDelete = v.id; } }
                GUILayout.FlexibleSpace(); EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField("F2 per rinominare", EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndVertical();
            Rect cardRect = GUILayoutUtility.GetLastRect();
            bool isHover = cardRect.Contains(e.mousePosition);
            if (Event.current.type == EventType.Repaint)
            {
                // Niente fill sopra il contenuto: solo un accento visivo non invasivo
                if (isSelected)
                {
                    var bar = new Rect(cardRect.x, cardRect.y, 4f, cardRect.height);
                    EditorGUI.DrawRect(bar, new Color(0.30f,0.65f,1f,0.95f));
                    // Sottile outline semi trasparente (dietro testo non lo schiaccia)
                    Handles.BeginGUI();
                    Handles.color = new Color(0.30f,0.65f,1f,0.35f);
                    Handles.DrawAAPolyLine(1.5f, new Vector3(cardRect.x, cardRect.y), new Vector3(cardRect.xMax, cardRect.y));
                    Handles.DrawAAPolyLine(1.5f, new Vector3(cardRect.xMax, cardRect.y), new Vector3(cardRect.xMax, cardRect.yMax));
                    Handles.DrawAAPolyLine(1.5f, new Vector3(cardRect.xMax, cardRect.yMax), new Vector3(cardRect.x, cardRect.yMax));
                    Handles.DrawAAPolyLine(1.5f, new Vector3(cardRect.x, cardRect.yMax), new Vector3(cardRect.x, cardRect.y));
                    Handles.EndGUI();
                }
                else if (isHover)
                {
                    var bar = new Rect(cardRect.x, cardRect.y, 3f, cardRect.height);
                    EditorGUI.DrawRect(bar, new Color(1f,1f,1f,0.25f));
                }
            }
            if (e.type == EventType.MouseDown && e.button == 0 && cardRect.Contains(e.mousePosition))
            { if (!starRect.Contains(e.mousePosition) && !folderBtnRect.Contains(e.mousePosition)) { double now = EditorApplication.timeSinceStartup; bool db = (_lastClickId == v.id) && (now - _lastClickTime < 0.35f); _lastClickId = v.id; _lastClickTime = now; _selectedVersionId = v.id; GUI.FocusControl(""); Repaint(); if (db) RestorePreviewWindow.Open(v.id); e.Use(); } }
            GUILayout.Space(4);
            rowIndex++;
        }
        EditorGUILayout.EndScrollView();

        // Gestione hotkey F2 per rename su selezionata
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F2 && _selectedVersionId > 0 && _renamingId != _selectedVersionId)
        {
            var sel = _cachedVersions.FirstOrDefault(vv => vv.id == _selectedVersionId);
            if (sel != null) { _renamingId = sel.id; _renameBuffer = sel.description; Repaint(); }
        }

        // Esegui azioni mutate fuori dal loop per evitare problemi layout
        if (pendingTogglePin.HasValue)
        {
            using var vm = new FileBasedVersionManager();
            vm.SetPinned(pendingTogglePin.Value, !_cachedVersions.First(v => v.id == pendingTogglePin.Value).pinned);
            _cachedVersions = null; EnsureVersionsCache();
            Repaint();
            GUIUtility.ExitGUI();
        }
        if (pendingDelete.HasValue)
        {
            try
            {
                using var vm = new FileBasedVersionManager();
                vm.DeleteVersion(pendingDelete.Value);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Delete", "Failed: " + ex.Message, "OK");
            }
            _cachedVersions = null; EnsureVersionsCache();
            if (_selectedVersionId == pendingDelete.Value) _selectedVersionId = -1;
            Repaint();
            GUIUtility.ExitGUI();
        }

        // Overlay icona cartella dopo avere l'intero contenuto (evita mismatch layout)
        if (Event.current.type == EventType.Repaint)
        {
            // Ridisegniamo tutte le card di nuovo? No: semplice approccio futuro -> TODO: convertire in IMGUIContainer overlay.
            // Per semplicitÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â  attuale: niente overlay multi pass; mantenere comportamento precedente (rimosso slot).
            // (Se serve davvero overlay fisso, reintrodurremo slot ma con contenuto invisibile invece di vuoto.)
        }
    }

    void EnsureVersionsCache()
    {
        if (_cachedVersions != null) return;
        try
        {
            using var vm = new FileBasedVersionManager();
            _cachedVersions = vm.GetVersions();
        }
        catch (Exception ex)
        {
            EditorGUILayout.HelpBox("Failed to load versions: " + ex.Message, MessageType.Error);
        }
    }

    string FormatSize(long bytes)
    {
        if (bytes <= 0) return "--";
        string[] units = { "B", "KB", "MB", "GB" };
        double val = bytes; int u = 0;
        while (val > 1024 && u < units.Length - 1) { val /= 1024; u++; }
        return $"{val:0.0} {units[u]}";
    }

    VersionInfo GetLatestVersionCached()
    {
        if (_cachedVersions == null || _cachedVersions.Count == 0) return null;
        return _cachedVersions.OrderByDescending(v => v.timestamp).FirstOrDefault();
    }

    bool CanCreateManualVersion(out string reason)
    {
        reason = string.Empty;
        if (!_settings.AdvancedMode && _cachedVersions != null && _cachedVersions.Count > 0)
        { reason = "Manual versions only in Debug (auto after backup)"; return false; }
        string currentDir = Path.Combine(FileUtilEx.BackupRoot, "Current");
        if (!Directory.Exists(currentDir)) { reason = "No Current backup yet"; return false; }
        double now = EditorApplication.timeSinceStartup;
        if (_cachedCurrentSig == null || now - _cachedCurrentSigTime > 2.0)
        {
            long total = 0; int count = 0;
            try
            {
                foreach (var f in Directory.GetFiles(currentDir, "*", SearchOption.AllDirectories))
                {
                    if (f.EndsWith("backup.ok", StringComparison.OrdinalIgnoreCase)) continue;
                    var fi = new FileInfo(f); total += fi.Length; count++;
                }
            }
            catch { }
            _cachedCurrentSig = new BackupSignature { size = total, count = count }; _cachedCurrentSigTime = now;
        }
        var sig = _cachedCurrentSig.Value;
        var latest = GetLatestVersionCached();
        if (latest != null && latest.fileCount == sig.count && latest.totalSizeBytes == sig.size)
        { reason = "No changes since last version"; return false; }
        return true;
    }


    void CommitRename(VersionInfo v)
    {
        string trimmed = (_renameBuffer ?? string.Empty).Trim();
        if (trimmed.Length == 0) { _renamingId = -1; _renameBuffer = string.Empty; return; }
        if (trimmed != v.description)
        {
            using var vm = new FileBasedVersionManager();
            vm.UpdateDescription(v.id, trimmed);
            _cachedVersions = null;
        }
        _renamingId = -1; _renameBuffer = string.Empty;
    }

    void DrawAdvancedSettings()
    {
        if (!_settings.AdvancedMode) return;
        EditorGUILayout.Space(10);
        _settings.showAdvanced = EditorGUILayout.Foldout(_settings.showAdvanced, "Advanced Settings");
        if (!_settings.showAdvanced) return;

        // ZIP POLICY
        if (_settings.AdvancedMode) // show snapshot policy only in advanced to declutter normal UX
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Snapshot (Zip) Policy", EditorStyles.boldLabel);
            if (GUILayout.Button(new GUIContent("?", "Legacy snapshot system ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã¢â‚¬Å“ mainly for compressed archives."), GUILayout.Width(22)))
            {
                EditorUtility.DisplayDialog("Snapshot Policy", "Snapshots are compressed .zip archives of the backup set. Regular users can rely on Versions instead.", "OK");
            }
            EditorGUILayout.EndHorizontal();
            _settings.zipPolicy = (ZipPolicy)EditorGUILayout.EnumPopup(new GUIContent("Mode", "When to create zip snapshots."), _settings.zipPolicy);
            using (new EditorGUI.DisabledScope(_settings.zipPolicy != ZipPolicy.Idle))
            {
                _settings.idleDelaySeconds = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Idle delay (s)", "Seconds of inactivity before creating a zip when policy is Idle."), _settings.idleDelaySeconds), 1, 3600);
            }
            _settings.keepSnapshots = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Keep last snapshots", "How many .zip snapshots to keep in the Archive folder (1 disables snapshots)."), _settings.keepSnapshots), 1, 50);
            _settings.zipFastest = EditorGUILayout.ToggleLeft(new GUIContent("Compression level: Fastest (quicker)", "Fastest is quicker but larger archives. Untick for Optimal (smaller, slower)."), _settings.zipFastest);
            EditorGUILayout.EndVertical();
        }

        // PERFORMANCE
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Performance", EditorStyles.boldLabel);
        _settings.autoThrottle = EditorGUILayout.ToggleLeft(new GUIContent("Auto throttle (recommended)", "Automatically caps IO speed to keep the editor responsive."), _settings.autoThrottle);
        _settings.maxParallelThreads = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Max parallel threads", "Number of concurrent copy/hash tasks."), _settings.maxParallelThreads), 1, Math.Max(1, System.Environment.ProcessorCount));
        _settings.saveScenesBeforeBackup = EditorGUILayout.ToggleLeft(new GUIContent("Save open scenes before backup", "Saves scenes if dirty before backup. May block briefly."), _settings.saveScenesBeforeBackup);
        if (_settings.AdvancedMode && _settings.lastMeasuredMBps > 0f)
            EditorGUILayout.LabelField($"Measured throughput: {_settings.lastMeasuredMBps:F1} MB/s", EditorStyles.miniLabel);
        // Benchmark button only in advanced (moved below) ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã¢â‚¬Å“ keeps UI simpler
        EditorGUILayout.EndVertical();

        // WHAT TO INCLUDE
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("What to include", EditorStyles.boldLabel);
        _settings.incVRCAssets = EditorGUILayout.ToggleLeft(new GUIContent("VRC Expressions (.asset)", "Common VRC expression assets and similarly named .asset files."), _settings.incVRCAssets);
        _settings.incAnimControllers = EditorGUILayout.ToggleLeft(new GUIContent("Animator Controllers (.controller)", "Animator controller assets."), _settings.incAnimControllers);
        _settings.incAnimationClips = EditorGUILayout.ToggleLeft(new GUIContent("Animation Clips (.anim)", "Animation clip assets."), _settings.incAnimationClips);
        _settings.incScenes = EditorGUILayout.ToggleLeft(new GUIContent("Scenes (.unity)", "Unity scene files."), _settings.incScenes);
        _settings.incMaterials = EditorGUILayout.ToggleLeft(new GUIContent("Materials (.mat) under size limit", "Small material files. Larger ones are skipped by threshold."), _settings.incMaterials);
        using (new EditorGUI.DisabledScope(!_settings.incMaterials))
        {
            string[] mNames = { "256 KB", "512 KB", "1 MB", "2 MB", "4 MB", "Custom" };
            long[] mValues = { 256, 512, 1024, 2048, 4096, -1 };
            _settings.materialsSizePresetIndex = EditorGUILayout.Popup(new GUIContent("Material size limit", "Skip materials larger than this size."), _settings.materialsSizePresetIndex, mNames);
            int mi = Mathf.Clamp(_settings.materialsSizePresetIndex, 0, mNames.Length - 1);
            if (mi < mNames.Length - 1)
            {
                _settings.materialsMaxKB = mValues[mi];
                EditorGUILayout.LabelField($"= {_settings.materialsMaxKB} KB");
            }
            else
            {
                long v = EditorGUILayout.LongField(new GUIContent("Custom (KB)", "Custom max size in KB."), _settings.materialsMaxKB);
                v = Math.Max(10L, Math.Min(v, 100L * 1024L));
                _settings.materialsMaxKB = v;
            }
        }
        _settings.incDlls = EditorGUILayout.ToggleLeft(new GUIContent("Plugin .dll in Assets (under size limit)", "Small .dll files under Assets/ (useful for simple plugins)."), _settings.incDlls);
        using (new EditorGUI.DisabledScope(!_settings.incDlls))
        {
            string[] dNames = { "512 KB", "1 MB", "2 MB", "4 MB", "Custom" };
            long[] dValues = { 512, 1024, 2048, 4096, -1 };
            if (_settings.dllSizePresetIndex == dNames.Length - 1) { for (int i=0;i<dValues.Length-1;i++) if (_settings.dllsMaxKB == dValues[i]) { _settings.dllSizePresetIndex = i; break; } }
            _settings.dllSizePresetIndex = EditorGUILayout.Popup(new GUIContent("DLL size limit", "Skip DLLs larger than this size."), _settings.dllSizePresetIndex, dNames);
            int di = Mathf.Clamp(_settings.dllSizePresetIndex, 0, dNames.Length - 1);
            if (di < dNames.Length - 1)
            {
                _settings.dllsMaxKB = dValues[di];
                EditorGUILayout.LabelField($"= {_settings.dllsMaxKB} KB");
            }
            else
            {
                long v = EditorGUILayout.LongField(new GUIContent("Custom (KB)", "Custom max size in KB."), _settings.dllsMaxKB);
                v = Math.Max(128L, Math.Min(v, 1024L * 10L));
                _settings.dllsMaxKB = v;
            }
        }
        EditorGUILayout.EndVertical();

        // FOLDERS & EXTENSIONS
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Folders & Types", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Add folders from Project or simple extensions (e.g., .prefab).", EditorStyles.miniLabel);
        _settings.extWithinIncludeFolders = EditorGUILayout.ToggleLeft(new GUIContent("Apply extensions only within included folders", "When enabled, extensions like .prefab are searched only inside the folders you included."), _settings.extWithinIncludeFolders);
        EditorGUILayout.EndVertical();
        DrawIncludeExcludeSection("Include", _settings.includeFolders, ref _newIncludePattern, ref _includePrefix, ref _includeFolderObj);
        EditorGUILayout.Space(6);
        DrawIncludeExcludeSection("Exclude", _settings.excludeFolders, ref _newExcludePattern, ref _excludePrefix, ref _excludeFolderObj);
        DrawTrackedSelectionSection();
        EditorGUILayout.EndVertical();

        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Diagnostics & Utilities", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        bool useGlobal = !_settings.useProjectSettings;
        bool newUseGlobal = EditorGUILayout.ToggleLeft(new GUIContent("Use global settings", "Use global settings instead of project-local ones."), useGlobal);
        if (EditorGUI.EndChangeCheck())
        {
            _settings.useProjectSettings = !newUseGlobal;
            BackupManager.SaveSettings(_settings);
            TimerService.InvalidateSettingsCache();
            _settings = BackupManager.LoadSettings();
        }
        if (GUILayout.Button(new GUIContent("Re-run benchmark", "Measure disk throughput again."), GUILayout.Width(150))) _ = BackupManager.RunManualBenchmarkAsync(_settings);
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Anti-spam cooldowns", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(new GUIContent("Snapshot cooldown (s)", "Minimum seconds between manual snapshots."), GUILayout.Width(160));
        _settings.manualSnapshotCooldownSeconds = Mathf.Clamp(EditorGUILayout.IntField(_settings.manualSnapshotCooldownSeconds, GUILayout.Width(60)), 1, 3600);
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(new GUIContent("Benchmark cooldown (s)", "Minimum seconds between manual benchmark runs."), GUILayout.Width(160));
        _settings.manualBenchmarkCooldownSeconds = Mathf.Clamp(EditorGUILayout.IntField(_settings.manualBenchmarkCooldownSeconds, GUILayout.Width(60)), 1, 3600);
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(new GUIContent("Min backup interval (s)", "Minimum seconds between manual backup requests."), GUILayout.Width(160));
        _settings.minManualBackupIntervalSeconds = Mathf.Clamp(EditorGUILayout.IntField(_settings.minManualBackupIntervalSeconds, GUILayout.Width(60)), 1, 3600);
        EditorGUILayout.EndHorizontal();
        _settings.enableDebugLogging = EditorGUILayout.ToggleLeft(new GUIContent("Detailed file logging", "Enable detailed logging to file for troubleshooting."), _settings.enableDebugLogging);
        EditorGUILayout.EndVertical();
    }
    
    
        void DrawTrackedSelectionSection()
        {
            if (_settings.trackedRoots == null) _settings.trackedRoots = new List<string>();
            var tracked = _settings.trackedRoots;
            bool frozen = BackupManager.IsSelectionFrozen(_settings);

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Tracked Selection", EditorStyles.boldLabel);

            if (tracked.Count == 0)
            {
                EditorGUILayout.LabelField("Tracking all assets allowed by filters.", EditorStyles.miniLabel);
            }
            else
            {
                int removeIndex = -1;
                for (int i = 0; i < tracked.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(tracked[i], GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Remove", GUILayout.Width(70))) removeIndex = i;
                    EditorGUILayout.EndHorizontal();
                }
                if (removeIndex >= 0)
                {
                    var updated = new List<string>(tracked);
                    updated.RemoveAt(removeIndex);
                    if (!BackupManager.TryUpdateTrackedSelection(updated, _settings.selectionLocked, out var err))
                        EditorUtility.DisplayDialog("Tracked Selection", err ?? "Failed to update selection.", "OK");
                    _settings = BackupManager.LoadSettings();
                    TimerService.InvalidateSettingsCache();
                    EditorGUILayout.EndVertical();
                    return;
                }
            }

            string folderToAdd = null;
            string fileToAdd = null;

            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(frozen))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Add Folder...", GUILayout.Width(110)))
                {
                    folderToAdd = EditorUtility.OpenFolderPanel("Select folder", FileUtilEx.ProjectRoot, string.Empty);
                }
                if (GUILayout.Button("Add File...", GUILayout.Width(110)))
                {
                    fileToAdd = EditorUtility.OpenFilePanel("Select asset", FileUtilEx.ProjectRoot, "*");
                }
                EditorGUILayout.EndHorizontal();

                if (!_settings.selectionLocked && GUILayout.Button("Lock Selection"))
                {
                    if (!BackupManager.TryUpdateTrackedSelection(_settings.trackedRoots, true, out var err))
                        EditorUtility.DisplayDialog("Tracked Selection", err ?? "Failed to lock selection.", "OK");
                    _settings = BackupManager.LoadSettings();
                    TimerService.InvalidateSettingsCache();
                    EditorGUILayout.EndVertical();
                    return;
                }
            }

            if (!string.IsNullOrEmpty(folderToAdd))
            {
                string rel = FileUtilEx.MakeRelToProject(folderToAdd).Replace("\\", "/");
                if (!rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    EditorUtility.DisplayDialog("Tracked Selection", "Please choose a folder inside the Assets directory.", "OK");
                }
                else
                {
                    if (!rel.EndsWith("/")) rel += "/";
                    var updated = new List<string>(_settings.trackedRoots ?? new List<string>()) { rel };
                    if (!BackupManager.TryUpdateTrackedSelection(updated, _settings.selectionLocked, out var err))
                        EditorUtility.DisplayDialog("Tracked Selection", err ?? "Failed to update selection.", "OK");
                    _settings = BackupManager.LoadSettings();
                    TimerService.InvalidateSettingsCache();
                    EditorGUILayout.EndVertical();
                    return;
                }
            }

            if (!string.IsNullOrEmpty(fileToAdd))
            {
                string rel = FileUtilEx.MakeRelToProject(fileToAdd).Replace("\\", "/");
                if (!rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    EditorUtility.DisplayDialog("Tracked Selection", "Please choose a file inside the Assets directory.", "OK");
                }
                else if (rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    EditorUtility.DisplayDialog("Tracked Selection", "Script files (.cs) are excluded from backups.", "OK");
                }
                else
                {
                    var updated = new List<string>(_settings.trackedRoots ?? new List<string>()) { rel };
                    if (!BackupManager.TryUpdateTrackedSelection(updated, _settings.selectionLocked, out var err))
                        EditorUtility.DisplayDialog("Tracked Selection", err ?? "Failed to update selection.", "OK");
                    _settings = BackupManager.LoadSettings();
                    TimerService.InvalidateSettingsCache();
                    EditorGUILayout.EndVertical();
                    return;
                }
            }

            if (frozen)
            {
                EditorGUILayout.HelpBox("Selection locked. Remove entries to narrow scope. Reset settings to change additions.", MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        static void DrawStringListVertical(List<string> list, string addLabel)
        {
            int remove = -1;
            for (int i = 0; i < list.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                list[i] = EditorGUILayout.TextField(list[i], GUILayout.ExpandWidth(true));
                if (GUILayout.Button("X", GUILayout.Width(20))) remove = i;
                EditorGUILayout.EndHorizontal();
            }
            if (remove >= 0) list.RemoveAt(remove);
            if (GUILayout.Button(addLabel)) list.Add("Assets/");
        }
    
        static void DrawIncludeExcludeSection(string title, List<string> list, ref string newExt, ref string newPrefix, ref UnityEngine.Object folderObj)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(title + $":  (" + list.Count + ")", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(list.Count == 0))
            {
                if (GUILayout.Button(new GUIContent("Clear", "Remove all entries from this list"), GUILayout.Width(60)))
                    list.Clear();
            }
            EditorGUILayout.EndHorizontal();
            // Add-area contained in a small box
            EditorGUILayout.BeginVertical("box");
            // Add folder (Project picker)
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Folder", GUILayout.Width(60));
            var newObj = EditorGUILayout.ObjectField(folderObj, typeof(DefaultAsset), false);
            if (newObj != folderObj) folderObj = newObj;
            using (new EditorGUI.DisabledScope(folderObj == null))
            {
                if (GUILayout.Button("Add", GUILayout.Width(60)))
                {
                    string p = AssetDatabase.GetAssetPath(folderObj);
                    if (string.IsNullOrEmpty(p) || !AssetDatabase.IsValidFolder(p))
                    {
                        EditorUtility.DisplayDialog("Not a folder", "Please select a folder inside the Project window.", "OK");
                    }
                    else
                    {
                        if (!p.EndsWith("/")) p += "/";
                        if (!list.Contains(p)) list.Add(p);
                        folderObj = null; // clear selection after add
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
    
            // Add extension
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Extension", GUILayout.Width(60));
            newExt = EditorGUILayout.TextField(newExt, GUILayout.ExpandWidth(true));
            EditorGUILayout.LabelField("e.g. .prefab", EditorStyles.miniLabel, GUILayout.Width(90));
            if (GUILayout.Button("Add", GUILayout.Width(60)))
            {
                var t = (newExt ?? string.Empty).Trim();
                if (t.StartsWith("*")) t = t.Substring(1);
                if (!string.IsNullOrEmpty(t))
                {
                    if (!t.StartsWith(".")) t = "." + t;
                    if (!list.Contains(t)) list.Add(t);
                    newExt = string.Empty;
                }
            }
            EditorGUILayout.EndHorizontal();
    
            // Add prefix (Assets/...)
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Prefix", GUILayout.Width(60));
            newPrefix = EditorGUILayout.TextField(newPrefix, GUILayout.ExpandWidth(true));
            EditorGUILayout.LabelField("e.g. Assets/SubFolder/", EditorStyles.miniLabel, GUILayout.Width(170));
            if (GUILayout.Button("Add", GUILayout.Width(60)))
            {
                var t = (newPrefix ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(t))
                {
                    if (!t.EndsWith("/")) t += "/";
                    if (!list.Contains(t)) list.Add(t);
                    newPrefix = string.Empty;
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical(); // end add-area box
    
            DrawThinSeparator();
    
            // Current entries (below)
            int remove = -1;
            for (int i = 0; i < list.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("", GUILayout.Width(2));
                list[i] = EditorGUILayout.TextField(list[i], GUILayout.ExpandWidth(true));
                if (GUILayout.Button("X", GUILayout.Width(20))) remove = i;
                EditorGUILayout.EndHorizontal();
            }
            if (remove >= 0) list.RemoveAt(remove);
        }
    
        static void DrawThinSeparator()
        {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(1f, 1f, 1f, 0.18f));
        }
    
        static void RestoreLatest()
        {
            string srcRoot = Path.Combine(FileUtilEx.BackupRoot, "Current");
            if (!Directory.Exists(srcRoot)) { EditorUtility.DisplayDialog("Restore", "No Current/ backup found.", "OK"); return; }
            var ok = Path.Combine(srcRoot, "backup.ok");
            if (!File.Exists(ok)) { EditorUtility.DisplayDialog("Restore", "Backup in progress or not complete.", "OK"); return; }
            foreach (var src in Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories))
            {
                string rel = MakeRelTo(src, srcRoot).Replace("\\", "/");
                if (!rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                if (rel.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || rel.Equals("backup.ok", StringComparison.OrdinalIgnoreCase)) continue;
                string dst = Path.Combine(FileUtilEx.ProjectRoot, rel);
                try { Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.Copy(src, dst, true); }
                catch (Exception ex) { Log.Warn("Restore: failed to copy " + rel + " ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã¢â‚¬Å“ " + ex.Message); }
            }
            MainThread.Invoke(() => AssetDatabase.Refresh());
            EditorUtility.DisplayDialog("Restore", "Restore completato.", "OK");
        }
    
        static string MakeRelTo(string p, string root)
        {
            var pu = new Uri(Path.GetFullPath(p));
            var ru = new Uri(Path.GetFullPath(root) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(ru.MakeRelativeUri(pu).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        // Removed separate selected card drawing (now inline)
    }
}
#endif
