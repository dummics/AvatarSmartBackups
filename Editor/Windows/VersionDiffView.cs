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
    internal sealed class VersionDiffView
    {
        public sealed class State
        {
            public Vector2 Scroll;
            public string Search = string.Empty;
            public bool ShowAdded = true;
            public bool ShowModified = true;
            public bool ShowRemoved = true;
            public bool ShowNamesOnly = false;
            public bool HighlightSearch = true;
        }

        enum ChangeKind { Added, Modified, Removed }

        struct ChangeRow
        {
            public ChangeKind Kind;
            public string Path;
            public string Category;
            public long Size;
        }

        static readonly Color ColorAdded = new Color(0.19f, 0.55f, 0.28f, 0.25f);
        static readonly Color ColorModified = new Color(0.77f, 0.55f, 0.16f, 0.22f);
        static readonly Color ColorRemoved = new Color(0.75f, 0.25f, 0.25f, 0.22f);

        readonly List<ChangeRow> _buffer = new List<ChangeRow>();

        public void Draw(VersionInfo version, VersionDeltaMetadata metadata, State state)
        {
            if (version == null)
            {
                EditorGUILayout.HelpBox(L.T("vc.no.version", "No version data available."), MessageType.Info);
                return;
            }
            metadata ??= new VersionDeltaMetadata();

            DrawSummary(version, metadata);
            GUILayout.Space(4);
            DrawFilters(state);
            GUILayout.Space(6);
            DrawChangeList(metadata, state);
        }

        void DrawSummary(VersionInfo version, VersionDeltaMetadata metadata)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(string.Format(L.T("vc.version.header", "Version #{0}"), version.id), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(string.Format(L.T("vh.desc", "Description: {0}"), string.IsNullOrWhiteSpace(version.description) ? L.T("ui.overview.noDescription", "(no description)") : version.description), EditorStyles.miniLabel);

            var typeRect = EditorGUILayout.GetControlRect(false, 22f);
            EditorGUI.DrawRect(typeRect, version.isCheckpoint ? _Styles.ColorCheckpoint : _Styles.ColorIncremental);
            GUI.Label(new Rect(typeRect.x + 8, typeRect.y + 3, typeRect.width - 16, typeRect.height - 6),
                version.isCheckpoint ? L.T("vc.summary.checkpoint", "Full checkpoint (complete snapshot).") :
                string.Format(L.T("vc.summary.incremental", "Incremental version (based on checkpoint #{0})."), version.checkpointId > 0 ? version.checkpointId.ToString() : "--"),
                _Styles.SummaryLabel);

            EditorGUILayout.BeginHorizontal();
            if (version.changedFileCount > 0)
                DrawMiniBadge(_Styles.ColorModified, string.Format(L.T("vc.summary.changed", "Changed: {0}"), version.changedFileCount));
            if (version.removedFileCount > 0)
                DrawMiniBadge(_Styles.ColorRemoved, string.Format(L.T("vc.summary.removed", "Removed: {0}"), version.removedFileCount));
            if (metadata.changedBytes > 0)
                DrawMiniBadge(_Styles.ColorModified, string.Format(L.T("vc.summary.bytes.changed", "Delta: {0}"), FormatSize(metadata.changedBytes)));
            if (metadata.removedBytes > 0)
                DrawMiniBadge(_Styles.ColorRemoved, string.Format(L.T("vc.summary.bytes.removed", "Removed bytes: {0}"), FormatSize(metadata.removedBytes)));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        void DrawMiniBadge(Color tint, string text)
        {
            Rect rect = GUILayoutUtility.GetRect(StyleCache.MiniBadgeWidth, 20f, StyleCache.BadgeLabel, GUILayout.MaxWidth(StyleCache.MiniBadgeWidth));
            EditorGUI.DrawRect(rect, tint);
            GUI.Label(rect, text, StyleCache.BadgeLabel);
        }

        void DrawFilters(State state)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(L.T("vc.filter.search", "Search"), GUILayout.Width(50));
            EditorGUI.BeginChangeCheck();
            string newSearch = EditorGUILayout.TextField(state.Search ?? string.Empty, GUILayout.ExpandWidth(true));
            if (EditorGUI.EndChangeCheck())
                state.Search = newSearch;
            if (GUILayout.Button(L.T("vc.filter.clear", "Clear"), GUILayout.Width(60)))
                state.Search = string.Empty;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            DrawFilterToggle(ref state.ShowAdded, ColorAdded, L.T("vc.filter.added", "New"));
            DrawFilterToggle(ref state.ShowModified, ColorModified, L.T("vc.filter.modified", "Changed"));
            DrawFilterToggle(ref state.ShowRemoved, ColorRemoved, L.T("vc.filter.removed", "Removed"));
            GUILayout.Space(8);
            state.ShowNamesOnly = GUILayout.Toggle(state.ShowNamesOnly, L.T("vc.filter.namesonly", "File name only"), EditorStyles.miniButton, GUILayout.Width(140));
            state.HighlightSearch = GUILayout.Toggle(state.HighlightSearch, L.T("vc.filter.highlight", "Highlight hits"), EditorStyles.miniButton, GUILayout.Width(140));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        void DrawFilterToggle(ref bool value, Color tint, string label)
        {
            Rect rect = GUILayoutUtility.GetRect(StyleCache.FilterBadgeWidth, 20f, StyleCache.ToggleLabel, GUILayout.MaxWidth(StyleCache.FilterBadgeWidth));
            Color fill = value ? tint : new Color(tint.r, tint.g, tint.b, tint.a * 0.35f);
            EditorGUI.DrawRect(rect, fill);
            value = GUI.Toggle(rect, value, label, StyleCache.ToggleLabel);
        }

        void DrawChangeList(VersionDeltaMetadata metadata, State state)
        {
            BuildRows(metadata, state);
            state.Scroll = EditorGUILayout.BeginScrollView(state.Scroll);
            foreach (var row in _buffer)
            {
                DrawRow(row, state);
            }
            EditorGUILayout.EndScrollView();
        }

        void BuildRows(VersionDeltaMetadata metadata, State state)
        {
            _buffer.Clear();
            if (metadata.changedEntries != null)
            {
                foreach (var entry in metadata.changedEntries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.relPath)) continue;
                    var row = new ChangeRow
                    {
                        Kind = entry.isNew ? ChangeKind.Added : ChangeKind.Modified,
                        Path = entry.relPath,
                        Category = entry.category,
                        Size = Math.Max(0, entry.size)
                    };
                    if (PassesFilters(row, state))
                        _buffer.Add(row);
                }
            }
            if (metadata.removedEntries != null)
            {
                foreach (var removed in metadata.removedEntries)
                {
                    if (string.IsNullOrEmpty(removed)) continue;
                    var row = new ChangeRow
                    {
                        Kind = ChangeKind.Removed,
                        Path = removed,
                        Category = Categorize(removed),
                        Size = 0
                    };
                    if (PassesFilters(row, state))
                        _buffer.Add(row);
                }
            }
            _buffer.Sort((a, b) =>
            {
                int kind = a.Kind.CompareTo(b.Kind);
                if (kind != 0) return kind;
                return string.CompareOrdinal(a.Path, b.Path);
            });
        }

        void DrawRow(ChangeRow row, State state)
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 22f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, GetRowColor(row.Kind));

            var typeRect = new Rect(rect.x + 8, rect.y + 2, 90, rect.height - 4);
            GUI.Label(typeRect, GetKindLabel(row.Kind), StyleCache.KindLabel);

            var pathRect = new Rect(typeRect.xMax + 6, rect.y + 2, rect.width - 220, rect.height - 4);
            var displayPath = state.ShowNamesOnly ? Path.GetFileName(row.Path) : row.Path;
            DrawHighlightedLabel(pathRect, displayPath ?? string.Empty, row.Path, state);

            var categoryRect = new Rect(rect.xMax - 110, rect.y + 2, 70, rect.height - 4);
            if (!string.IsNullOrEmpty(row.Category))
                GUI.Label(categoryRect, row.Category, StyleCache.CategoryLabel);

            if (row.Kind != ChangeKind.Removed)
            {
                var sizeRect = new Rect(rect.xMax - 40, rect.y + 2, 36, rect.height - 4);
                GUI.Label(sizeRect, FormatSize(row.Size), StyleCache.SizeLabel);
            }
        }

        void DrawHighlightedLabel(Rect rect, string displayText, string fullPath, State state)
        {
            displayText ??= string.Empty;
            fullPath ??= displayText;
            if (state.HighlightSearch && !string.IsNullOrEmpty(state.Search))
            {
                int idx = displayText.IndexOf(state.Search, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var prefix = displayText.Substring(0, idx);
                    var match = displayText.Substring(idx, state.Search.Length);
                    var suffix = displayText.Substring(idx + state.Search.Length);
                    string rich = prefix + "<color=#FFD760><b>" + match + "</b></color>" + suffix;
                    GUI.Label(rect, new GUIContent(rich, fullPath), StyleCache.HighlightPathLabel);
                    return;
                }
            }
            GUI.Label(rect, new GUIContent(displayText, fullPath), StyleCache.PathLabel);
        }

        bool PassesFilters(ChangeRow row, State state)
        {
            if (!state.ShowAdded && row.Kind == ChangeKind.Added) return false;
            if (!state.ShowModified && row.Kind == ChangeKind.Modified) return false;
            if (!state.ShowRemoved && row.Kind == ChangeKind.Removed) return false;
            if (!string.IsNullOrEmpty(state.Search) && (row.Path == null || row.Path.IndexOf(state.Search, StringComparison.OrdinalIgnoreCase) < 0))
                return false;
            return true;
        }

        static Color GetRowColor(ChangeKind kind) => kind switch
        {
            ChangeKind.Added => ColorAdded,
            ChangeKind.Modified => ColorModified,
            ChangeKind.Removed => ColorRemoved,
            _ => Color.gray
        };

        static string GetKindLabel(ChangeKind kind) => kind switch
        {
            ChangeKind.Added => L.T("vc.kind.added", "Added"),
            ChangeKind.Modified => L.T("vc.kind.modified", "Modified"),
            ChangeKind.Removed => L.T("vc.kind.removed", "Removed"),
            _ => kind.ToString()
        };

        static string Categorize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "Other";
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".controller" => "Controller",
                ".anim" => "Anim",
                ".animator" => "Anim",
                ".playable" => "Controller",
                ".mat" => "Material",
                ".shader" => "Shader",
                ".prefab" => "Prefab",
                ".unity" => "Scene",
                ".asset" => path.IndexOf("VRCExpression", StringComparison.OrdinalIgnoreCase) >= 0 ? "VRC Assets" : "Asset",
                _ => "Other"
            };
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

            static GUIStyle _summaryLabel = null!;
            static GUIStyle _badgeLabel = null!;
            static GUIStyle _toggleLabel = null!;
            static GUIStyle _kindLabel = null!;
            static GUIStyle _pathLabel = null!;
            static GUIStyle _highlightPathLabel = null!;
            static GUIStyle _categoryLabel = null!;
            static GUIStyle _sizeLabel = null!;

            public static Color ColorCheckpoint { get; } = new Color(0.23f, 0.46f, 0.80f, 0.18f);
            public static Color ColorIncremental { get; } = new Color(0.45f, 0.35f, 0.78f, 0.18f);
            public static Color ColorModified { get; } = new Color(0.77f, 0.55f, 0.16f, 0.22f);
            public static Color ColorRemoved { get; } = new Color(0.75f, 0.25f, 0.25f, 0.22f);

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
                        _toggleLabel.richText = true;
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

        static class _Styles
        {
            public static readonly Color ColorCheckpoint = StyleCache.ColorCheckpoint;
            public static readonly Color ColorIncremental = StyleCache.ColorIncremental;
            public static readonly Color ColorModified = StyleCache.ColorModified;
            public static readonly Color ColorRemoved = StyleCache.ColorRemoved;
            public static GUIStyle SummaryLabel => StyleCache.SummaryLabel;
            public static GUIStyle BadgeLabel => StyleCache.BadgeLabel;
            public static GUIStyle ToggleLabel => StyleCache.ToggleLabel;
            public static GUIStyle KindLabel => StyleCache.KindLabel;
            public static GUIStyle PathLabel => StyleCache.PathLabel;
            public static GUIStyle HighlightPathLabel => StyleCache.HighlightPathLabel;
            public static GUIStyle CategoryLabel => StyleCache.CategoryLabel;
            public static GUIStyle SizeLabel => StyleCache.SizeLabel;
        }
    }
}
#endif
