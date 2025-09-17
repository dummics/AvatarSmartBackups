#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using AvatarSmartBackup.Localization;

namespace AvatarSmartBackup
{
    public class VersionChangesWindow : EditorWindow
    {
        enum ChangeKind { Added, Modified, Removed }

        struct ChangeRow
        {
            public ChangeKind Kind;
            public string Path;
            public string Category;
            public long Size;
        }

        static readonly Color ColorCheckpoint = new Color(0.23f, 0.46f, 0.80f, 0.18f);
        static readonly Color ColorIncremental = new Color(0.45f, 0.35f, 0.78f, 0.18f);
        static readonly Color ColorAdded = new Color(0.19f, 0.55f, 0.28f, 0.25f);
        static readonly Color ColorModified = new Color(0.77f, 0.55f, 0.16f, 0.22f);
        static readonly Color ColorRemoved = new Color(0.75f, 0.25f, 0.25f, 0.22f);

        VersionInfo _version;
        VersionDeltaMetadata _metadata;
        Vector2 _scroll;
        string _search = string.Empty;
        bool _showAdded = true;
        bool _showModified = true;
        bool _showRemoved = true;
        bool _showNamesOnly = false;
        bool _highlightSearch = true;

        public static void Open(VersionInfo version, VersionDeltaMetadata metadata)
        {
            if (version == null)
            {
                EditorUtility.DisplayDialog(L.T("vc.error.title", "Show changes"), L.T("vc.error.noversion", "Unable to open changes window: no version data."), "OK");
                return;
            }

            var title = string.Format(L.T("vc.window.title", "Changes for v{0}"), version.id);
            var window = GetWindow<VersionChangesWindow>(true, title, true);
            window.Initialize(version, metadata ?? new VersionDeltaMetadata());
            window.minSize = new Vector2(560, 420);
            window.Show();
        }

        void Initialize(VersionInfo version, VersionDeltaMetadata metadata)
        {
            _version = version;
            _metadata = metadata ?? new VersionDeltaMetadata();
            _scroll = Vector2.zero;
            _search = string.Empty;
            UpdateTitle();
        }

        void UpdateTitle()
        {
            if (_version != null)
                titleContent = new GUIContent(string.Format(L.T("vc.window.title", "Changes for v{0}"), _version.id));
        }

        void OnGUI()
        {
            if (_version == null)
            {
                EditorGUILayout.HelpBox(L.T("vc.no.version", "No version data available."), MessageType.Info);
                return;
            }

            UpdateTitle();

            DrawSummary();
            GUILayout.Space(4);
            DrawFilters();
            GUILayout.Space(6);
            DrawChangeList();
        }

