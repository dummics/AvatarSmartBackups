#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup.Localization
{
    public enum Language
    {
        English = 0,
        Italian = 1,
    }

    public interface ILocalizationSource
    {
        bool TryGet(string key, Language lang, out string value);
        IEnumerable<string> Keys { get; }
    }

    public class DictionaryLocalizationSource : ILocalizationSource
    {
        readonly Dictionary<string, string> _en;
        readonly Dictionary<string, string> _it;

        public DictionaryLocalizationSource(Dictionary<string, string> en, Dictionary<string, string> it = null)
        {
            _en = en ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _it = it ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public bool TryGet(string key, Language lang, out string value)
        {
            if (lang == Language.Italian)
            {
                if (_it.TryGetValue(key, out value)) return true;
            }
            // Fallback to English
            return _en.TryGetValue(key, out value);
        }

        public IEnumerable<string> Keys
        {
            get
            {
                var hs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var k in _en.Keys) hs.Add(k);
                foreach (var k in _it.Keys) hs.Add(k);
                return hs;
            }
        }
    }

    public static class L
    {
        const string PrefKey = "AvatarSmartBackups.Language";
        static Language? _current;
        static readonly List<ILocalizationSource> _sources = new List<ILocalizationSource>();
        static bool _initialized;

        public static Language Current
        {
            get
            {
                if (_current.HasValue) return _current.Value;
                var val = EditorPrefs.GetInt(PrefKey, (int)Language.English);
                _current = (Language)Mathf.Clamp(val, 0, int.MaxValue);
                EnsureInitialized();
                return _current.Value;
            }
            set
            {
                if (_current == value) return;
                _current = value;
                EditorPrefs.SetInt(PrefKey, (int)value);
                RepaintAllWindows();
            }
        }

        public static void AddSource(ILocalizationSource source)
        {
            if (source == null) return;
            if (!_sources.Contains(source)) _sources.Add(source);
        }

        static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;
            // Seed with a tiny in-code dictionary so the system works out of the box
            var en = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {"menu.language.english", "English"},
                {"menu.language.italian", "Italian"},
                {"window.version.history.help", "Versions are of two types: full checkpoints and incremental. Incremental versions store only changes since the previous checkpoint."},
            };
            var it = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {"menu.language.english", "Inglese"},
                {"menu.language.italian", "Italiano"},
                {"window.version.history.help", "Le versioni sono di due tipi: checkpoint completi e incrementali. Le versioni incrementali contengono solo i cambiamenti rispetto al checkpoint precedente."},
            };
            AddSource(new DictionaryLocalizationSource(en, it));
        }

        public static string T(string key, string fallback = null, params object[] args)
        {
            if (string.IsNullOrEmpty(key)) return fallback ?? string.Empty;
            EnsureInitialized();

            string text = null;
            foreach (var s in _sources)
            {
                if (s.TryGet(key, Current, out text)) break;
            }
            if (string.IsNullOrEmpty(text))
            {
                text = fallback ?? key;
#if UNITY_EDITOR
                if (!Application.isBatchMode)
                    Debug.LogWarning($"[ASB] Missing localization for key '{key}' (lang: {Current}). Using fallback: '{text}'.");
#endif
            }
            if (args != null && args.Length > 0)
            {
                try { text = string.Format(text, args); }
                catch (FormatException) { /* ignore formatting errors */ }
            }
            return text;
        }

        public static GUIContent C(string key, string fallback = null)
        {
            return new GUIContent(T(key, fallback));
        }

        public static GUIContent C(string key, string fallback, string tooltipKey, string tooltipFallback = null)
        {
            return new GUIContent(T(key, fallback), T(tooltipKey, tooltipFallback));
        }

        static void RepaintAllWindows()
        {
            // Repaint all editor windows so strings update immediately after language switch
            var wins = Resources.FindObjectsOfTypeAll<EditorWindow>();
            foreach (var w in wins) w.Repaint();
        }
    }
}
#endif
