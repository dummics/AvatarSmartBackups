#if UNITY_EDITOR
using System;
using UnityEditor;

namespace AvatarSmartBackup
{
    internal sealed class ASB_AssetWatcher : AssetPostprocessor
    {
        static double _lastSignalTime;
        const double DebounceSeconds = 0.5;
        static bool _pending;

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            // Se nessuna finestra restore aperta evitare lavoro
            if (!RestorePreviewWindowIsOpen()) return;
            if (!Relevant(imported) && !Relevant(deleted) && !Relevant(moved) && !Relevant(movedFrom)) return;
            _pending = true; _lastSignalTime = EditorApplication.timeSinceStartup;
            EditorApplication.update -= Tick; // evita duplicati
            EditorApplication.update += Tick;
        }

        static bool RestorePreviewWindowIsOpen()
        {
            var wins = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEditor.EditorWindow>();
            for (int i = 0; i < wins.Length; i++)
            {
                if (wins[i].GetType().Name == "RestorePreviewWindow") return true;
            }
            return false;
        }

        static bool Relevant(string[] arr)
        {
            if (arr == null || arr.Length == 0) return false;
            for (int i=0;i<arr.Length;i++)
            {
                var p = arr[i];
                if (p.EndsWith(".controller", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static void Tick()
        {
            if (!_pending) { EditorApplication.update -= Tick; return; }
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastSignalTime < DebounceSeconds) return;
            _pending = false; EditorApplication.update -= Tick;
            // Trova finestre e invoca refresh soft (metodo riflessione per non dipendere da internal)
            var wins = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEditor.EditorWindow>();
            foreach (var w in wins)
            {
                if (w.GetType().Name == "RestorePreviewWindow")
                {
                    var m = w.GetType().GetMethod("RequestRescan", System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
                    m?.Invoke(w, null);
                }
            }
        }
    }
}
#endif
