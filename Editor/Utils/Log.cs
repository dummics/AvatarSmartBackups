#if UNITY_EDITOR
using System;
using System.IO;
using System.Globalization;
using UnityEngine;

namespace AvatarSmartBackup
{
    internal static class Log
    {
        const string Tag = "[ Avatar Backup System ] ";
        static readonly string LogDir = Path.Combine(FileUtilEx.BackupRoot, "Logs");
        static readonly string CurrentLogFile = Path.Combine(LogDir, "asb.log");
        
        // Log rotation settings
        const long MaxLogSizeBytes = 2 * 1024 * 1024; // 2MB per file
        const int MaxLogFiles = 5; // Keep last 5 log files
        
        public enum Level
        {
            Debug = 0, 
            Info = 1, 
            Warn = 2,
            Error = 3
        }

        static void WriteToFile(Level level, string msg, Exception ex = null)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                
                // Check if current log file needs rotation
                if (File.Exists(CurrentLogFile) && new FileInfo(CurrentLogFile).Length > MaxLogSizeBytes)
                {
                    RotateLogFiles();
                }
                
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                string line = $"{timestamp} [{level.ToString().ToUpperInvariant()}] {msg}";
                
                if (ex != null)
                {
                    line += $"{Environment.NewLine}Exception: {ex.GetType().Name}: {ex.Message}";
                    if (!string.IsNullOrEmpty(ex.StackTrace))
                        line += $"{Environment.NewLine}StackTrace: {ex.StackTrace}";
                }
                
                File.AppendAllText(CurrentLogFile, line + Environment.NewLine);
            }
            catch { /* Silent fail for logging system itself */ }
        }
        
        static void RotateLogFiles()
        {
            try
            {
                // Move current log to backup with timestamp
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string backupPath = Path.Combine(LogDir, $"asb_{timestamp}.log");
                
                if (File.Exists(CurrentLogFile))
                    File.Move(CurrentLogFile, backupPath);
                
                // Clean up old log files
                var logFiles = Directory.GetFiles(LogDir, "asb_*.log");
                if (logFiles.Length > MaxLogFiles)
                {
                    Array.Sort(logFiles); // Sort by name (which includes timestamp)
                    for (int i = 0; i < logFiles.Length - MaxLogFiles; i++)
                    {
                        try { File.Delete(logFiles[i]); } catch { }
                    }
                }
            }
            catch { /* Silent fail for log rotation */ }
        }

        static bool ShouldLogToFile(Level level)
        {
            // Evita chiamate dirette a EditorPrefs da thread secondari
            var settings = SafeGetSettings();
            
            // Always log Warn and Error to file
            if (level >= Level.Warn) return true;
            
            // Log Debug and Info only if debug logging is enabled
            if (settings.enableDebugLogging) return true;
            
            return false;
        }

        // Console logging - minimal and user-friendly
        static void LogToConsole(Level level, string userMessage)
        {
            string consoleMsg = Tag + userMessage;
            
            switch (level)
            {
                case Level.Debug:
                case Level.Info:
                    UnityEngine.Debug.Log(consoleMsg);
                    break;
                case Level.Warn:
                    UnityEngine.Debug.LogWarning(consoleMsg);
                    break;
                case Level.Error:
                    UnityEngine.Debug.LogError(consoleMsg);
                    break;
            }
        }

        // Public API - simplified console messages, detailed file logging
        public static void Debug(string detailedMsg, string consoleMsg = null)
        {
            if (ShouldLogToFile(Level.Debug))
                WriteToFile(Level.Debug, detailedMsg);
            
            // Only show in console if debug mode is enabled
            var settings = SafeGetSettings();
            if (settings.debugMode && !string.IsNullOrEmpty(consoleMsg))
                LogToConsole(Level.Debug, consoleMsg);
        }

        public static void Info(string detailedMsg, string consoleMsg = null)
        {
            if (ShouldLogToFile(Level.Info))
                WriteToFile(Level.Info, detailedMsg);
            
            if (!string.IsNullOrEmpty(consoleMsg))
                LogToConsole(Level.Info, consoleMsg);
        }

        public static void Warn(string detailedMsg, string consoleMsg = null)
        {
            WriteToFile(Level.Warn, detailedMsg);
            LogToConsole(Level.Warn, consoleMsg ?? detailedMsg);
        }

        public static void Error(string detailedMsg, string consoleMsg = null, Exception ex = null)
        {
            WriteToFile(Level.Error, detailedMsg, ex);
            LogToConsole(Level.Error, consoleMsg ?? detailedMsg);
        }

        // Legacy API compatibility (will be updated in Task 3)
        public static void Info(string msg) => Info(msg, msg);
        public static void Warn(string msg) => Warn(msg, msg);  
        public static void Err(string msg) => Error(msg, msg);
        
        // Utility methods
        public static string GetLogDirectory() => LogDir;
        public static string GetCurrentLogFile() => CurrentLogFile;

        // Thread-safe cached access alle settings per logging
        static BackupSettings _cachedSettings;
        static DateTime _lastSettingsFetch;
        static readonly TimeSpan SettingsCacheLifetime = TimeSpan.FromSeconds(5);
        static readonly object _settingsLock = new object();

        static BackupSettings SafeGetSettings()
        {
            try
            {
                lock (_settingsLock)
                {
                    if (_cachedSettings != null && (DateTime.UtcNow - _lastSettingsFetch) < SettingsCacheLifetime)
                        return _cachedSettings;
                }
                // Eseguiamo la fetch sul main thread per sicurezza
                var s = MainThread.InvokeBlocking(() => BackupManager.LoadSettings());
                lock (_settingsLock)
                {
                    _cachedSettings = s;
                    _lastSettingsFetch = DateTime.UtcNow;
                }
                return s;
            }
            catch { return new BackupSettings(); }
        }
    }
}
#endif

