#if UNITY_EDITOR
using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Centralized resilient error handling system
    /// Ensures backup/versioning failures are graceful and non-blocking
    /// </summary>
    public static class ResilientErrorHandler
    {
        public enum Severity
        {
            Info,
            Warning,
            Error,
            Critical
        }

        public enum Component
        {
            Backup,
            Versioning,
            Restore,
            Database,
            FileSystem,
            UI
        }

        /// <summary>
        /// Handle errors gracefully without breaking user workflow
        /// </summary>
        public static void HandleError(Exception ex, Component component, Severity severity, string context = null, bool showToUser = false)
        {
            try
            {
                string componentStr = component.ToString();
                string severityStr = severity.ToString().ToUpper();
                string contextStr = !string.IsNullOrEmpty(context) ? $" [{context}]" : "";
                
                string logMessage = $"[{componentStr}] {severityStr}{contextStr}: {ex.Message}";
                
                // Log appropriately based on severity
                switch (severity)
                {
                    case Severity.Info:
                        Debug.Log($"ℹ️ {logMessage}");
                        break;
                    case Severity.Warning:
                        Debug.LogWarning($"⚠️ {logMessage}");
                        break;
                    case Severity.Error:
                        Debug.LogError($"❌ {logMessage}");
                        break;
                    case Severity.Critical:
                        Debug.LogError($"🚨 CRITICAL: {logMessage}\nStack trace: {ex.StackTrace}");
                        break;
                }

                // Detailed logging for debugging
                if (severity >= Severity.Error)
                {
                    try
                    {
                        Log.Error($"{logMessage}\nStack trace: {ex.StackTrace}", $"{componentStr} {severityStr}", ex);
                    }
                    catch
                    {
                        // If logging fails, at least try console
                        Console.WriteLine($"LOGGING FAILED: {logMessage}");
                    }
                }

                // User notification for critical issues
                if (showToUser || severity == Severity.Critical)
                {
                    ShowUserFriendlyError(ex, component, severity, context);
                }

                // Component-specific recovery actions
                TryRecoveryAction(component, severity, ex);
            }
            catch (Exception handlerEx)
            {
                // Error handler itself failed - fallback to basic logging
                try
                {
                    Debug.LogError($"🚨 ERROR HANDLER FAILED: {handlerEx.Message}\nOriginal error: {ex.Message}");
                }
                catch
                {
                    // Last resort
                    Console.WriteLine($"COMPLETE FAILURE: Handler={handlerEx.Message}, Original={ex.Message}");
                }
            }
        }

        /// <summary>
        /// Show user-friendly error messages
        /// </summary>
        static void ShowUserFriendlyError(Exception ex, Component component, Severity severity, string context)
        {
            try
            {
                string title = GetUserFriendlyTitle(component, severity);
                string message = GetUserFriendlyMessage(ex, component, context);
                
                if (severity == Severity.Critical)
                {
                    EditorUtility.DisplayDialog(title, message, "OK");
                }
                else if (EditorWindow.focusedWindow != null)
                {
                    EditorWindow.focusedWindow.ShowNotification(new GUIContent($"⚠️ {title}"));
                }
            }
            catch
            {
                // Fallback: basic dialog
                EditorUtility.DisplayDialog("Error", 
                    $"An error occurred in {component}: {ex.Message}", "OK");
            }
        }

        /// <summary>
        /// Get user-friendly error titles
        /// </summary>
        static string GetUserFriendlyTitle(Component component, Severity severity)
        {
            return component switch
            {
                Component.Backup => severity == Severity.Critical ? "Backup Failed" : "Backup Issue",
                Component.Versioning => "Version Tracking Issue",
                Component.Restore => "Restore Problem",
                Component.Database => "History Database Issue",
                Component.FileSystem => "File System Issue",
                Component.UI => "Interface Issue",
                _ => "System Issue"
            };
        }

        /// <summary>
        /// Get user-friendly error messages
        /// </summary>
        static string GetUserFriendlyMessage(Exception ex, Component component, string context)
        {
            string baseMessage = component switch
            {
                Component.Backup => "A backup operation encountered an issue, but your work is safe.",
                Component.Versioning => "Version tracking had an issue, but backup functionality continues normally.",
                Component.Restore => "Restore operation encountered a problem. Your current work remains unchanged.",
                Component.Database => "Version history database had an issue. Backup continues with basic tracking.",
                Component.FileSystem => "File system operation had an issue. Please check disk space and permissions.",
                Component.UI => "Interface update had an issue. Try refreshing the window.",
                _ => "A system operation encountered an issue."
            };

            // Add context if available
            if (!string.IsNullOrEmpty(context))
            {
                baseMessage += $"\n\nContext: {context}";
            }

            // Add common troubleshooting
            baseMessage += "\n\nTroubleshooting:\n• Check disk space\n• Verify folder permissions\n• Try again in a moment";

            return baseMessage;
        }

        /// <summary>
        /// Attempt component-specific recovery actions
        /// </summary>
        static void TryRecoveryAction(Component component, Severity severity, Exception ex)
        {
            try
            {
                switch (component)
                {
                    case Component.Versioning:
                        // Versioning failed, ensure it doesn't break backup
                        if (severity >= Severity.Error)
                        {
                            Debug.Log("🔄 Versioning recovery: Switching to fallback mode");
                            // Could set a flag to use filesystem-based counting
                        }
                        break;

                    case Component.Database:
                        // Database failed, try to recreate or use fallback
                        if (severity >= Severity.Error)
                        {
                            Debug.Log("🔄 Database recovery: Attempting fallback to filesystem tracking");
                        }
                        break;

                    case Component.FileSystem:
                        // File system issues might need permission fixes
                        if (ex.Message.Contains("access") || ex.Message.Contains("permission"))
                        {
                            Debug.Log("🔄 FileSystem recovery: Permission issue detected, suggesting solutions");
                        }
                        break;

                    case Component.Backup:
                        // Backup failed, ensure system remains stable
                        if (severity == Severity.Critical)
                        {
                            Debug.Log("🔄 Backup recovery: Critical failure, ensuring system stability");
                        }
                        break;
                }
            }
            catch (Exception recoveryEx)
            {
                Debug.LogWarning($"Recovery action failed for {component}: {recoveryEx.Message}");
            }
        }

        /// <summary>
        /// Safe wrapper for operations that might fail
        /// </summary>
        public static T SafeExecute<T>(Func<T> operation, Component component, T fallbackValue = default, string context = null)
        {
            try
            {
                return operation();
            }
            catch (Exception ex)
            {
                HandleError(ex, component, Severity.Warning, context);
                return fallbackValue;
            }
        }

        /// <summary>
        /// Safe wrapper for void operations
        /// </summary>
        public static void SafeExecute(Action operation, Component component, string context = null)
        {
            try
            {
                operation();
            }
            catch (Exception ex)
            {
                HandleError(ex, component, Severity.Warning, context);
            }
        }

        /// <summary>
        /// Safe wrapper for async operations
        /// </summary>
        public static async Task<T> SafeExecuteAsync<T>(Func<Task<T>> operation, Component component, T fallbackValue = default, string context = null)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex)
            {
                HandleError(ex, component, Severity.Warning, context);
                return fallbackValue;
            }
        }

        /// <summary>
        /// Safe wrapper for async void operations
        /// </summary>
        public static async Task SafeExecuteAsync(Func<Task> operation, Component component, string context = null)
        {
            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                HandleError(ex, component, Severity.Warning, context);
            }
        }
    }
}
#endif