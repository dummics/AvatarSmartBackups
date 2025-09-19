#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading.Tasks;

#nullable enable

namespace AvatarSmartBackup
{
    /// <summary>
    /// Centralized exception handling and logging system
    /// Provides consistent error handling across the entire backup system
    /// </summary>
    internal static class ExceptionHandler
    {
        /// <summary>
        /// Execute an action with automatic exception handling and logging
        /// </summary>
        public static void SafeExecute(Action action, string operationName, string? userFriendlyMessage = null)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                HandleException(ex, operationName, userFriendlyMessage!);
            }
        }

        /// <summary>
        /// Execute a function with automatic exception handling and logging
        /// Returns default(T) if an exception occurs
        /// </summary>
        public static T SafeExecute<T>(Func<T> func, string operationName, string? userFriendlyMessage = null, T defaultValue = default!)
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                HandleException(ex, operationName, userFriendlyMessage!);
                return defaultValue!;
            }
        }

        /// <summary>
        /// Execute an async action with automatic exception handling and logging
        /// </summary>
        public static async Task SafeExecuteAsync(Func<Task> asyncAction, string operationName, string? userFriendlyMessage = null)
        {
            try
            {
                await asyncAction();
            }
            catch (Exception ex)
            {
                HandleException(ex, operationName, userFriendlyMessage!);
            }
        }

        /// <summary>
        /// Execute an async function with automatic exception handling and logging
        /// Returns default(T) if an exception occurs
        /// </summary>
        public static async Task<T> SafeExecuteAsync<T>(Func<Task<T>> asyncFunc, string operationName, string? userFriendlyMessage = null, T defaultValue = default!)
        {
            try
            {
                return await asyncFunc();
            }
            catch (Exception ex)
            {
                HandleException(ex, operationName, userFriendlyMessage!);
                return defaultValue!;
            }
        }

        /// <summary>
        /// Central exception handling logic
        /// </summary>
        private static void HandleException(Exception ex, string operationName, string userFriendlyMessage)
        {
            // Detailed technical message for log file
            string detailedMessage = $"Operation '{operationName}' failed: {ex.Message}";
            
            // User-friendly message for console (fallback to operation name if not provided)
            string consoleMessage = userFriendlyMessage ?? $"Operation failed: {operationName}";
            
            // Add suggestion to check log file
            if (!string.IsNullOrEmpty(userFriendlyMessage))
                consoleMessage += " (see log file for details)";

            Log.Error(detailedMessage, consoleMessage, ex);
        }

        /// <summary>
        /// Handle specific file operation exceptions with better user messaging
        /// </summary>
        public static void HandleFileException(Exception ex, string filePath, string operation)
        {
            string fileName = System.IO.Path.GetFileName(filePath);
            string detailedMessage = $"File operation '{operation}' failed for {filePath}: {ex.Message}";
            string userMessage = $"File operation failed: {fileName}";
            
            // Provide specific guidance for common file errors
            if (ex is UnauthorizedAccessException)
                userMessage += " (file may be locked or read-only)";
            else if (ex is DirectoryNotFoundException || ex is FileNotFoundException)
                userMessage += " (file or folder not found)";
            else if (ex is IOException)
                userMessage += " (file in use or disk full)";
            
            Log.Error(detailedMessage, userMessage, ex);
        }

        /// <summary>
        /// Handle backup operation exceptions with context
        /// </summary>
        public static void HandleBackupException(Exception ex, string phase, int filesProcessed = -1)
        {
            string detailedMessage = $"Backup failed during {phase}: {ex.Message}";
            if (filesProcessed >= 0)
                detailedMessage += $" (processed {filesProcessed} files before failure)";
            
            string userMessage = $"Backup failed during {phase}";
            
            Log.Error(detailedMessage, userMessage, ex);
        }
    }
}
#endif
