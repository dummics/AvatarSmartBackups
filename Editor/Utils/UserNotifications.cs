#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Simple notification system for user feedback
    /// Designed to be non-intrusive and user-friendly
    /// </summary>
    public static class UserNotifications
    {
        /// <summary>
        /// Show a success notification in the editor
        /// </summary>
        public static void ShowSuccess(string message, string title = "Success")
        {
            if (EditorWindow.focusedWindow != null)
            {
                EditorWindow.focusedWindow.ShowNotification(new GUIContent($"✅ {message}"));
            }
            Debug.Log($"✅ {title}: {message}");
        }

        /// <summary>
        /// Show an info notification in the editor
        /// </summary>
        public static void ShowInfo(string message, string title = "Info")
        {
            if (EditorWindow.focusedWindow != null)
            {
                EditorWindow.focusedWindow.ShowNotification(new GUIContent($"ℹ️ {message}"));
            }
            Debug.Log($"ℹ️ {title}: {message}");
        }

        /// <summary>
        /// Show a warning notification in the editor
        /// </summary>
        public static void ShowWarning(string message, string title = "Warning")
        {
            if (EditorWindow.focusedWindow != null)
            {
                EditorWindow.focusedWindow.ShowNotification(new GUIContent($"⚠️ {message}"));
            }
            Debug.LogWarning($"⚠️ {title}: {message}");
        }

        /// <summary>
        /// Show a toast-style message with automatic dismiss
        /// </summary>
        public static void ShowToast(string message)
        {
            ShowInfo(message);
            
            // Auto-dismiss after 3 seconds
            EditorApplication.delayCall += () =>
            {
                if (EditorWindow.focusedWindow != null)
                {
                    EditorWindow.focusedWindow.RemoveNotification();
                }
            };
        }

        /// <summary>
        /// Show a confirmation dialog with better UX
        /// </summary>
        public static bool ShowConfirmation(string title, string message, string confirmButton = "Yes", string cancelButton = "No")
        {
            return EditorUtility.DisplayDialog(title, message, confirmButton, cancelButton);
        }

        /// <summary>
        /// Show a three-option dialog
        /// </summary>
        public static int ShowTripleChoice(string title, string message, string option1, string option2, string option3)
        {
            return EditorUtility.DisplayDialogComplex(title, message, option1, option2, option3);
        }
    }
}
#endif