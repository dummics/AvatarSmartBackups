#if UNITY_EDITOR
using System;
using UnityEditor;

namespace AvatarSmartBackup
{
    internal static class SessionKeys
    {
        public const string IsRunning = "ASB/IsRunning";
        public const string NextRunUtc = "ASB/NextRunUtcTicks";
        public const string LastBackupUtc = "ASB/LastBackupUtcTicks";
        public const string RunsCount = "ASB/RunsCount";
        public const string HookedVRC = "ASB/HookedVRC";
        
        // New reliability keys
        public const string InBackup = "ASB/InBackup";
        public const string InBenchmark = "ASB/InBenchmark";
        public const string LastBenchmark = "ASB/LastBenchmark";
        public const string LastManualSnapshot = "ASB/LastManualSnapshot";
    }

    internal static class Session
    {
        public static bool IsRunning { get => SessionState.GetBool(SessionKeys.IsRunning, false); set => SessionState.SetBool(SessionKeys.IsRunning, value); }
        public static DateTime? NextRunUtc { get => GetDT(SessionKeys.NextRunUtc); set => SetDT(SessionKeys.NextRunUtc, value); }
        public static DateTime? LastBackupUtc { get => GetDT(SessionKeys.LastBackupUtc); set => SetDT(SessionKeys.LastBackupUtc, value); }
        public static int RunsCount { get => SessionState.GetInt(SessionKeys.RunsCount, 0); set => SessionState.SetInt(SessionKeys.RunsCount, value); }
        public static bool HookedVRC { get => SessionState.GetBool(SessionKeys.HookedVRC, false); set => SessionState.SetBool(SessionKeys.HookedVRC, value); }

        // Reliability improvements: Thread-safe session management
        private static object lockObject = new object();
        
        public static bool InBackup 
        { 
            get => SessionState.GetBool(SessionKeys.InBackup, false);
            set 
            {
                lock (lockObject)
                {
                    SessionState.SetBool(SessionKeys.InBackup, value);
                    if (value)
                        Log.Debug($"Session: Backup started at {DateTime.Now:HH:mm:ss}");
                    else
                        Log.Debug($"Session: Backup completed at {DateTime.Now:HH:mm:ss}");
                }
            }
        }

        public static bool InBenchmark 
        { 
            get => SessionState.GetBool(SessionKeys.InBenchmark, false);
            set 
            {
                lock (lockObject)
                {
                    SessionState.SetBool(SessionKeys.InBenchmark, value);
                    if (value)
                        Log.Debug($"Session: Benchmark started at {DateTime.Now:HH:mm:ss}");
                    else
                        Log.Debug($"Session: Benchmark completed at {DateTime.Now:HH:mm:ss}");
                }
            }
        }

        public static DateTime LastBenchmark
        {
            get
            {
                var dt = GetDT(SessionKeys.LastBenchmark);
                return dt?.ToLocalTime() ?? DateTime.MinValue;
            }
            set
            {
                lock (lockObject)
                {
                    SetDT(SessionKeys.LastBenchmark, value.ToUniversalTime());
                }
            }
        }

        public static DateTime LastManualSnapshot
        {
            get
            {
                var dt = GetDT(SessionKeys.LastManualSnapshot);
                return dt?.ToLocalTime() ?? DateTime.MinValue;
            }
            set
            {
                lock (lockObject)
                {
                    SetDT(SessionKeys.LastManualSnapshot, value.ToUniversalTime());
                }
            }
        }

        // Cleanup session state when Unity starts - prevents phantom locks
        [InitializeOnLoadMethod]
        private static void ResetSessionOnLoad()
        {
            Log.Debug("Session: Resetting session state on Unity load");
            InBackup = false;
            InBenchmark = false;
        }

        // Safe atomic check-and-set operations for reliability
        public static bool TryStartBackup()
        {
            lock (lockObject)
            {
                if (InBackup)
                {
                    Log.Debug("Session: Backup already in progress");
                    return false;
                }
                InBackup = true;
                return true;
            }
        }

        public static bool TryStartBenchmark()
        {
            lock (lockObject)
            {
                if (InBenchmark || InBackup)
                {
                    Log.Debug("Session: Benchmark blocked by ongoing operation");
                    return false;
                }
                InBenchmark = true;
                return true;
            }
        }

        public static void EndBackup()
        {
            lock (lockObject)
            {
                InBackup = false;
            }
        }

        public static void EndBenchmark()
        {
            lock (lockObject)
            {
                InBenchmark = false;
                LastBenchmark = DateTime.Now;
            }
        }

        static DateTime? GetDT(string k)
        {
            var s = SessionState.GetString(k, "");
            if (string.IsNullOrEmpty(s)) return null;
            if (long.TryParse(s, out var ticks) && ticks > 0) return new DateTime(ticks, DateTimeKind.Utc);
            if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            return null;
        }
        static void SetDT(string k, DateTime? v) => SessionState.SetString(k, v.HasValue ? v.Value.ToString("o") : "");
    }
}
#endif
