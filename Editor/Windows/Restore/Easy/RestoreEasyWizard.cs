#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using AvatarSmartBackup.Localization;
using AvatarSmartBackup.Shared;
using VersionInfo = AvatarSmartBackup.VersionInfo;
using BackupManifest = AvatarSmartBackup.BackupManifest;
using FileBasedVersionManager = AvatarSmartBackup.FileBasedVersionManager;
using FileUtilEx = AvatarSmartBackup.FileUtilEx;
using Log = AvatarSmartBackup.Log;
using VersionRestoreService = AvatarSmartBackup.VersionRestoreService;

namespace AvatarSmartBackup.Restore.Easy
{
    internal sealed class RestoreEasyWizard : EditorWindow
    {
        enum WizardStep
        {
            SelectVersion,
            ChooseContent,
            Confirm
        }

        enum FilterPreset
        {
            Essentials,
            ScenesAndScripts,
            Everything
        }

        sealed class CategoryInfo
        {
            public string Name = string.Empty;
            public readonly List<int> Indices = new List<int>();
            public bool Selected;
            public long TotalSize;
        }

        sealed class EntryInfo
        {
            public string RelPath = string.Empty;
            public long Size;
            public string Category = string.Empty;
        }

        readonly ModernInfoBanner _modernBanner = new ModernInfoBanner();
        StatusBanner? _statusBanner;
        WizardStep _step = WizardStep.SelectVersion;
        Vector2 _scroll;
        Vector2 _versionScroll;

        readonly List<VersionInfo> _versions = new List<VersionInfo>();
        VersionInfo? _selectedVersion;
        int _selectedVersionIndex = -1;
        string? _snapshotRoot;
        string? _loadError;

        readonly List<EntryInfo> _entries = new List<EntryInfo>();
        readonly List<CategoryInfo> _categories = new List<CategoryInfo>();
        readonly List<bool> _selected = new List<bool>();
        FilterPreset? _activePreset;
        bool _safetyBackup = true;
        bool _restoreInProgress;
        bool _restoreCompleted;
        string? _completionMessage;

        StatusBanner Banner => _statusBanner ??= new StatusBanner(_modernBanner);

        public static void Open(VersionInfo? initialVersion = null)
        {
            var window = CreateInstance<RestoreEasyWizard>();
            window.titleContent = new GUIContent(L.T("easy.restore.title", "Guided Restore"));
            window.minSize = new Vector2(560, 420);
            window.ShowUtility();
            if (initialVersion != null)
            {
                window._selectedVersion = initialVersion;
                window._selectedVersionIndex = -1;
                window._step = WizardStep.ChooseContent;
                window.LoadManifestIfNeeded(force: true);
            }
        }

        void OnEnable()
        {
            LoadVersions();
        }

