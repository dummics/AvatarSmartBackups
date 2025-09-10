#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    // Tracks editor activity (input, compilation, playmode) to detect idle periods
    internal static class EditorIdle
    {
        static double _lastActivity = EditorApplication.timeSinceStartup;

        static EditorIdle()
        {
            EditorApplication.update += Update;
        }

        static void Update()
        {
            if (UnityEngine.Input.anyKey ||
                UnityEngine.Input.GetMouseButton(0) ||
                UnityEngine.Input.GetMouseButton(1) ||
                UnityEngine.Input.GetMouseButton(2) ||
                EditorApplication.isCompiling ||
                EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                _lastActivity = EditorApplication.timeSinceStartup;
            }
        }

        public static double TimeSinceLastActivity => EditorApplication.timeSinceStartup - _lastActivity;
    }
}
#endif

