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
    }

    internal static class Session
    {
        public static bool IsRunning { get => SessionState.GetBool(SessionKeys.IsRunning, false); set => SessionState.SetBool(SessionKeys.IsRunning, value); }
        public static DateTime? NextRunUtc { get => GetDT(SessionKeys.NextRunUtc); set => SetDT(SessionKeys.NextRunUtc, value); }
        public static DateTime? LastBackupUtc { get => GetDT(SessionKeys.LastBackupUtc); set => SetDT(SessionKeys.LastBackupUtc, value); }
        public static int RunsCount { get => SessionState.GetInt(SessionKeys.RunsCount, 0); set => SessionState.SetInt(SessionKeys.RunsCount, value); }
        public static bool HookedVRC { get => SessionState.GetBool(SessionKeys.HookedVRC, false); set => SessionState.SetBool(SessionKeys.HookedVRC, value); }

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