        void LoadVersions()
        {
            _versions.Clear();
            try
            {
                using var vm = new FileBasedVersionManager();
                _versions.AddRange(vm.GetVersions());
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
            }

            if (_selectedVersion != null)
            {
                int idx = _versions.FindIndex(v => v.id == _selectedVersion.id);
                if (idx >= 0) _selectedVersionIndex = idx;
            }
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField(L.T("easy.restore.header", "Guided Restore"), EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            DrawStepIndicator();
            EditorGUILayout.Space();

            switch (_step)
            {
                case WizardStep.SelectVersion:
                    DrawSelectVersion();
                    break;
                case WizardStep.ChooseContent:
                    DrawChooseContent();
                    break;
                case WizardStep.Confirm:
                    DrawConfirm();
                    break;
            }
        }

        void DrawStepIndicator()
        {
            string[] steps =
            {
                L.T("easy.restore.step.version", "1. Select backup"),
                L.T("easy.restore.step.content", "2. Choose content"),
                L.T("easy.restore.step.confirm", "3. Confirm")
            };

            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < steps.Length; i++)
            {
                bool active = (int)_step == i;
                using (new GuiColorScope(active ? new Color(0.3f, 0.6f, 0.3f, 1f) : GUI.color))
                {
                    GUILayout.Label(steps[i], active ? EditorStyles.boldLabel : EditorStyles.miniLabel);
                }
                if (i < steps.Length - 1)
                {
                    GUILayout.Label("→", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(14));
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        void DrawSelectVersion()
        {
            if (!string.IsNullOrEmpty(_loadError))
            {
                Banner.Draw(MessageType.Error, string.Format(L.T("easy.restore.load.error", "Unable to load versions: {0}"), _loadError));
                return;
            }

            if (_versions.Count == 0)
            {
                Banner.Draw(MessageType.Info, L.T("easy.restore.no.versions", "No backups available yet. Run a backup and try again."));
                return;
            }

            EditorGUILayout.LabelField(L.T("easy.restore.pick.version", "Choose which backup to restore"), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            _versionScroll = EditorGUILayout.BeginScrollView(_versionScroll);
            for (int i = 0; i < _versions.Count; i++)
            {
                var v = _versions[i];
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();
                bool selected = GUILayout.Toggle(_selectedVersionIndex == i, string.Empty, GUILayout.Width(16));
                if (selected && _selectedVersionIndex != i)
                {
                    _selectedVersionIndex = i;
                    _selectedVersion = v;
                    _snapshotRoot = null;
                    _entries.Clear();
                    _categories.Clear();
                    _selected.Clear();
                    _activePreset = null;
                    _restoreCompleted = false;
                    _completionMessage = null;
                }
                EditorGUILayout.BeginVertical();
                string desc = string.IsNullOrWhiteSpace(v.description) ? L.T("ui.overview.noDescription", "(no description)") : v.description;
                EditorGUILayout.LabelField(string.Format("#{0:D3}  {1}", v.id, desc), EditorStyles.label);
                EditorGUILayout.LabelField(string.Format(L.T("easy.restore.version.meta", "Files: {0}    Size: {1}"), v.fileCount, FormatSize(v.totalSizeBytes)), EditorStyles.miniLabel);
                DateTime created = ParseCreatedUtc(v);
                if (created != DateTime.MinValue)
                    EditorGUILayout.LabelField(created.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(_selectedVersion == null))
            {
                if (GUILayout.Button(L.T("easy.restore.next", "Next"), GUILayout.Height(26)))
                {
                    if (_selectedVersion != null)
                    {
                        _step = WizardStep.ChooseContent;
                        LoadManifestIfNeeded(force: true);
                    }
                }
            }
        }

        void DrawChooseContent()
        {
            if (_selectedVersion == null)
            {
                Banner.Draw(MessageType.Warning, L.T("easy.restore.no.version", "Select a backup first."));
                if (GUILayout.Button(L.T("easy.restore.back", "Back"), GUILayout.Width(120)))
                    _step = WizardStep.SelectVersion;
                return;
            }

            LoadManifestIfNeeded();

            if (!string.IsNullOrEmpty(_loadError))
            {
                Banner.Draw(MessageType.Error, string.Format(L.T("easy.restore.manifest.error", "Unable to read backup manifest: {0}"), _loadError));
                if (GUILayout.Button(L.T("easy.restore.back", "Back"), GUILayout.Width(120)))
                    _step = WizardStep.SelectVersion;
                return;
            }

            if (_entries.Count == 0)
            {
                Banner.Draw(MessageType.Info, L.T("easy.restore.empty", "This backup does not contain any files."));
                if (GUILayout.Button(L.T("easy.restore.back", "Back"), GUILayout.Width(120)))
                    _step = WizardStep.SelectVersion;
                return;
            }

            EditorGUILayout.LabelField(string.Format(L.T("easy.restore.selected.version", "Backup #{0:D3}"), _selectedVersion.id), EditorStyles.boldLabel);

            DrawPresetToolbar();
            EditorGUILayout.Space(4);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var cat in _categories)
            {
                EditorGUILayout.BeginHorizontal();
                bool next = EditorGUILayout.ToggleLeft(string.Format("{0} ({1})", cat.Name, cat.Indices.Count), cat.Selected, GUILayout.Width(220));
                GUILayout.FlexibleSpace();
                GUILayout.Label(FormatSize(cat.TotalSize), EditorStyles.miniLabel, GUILayout.Width(100));
                EditorGUILayout.EndHorizontal();
                if (next != cat.Selected)
                {
                    SetCategorySelected(cat, next, fromPreset: false);
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(L.T("easy.restore.back", "Back"), GUILayout.Width(120)))
                _step = WizardStep.SelectVersion;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.T("easy.restore.show.advanced", "Mostra opzioni avanzate"), GUILayout.Width(200)))
            {
                RestorePreviewWindow.Open(_selectedVersion.id);
                Close();
                GUIUtility.ExitGUI();
            }
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(!HasAnySelection()))
            {
                if (GUILayout.Button(L.T("easy.restore.next", "Next"), GUILayout.Width(120)))
                {
                    _step = WizardStep.Confirm;
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        void DrawConfirm()
        {
            if (_selectedVersion == null)
            {
                _step = WizardStep.SelectVersion;
                DrawSelectVersion();
                return;
            }

            if (!HasAnySelection())
            {
                Banner.Draw(MessageType.Warning, L.T("easy.restore.empty.selection", "Select at least one category to restore."));
            }

            if (_restoreCompleted && !string.IsNullOrEmpty(_completionMessage))
            {
                Banner.Draw(MessageType.Info, _completionMessage);
            }

            ComputeSelectionStats(out int selectedFiles, out long selectedSize);
            EditorGUILayout.LabelField(string.Format(L.T("easy.restore.summary", "Selected files: {0}    Size: {1}"), selectedFiles, FormatSize(selectedSize)), EditorStyles.wordWrappedMiniLabel);

            _safetyBackup = EditorGUILayout.ToggleLeft(new GUIContent(L.T("easy.restore.safety", "Create safety backup before restoring"), L.T("easy.restore.safety.tt", "Copies the current project files to a PreRestore folder.")), _safetyBackup);

            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(L.T("easy.restore.back", "Back"), GUILayout.Width(120)))
            {
                _step = WizardStep.ChooseContent;
                return;
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_restoreInProgress || !HasAnySelection()))
            {
                if (GUILayout.Button(L.T("easy.restore.execute", "Restore"), GUILayout.Width(160), GUILayout.Height(28)))
                {
                    PerformRestore();
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        void DrawPresetToolbar()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L.T("easy.restore.presets", "Presets"), GUILayout.Width(60));

            foreach (FilterPreset preset in Enum.GetValues(typeof(FilterPreset)))
            {
                bool active = _activePreset == preset;
                if (GUILayout.Toggle(active, GetPresetLabel(preset), "Button", GUILayout.Height(22)) != active)
                {
                    ApplyPreset(preset);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        GUIContent GetPresetLabel(FilterPreset preset)
        {
            return preset switch
            {
                FilterPreset.ScenesAndScripts => new GUIContent(L.T("easy.restore.preset.scenes", "Scene + script"), L.T("easy.restore.preset.scenes.tt", "Only include scenes and scripts.")),
                FilterPreset.Everything => new GUIContent(L.T("easy.restore.preset.all", "All files"), L.T("easy.restore.preset.all.tt", "Restore everything from this backup.")),
                _ => new GUIContent(L.T("easy.restore.preset.essentials", "Essential assets"), L.T("easy.restore.preset.essentials.tt", "Scenes, prefabs, scripts, materials.")),
            };
        }

        void ApplyPreset(FilterPreset preset)
        {
            HashSet<string> include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            switch (preset)
            {
                case FilterPreset.Essentials:
                    include.UnionWith(new[] { "Scenes", "Prefabs", "Scripts", "Materials" });
                    break;
                case FilterPreset.ScenesAndScripts:
                    include.UnionWith(new[] { "Scenes", "Scripts" });
                    break;
                case FilterPreset.Everything:
                    foreach (var cat in _categories)
                        include.Add(cat.Name);
                    break;
            }

            foreach (var cat in _categories)
            {
                bool shouldSelect = include.Contains(cat.Name);
                SetCategorySelected(cat, shouldSelect, fromPreset: true);
            }

            _activePreset = preset;
        }

        void SetCategorySelected(CategoryInfo cat, bool selected, bool fromPreset)
        {
            cat.Selected = selected;
            foreach (int idx in cat.Indices)
            {
                if (idx >= 0 && idx < _selected.Count)
                    _selected[idx] = selected;
            }

            if (!fromPreset)
                _activePreset = null;
        }

        void LoadManifestIfNeeded(bool force = false)
        {
            if (_selectedVersion == null)
                return;

            if (!force && _entries.Count > 0)
                return;

            _entries.Clear();
            _categories.Clear();
            _selected.Clear();
            _loadError = null;
            _restoreCompleted = false;
            _completionMessage = null;

            try
            {
                string versionsRoot = Path.Combine(FileUtilEx.BackupRoot, "Versions");
                string manifestPath = Path.Combine(versionsRoot, $"v{_selectedVersion.id:D3}", "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    _loadError = L.T("easy.restore.manifest.missing", "Manifest missing for this backup.");
                    return;
                }

                var json = File.ReadAllText(manifestPath);
                var manifest = JsonUtility.FromJson<BackupManifest>(json);
                if (manifest?.entries == null)
                {
                    _loadError = L.T("easy.restore.manifest.invalid", "Manifest is empty.");
                    return;
                }

                for (int i = 0; i < manifest.entries.Count; i++)
                {
                    var entry = manifest.entries[i];
                    if (entry == null || string.IsNullOrEmpty(entry.relPath))
                        continue;

                    string rel = entry.relPath.Replace('\\', '/');
                    string category = ResolveCategory(rel);
                    var info = new EntryInfo
                    {
                        RelPath = rel,
                        Size = Math.Max(0, entry.size),
                        Category = category
                    };
                    int index = _entries.Count;
                    _entries.Add(info);
                    _selected.Add(false);

                    var cat = _categories.FirstOrDefault(c => string.Equals(c.Name, category, StringComparison.OrdinalIgnoreCase));
                    if (cat == null)
                    {
                        cat = new CategoryInfo { Name = category };
                        _categories.Add(cat);
                    }
                    cat.Indices.Add(index);
                    cat.TotalSize += info.Size;
                }

                _categories.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                ApplyPreset(FilterPreset.Essentials);
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
            }
        }

        static string ResolveCategory(string relPath)
        {
            string lower = relPath.ToLowerInvariant();
            string ext = Path.GetExtension(lower);
            if (string.IsNullOrEmpty(ext))
            {
                if (lower.Contains("/scenes")) return "Scenes";
                if (lower.Contains("/scripts")) return "Scripts";
                return "Other";
            }

            switch (ext)
            {
                case ".unity": return "Scenes";
                case ".prefab": return "Prefabs";
                case ".mat":
                case ".material": return "Materials";
                case ".cs":
                case ".asmdef":
                case ".asmref":
                case ".uxml":
                case ".ussp": return "Scripts";
                case ".anim":
                case ".controller":
                case ".overridecontroller":
                case ".playable": return "Animation";
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".tga":
                case ".psd":
                case ".tiff":
                case ".exr":
                case ".bmp": return "Textures";
                case ".shader":
                case ".cginc":
                case ".compute":
                case ".hlsl": return "Shaders";
                case ".wav":
                case ".mp3":
                case ".ogg":
                case ".aiff":
                case ".aif":
                case ".flac": return "Audio";
                case ".asset":
                case ".inputactions": return "Settings";
                default:
                    return "Other";
            }
        }

        void ComputeSelectionStats(out int count, out long size)
        {
            count = 0;
            size = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (!_selected[i])
                    continue;
                count++;
                size += _entries[i].Size;
            }
        }

        bool HasAnySelection()
        {
            for (int i = 0; i < _selected.Count; i++)
            {
                if (_selected[i])
                    return true;
            }
            return false;
        }

        void PerformRestore()
        {
            if (_selectedVersion == null || !_selected.Any(s => s))
                return;

            _restoreInProgress = true;
            try
            {
                string? snapshot = PrepareSnapshot();
                if (string.IsNullOrEmpty(snapshot) || !Directory.Exists(snapshot))
                {
                    Banner.Draw(MessageType.Error, L.T("easy.restore.snapshot.missing", "Unable to prepare snapshot for this backup."));
                    return;
                }

                if (_safetyBackup)
                {
                    if (!CreateSafetyBackup())
                    {
                        if (!EditorUtility.DisplayDialog(L.T("easy.restore.safety.fail.title", "Safety backup failed"), L.T("easy.restore.safety.fail.body", "Unable to create safety backup. Continue restore anyway?"), L.T("rp.yes", "Yes"), L.T("rp.no", "No")))
                        {
                            return;
                        }
                    }
                }

                string targetRoot = FileUtilEx.ProjectRoot;
                int restored = 0;
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (!_selected[i])
                        continue;

                    var entry = _entries[i];
                    string src = Path.Combine(snapshot, entry.RelPath.Replace('/', Path.DirectorySeparatorChar));
                    string dst = Path.Combine(targetRoot, entry.RelPath.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        File.Copy(src, dst, true);
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("RestoreEasyWizard: failed to copy " + entry.RelPath + " - " + ex.Message);
                    }
                }

                AssetDatabase.Refresh();
                _restoreCompleted = true;
                _completionMessage = string.Format(L.T("easy.restore.done", "Restore completed. Files restored: {0}"), restored);
            }
            finally
            {
                _restoreInProgress = false;
            }
        }

        string? PrepareSnapshot()
        {
            if (_selectedVersion == null)
                return null;

            if (!string.IsNullOrEmpty(_snapshotRoot) && Directory.Exists(_snapshotRoot))
                return _snapshotRoot;

            try
            {
                string? path = VersionRestoreService.PrepareSnapshot(_selectedVersion.id, forceRebuild: false);
                _snapshotRoot = path;
                return path;
            }
            catch (Exception ex)
            {
                _completionMessage = string.Format(L.T("easy.restore.snapshot.error", "Failed to prepare snapshot: {0}"), ex.Message);
                Banner.Draw(MessageType.Error, _completionMessage);
                return null;
            }
        }

        bool CreateSafetyBackup()
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string safedir = Path.Combine(FileUtilEx.BackupRoot, "PreRestore", stamp);
                foreach (var f in Directory.GetFiles(Path.Combine(FileUtilEx.ProjectRoot, "Assets"), "*", SearchOption.AllDirectories))
                {
                    string rel = FileUtilEx.MakeRelToProject(f).Replace("\\", "/");
                    string dst = Path.Combine(safedir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(f, dst, true);
                }
                Log.Info("Pre-restore backup created: " + safedir);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("RestoreEasyWizard safety backup failed: " + ex.Message);
                return false;
            }
        }

        static DateTime ParseCreatedUtc(VersionInfo info)
        {
            if (!string.IsNullOrEmpty(info.createdUtc) && DateTime.TryParse(info.createdUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var c))
                return c;
            return info.timestamp;
        }

        static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("F1") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("F1") + " MB";
            double gb = mb / 1024.0;
            return gb.ToString("F2") + " GB";
        }

        readonly struct GuiColorScope : IDisposable
        {
            readonly Color _prev;
            public GuiColorScope(Color color)
            {
                _prev = GUI.color;
                GUI.color = color;
            }

            public void Dispose()
            {
                GUI.color = _prev;
            }
        }
    }
}
#endif