        void DrawSummary()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(string.Format(L.T("vc.version.header", "Version #{0}"), _version.id), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(string.Format(L.T("vh.desc", "Description: {0}"), string.IsNullOrWhiteSpace(_version.description) ? L.T("ui.overview.noDescription", "(no description)") : _version.description), EditorStyles.miniLabel);

            var typeRow = EditorGUILayout.GetControlRect(false, 22f);
            DrawSummaryBadge(typeRow, _version.isCheckpoint ? ColorCheckpoint : ColorIncremental,
                _version.isCheckpoint ? L.T("vc.summary.checkpoint", "Full checkpoint (complete snapshot).") :
                    string.Format(L.T("vc.summary.incremental", "Incremental version (based on checkpoint #{0})."), _version.checkpointId > 0 ? _version.checkpointId.ToString() : "--"));

            if (_version.changedFileCount > 0 || _version.removedFileCount > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();
                if (_version.changedFileCount > 0)
                {
                    DrawMiniBadge(ColorModified, string.Format(L.T("vc.summary.changed", "Changed: {0}"), _version.changedFileCount));
                }
                if (_version.removedFileCount > 0)
                {
                    DrawMiniBadge(ColorRemoved, string.Format(L.T("vc.summary.removed", "Removed: {0}"), _version.removedFileCount));
                }
                if (_metadata.changedBytes > 0)
                {
                    DrawMiniBadge(ColorModified, string.Format(L.T("vc.summary.bytes.changed", "Delta: {0}"), FormatSize(_metadata.changedBytes)));
                }
                if (_metadata.removedBytes > 0)
                {
                    DrawMiniBadge(ColorRemoved, string.Format(L.T("vc.summary.bytes.removed", "Removed bytes: {0}"), FormatSize(_metadata.removedBytes)));
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        void DrawSummaryBadge(Rect rect, Color tint, string text)
        {
            EditorGUI.DrawRect(rect, tint);
            var labelRect = new Rect(rect.x + 8, rect.y + 3, rect.width - 16, rect.height - 6);
            GUI.Label(labelRect, text, Styles.SummaryLabel);
        }

        void DrawMiniBadge(Color tint, string text)
        {
            Rect rect = GUILayoutUtility.GetRect(StyleCache.MiniBadgeWidth, 20f, Styles.BadgeLabel);
            rect.width = Mathf.Max(rect.width, StyleCache.MiniBadgeWidth);
            EditorGUI.DrawRect(rect, tint);
            var labelRect = new Rect(rect.x + 6, rect.y + 2, rect.width - 12, rect.height - 4);
            GUI.Label(labelRect, text, Styles.BadgeLabel);
        }

        void DrawFilters()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(L.T("vc.filter.search", "Search"), GUILayout.Width(50));
            string newSearch = EditorGUILayout.TextField(_search ?? string.Empty, GUILayout.ExpandWidth(true));
            if (!string.Equals(newSearch, _search, StringComparison.Ordinal))
                _search = newSearch;
            if (GUILayout.Button(L.T("vc.filter.clear", "Clear"), GUILayout.Width(60)))
                _search = string.Empty;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            DrawFilterToggle(ref _showAdded, ColorAdded, L.T("vc.filter.added", "New"));
            DrawFilterToggle(ref _showModified, ColorModified, L.T("vc.filter.modified", "Changed"));
            DrawFilterToggle(ref _showRemoved, ColorRemoved, L.T("vc.filter.removed", "Removed"));
            GUILayout.Space(8);
            _showNamesOnly = GUILayout.Toggle(_showNamesOnly, L.T("vc.filter.namesonly", "File name only"), EditorStyles.miniButton, GUILayout.Width(140));
            _highlightSearch = GUILayout.Toggle(_highlightSearch, L.T("vc.filter.highlight", "Highlight hits"), EditorStyles.miniButton, GUILayout.Width(140));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        void DrawFilterToggle(ref bool value, Color tint, string label)
        {
            Rect rect = GUILayoutUtility.GetRect(StyleCache.FilterBadgeWidth, 20f, Styles.ToggleLabel, GUILayout.MaxWidth(StyleCache.FilterBadgeWidth));
            Color fill = value ? tint : new Color(tint.r, tint.g, tint.b, tint.a * 0.35f);
            EditorGUI.DrawRect(rect, fill);
            bool toggled = GUI.Toggle(rect, value, label, Styles.ToggleLabel);
            if (toggled != value) value = toggled;
        }

        void DrawChangeList()
        {
            var rows = BuildRows();
            if (rows.Count == 0)
            {
                EditorGUILayout.HelpBox(L.T("vc.none", "No recorded changes for this version."), MessageType.Info);
                return;
            }

            int visibleCount = 0;
            foreach (var row in rows)
                if (PassesFilters(row)) visibleCount++;

            if (visibleCount == 0)
            {
                EditorGUILayout.HelpBox(L.T("vc.none.filters", "Filters hide all entries. Adjust the toggles or search."), MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var row in rows)
            {
                if (!PassesFilters(row)) continue;
                DrawRow(row);
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawRow(ChangeRow row)
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 22f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, GetRowColor(row.Kind));

            var typeRect = new Rect(rect.x + 8, rect.y + 2, 90, rect.height - 4);
            GUI.Label(typeRect, GetKindLabel(row.Kind), Styles.KindLabel);

            var pathRect = new Rect(typeRect.xMax + 6, rect.y + 2, rect.width - 220, rect.height - 4);
            var displayPath = _showNamesOnly ? Path.GetFileName(row.Path) : row.Path;
            DrawHighlightedLabel(pathRect, displayPath ?? string.Empty, row.Path);

            var categoryRect = new Rect(rect.xMax - 110, rect.y + 2, 70, rect.height - 4);
            if (!string.IsNullOrEmpty(row.Category))
                GUI.Label(categoryRect, row.Category, Styles.CategoryLabel);

            if (row.Kind != ChangeKind.Removed)
            {
                var sizeRect = new Rect(rect.xMax - 40, rect.y + 2, 36, rect.height - 4);
                GUI.Label(sizeRect, FormatSize(row.Size), Styles.SizeLabel);
            }
        }

        void DrawHighlightedLabel(Rect rect, string displayText, string fullPath)
        {
            displayText ??= string.Empty;
            fullPath ??= displayText;
            if (_highlightSearch && !string.IsNullOrEmpty(_search))
            {
                int idx = displayText.IndexOf(_search, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var prefix = displayText.Substring(0, idx);
                    var match = displayText.Substring(idx, _search.Length);
                    var suffix = displayText.Substring(idx + _search.Length);
                    string rich = prefix + "<color=#FFD760><b>" + match + "</b></color>" + suffix;
                    GUI.Label(rect, new GUIContent(rich, fullPath), Styles.HighlightPathLabel);
                    return;
                }
            }
            GUI.Label(rect, new GUIContent(displayText, fullPath), Styles.PathLabel);
        }

        List<ChangeRow> BuildRows()
        {
            var rows = new List<ChangeRow>();
            if (_metadata.changedEntries != null)
            {
                foreach (var entry in _metadata.changedEntries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.relPath)) continue;
                    rows.Add(new ChangeRow
                    {
                        Kind = entry.isNew ? ChangeKind.Added : ChangeKind.Modified,
                        Path = entry.relPath,
                        Category = entry.category,
                        Size = Math.Max(0, entry.size)
                    });
                }
            }
            if (_metadata.removedEntries != null)
            {
                foreach (var removed in _metadata.removedEntries)
                {
                    if (string.IsNullOrEmpty(removed)) continue;
                    rows.Add(new ChangeRow
                    {
                        Kind = ChangeKind.Removed,
                        Path = removed,
                        Category = Categorize(removed),
                        Size = 0
                    });
                }
            }
            return rows
                .OrderBy(r => r.Kind)
                .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        bool PassesFilters(ChangeRow row)
        {
            if (!_showAdded && row.Kind == ChangeKind.Added) return false;
            if (!_showModified && row.Kind == ChangeKind.Modified) return false;
            if (!_showRemoved && row.Kind == ChangeKind.Removed) return false;
            if (!string.IsNullOrEmpty(_search))
            {
                if (row.Path == null || row.Path.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
            return true;
        }

        static string GetKindLabel(ChangeKind kind)
        {
            switch (kind)
            {
                case ChangeKind.Added: return L.T("vc.kind.added", "Added");
                case ChangeKind.Modified: return L.T("vc.kind.modified", "Modified");
                case ChangeKind.Removed: return L.T("vc.kind.removed", "Removed");
                default: return kind.ToString();
            }
        }

        static Color GetRowColor(ChangeKind kind)
        {
            switch (kind)
            {
                case ChangeKind.Added: return ColorAdded;
                case ChangeKind.Modified: return ColorModified;
                case ChangeKind.Removed: return ColorRemoved;
                default: return Color.gray;
            }
        }

        static string Categorize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "Other";
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".controller": return "Controller";
                case ".anim": return "Anim";
                case ".animator": return "Anim";
                case ".playable": return "Controller";
                case ".mat": return "Material";
                case ".shader": return "Shader";
                case ".prefab": return "Prefab";
                case ".unity": return "Scene";
                case ".asset":
                    return path.IndexOf("VRCExpression", StringComparison.OrdinalIgnoreCase) >= 0 ? "VRC Assets" : "Asset";
                default: return "Other";
            }
        }

        static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "-";
            double val = bytes;
            string[] units = { "B", "KB", "MB", "GB" };
            int idx = 0;
            while (val >= 1024 && idx < units.Length - 1)
            {
                val /= 1024;
                idx++;
            }
            return string.Format(idx == 0 ? "{0:0} {1}" : "{0:0.0} {1}", val, units[idx]);
        }

        static class StyleCache
        {
            public const float MiniBadgeWidth = 120f;
            public const float FilterBadgeWidth = 110f;
        }

        static class Styles
        {
            static GUIStyle _summaryLabel;
            static GUIStyle _badgeLabel;
            static GUIStyle _toggleLabel;
            static GUIStyle _kindLabel;
            static GUIStyle _pathLabel;
            static GUIStyle _highlightPathLabel;
            static GUIStyle _categoryLabel;
            static GUIStyle _sizeLabel;

            public static GUIStyle SummaryLabel => _summaryLabel ??= new GUIStyle(EditorStyles.label)
            {
                fontStyle = FontStyle.Bold,
                wordWrap = true
            };

            public static GUIStyle BadgeLabel => _badgeLabel ??= new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = EditorStyles.miniBoldLabel.normal.textColor }
            };

            public static GUIStyle ToggleLabel
            {
                get
                {
                    if (_toggleLabel == null)
                    {
                        _toggleLabel = new GUIStyle(EditorStyles.miniBoldLabel)
                        {
                            alignment = TextAnchor.MiddleCenter
                        };
                        _toggleLabel.normal.textColor = Color.white;
                        _toggleLabel.focused.textColor = Color.white;
                        _toggleLabel.active.textColor = Color.white;
                        _toggleLabel.hover.textColor = Color.white;
                    }
                    return _toggleLabel;
                }
            }

            public static GUIStyle KindLabel => _kindLabel ??= new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleLeft
            };

            public static GUIStyle PathLabel => _pathLabel ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft
            };

            public static GUIStyle HighlightPathLabel
            {
                get
                {
                    if (_highlightPathLabel == null)
                    {
                        _highlightPathLabel = new GUIStyle(PathLabel) { richText = true };
                    }
                    return _highlightPathLabel;
                }
            }

            public static GUIStyle CategoryLabel => _categoryLabel ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                fontStyle = FontStyle.Italic
            };

            public static GUIStyle SizeLabel => _sizeLabel ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight
            };
        }
    }
}
#endif





