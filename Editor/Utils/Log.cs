#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace AvatarSmartBackup
{
    internal static class Log
    {
        const string Tag = "[ Avatar Backup System ] ";
        static readonly string FilePath = Path.Combine(FileUtilEx.BackupRoot, "asb.log");

        static void Write(string level, string msg)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { }
        }

        public static void Info(string msg)
        {
            UnityEngine.Debug.Log(Tag + msg);
            Write("INFO", msg);
        }

        public static void Warn(string msg)
        {
            UnityEngine.Debug.LogWarning(Tag + msg);
            Write("WARN", msg);
        }

        public static void Err(string msg)
        {
            UnityEngine.Debug.LogError(Tag + msg);
            Write("ERROR", msg);
        }
    }
}
#endif

