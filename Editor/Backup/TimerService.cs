#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Threading;
using UnityEditor;

namespace AvatarSmartBackup
{
    [InitializeOnLoad]
    public static class TimerService
    {
        static readonly double UpdateEverySec = 0.5;
        static double _nextTick;
        static BackupSettings _cached;
        static double _nextReload;

        static TimerService()
        {
            EditorApplication.update += Update;
            var settings = BackupManager.LoadSettings();
            _ = BackupManager.EnsureBenchmarkAsync(settings);
            if (settings.autoRunOnLoad) StartTimerIfNeeded(settings);
            TryHookVRChat();
        }

        static BackupSettings GetSettingsCached()
        {
            if (_cached == null || EditorApplication.timeSinceStartup >= _nextReload)
            {
                _cached = BackupManager.LoadSettings();
                _nextReload = EditorApplication.timeSinceStartup + 10.0; // reload every 10s
            }
            return _cached;
        }
        public static void InvalidateSettingsCache() { _cached = null; _nextReload = 0; }

        static TimeSpan GetInterval(BackupSettings s)
        {
            double v = Math.Max(1, s.intervalMinutes);
            return s.debugMode ? TimeSpan.FromSeconds(v) : TimeSpan.FromMinutes(v);
        }

        public static void StartTimerIfNeeded(BackupSettings s)
        {
            if (!Session.IsRunning)
            {
                Session.IsRunning = true;
                ScheduleNext(DateTime.UtcNow + GetInterval(s));
                Log.Info("Timer started.");
            }
            else if (Session.NextRunUtc == null)
            {
                ScheduleNext(DateTime.UtcNow + GetInterval(s));
            }
        }
        public static void PauseTimer() { Session.IsRunning = false; Log.Info("Timer paused."); }
        static void ScheduleNext(DateTime utc) => Session.NextRunUtc = utc;
        public static void ScheduleNextRun(BackupSettings s) => ScheduleNext(DateTime.UtcNow + GetInterval(s));

        static void Update()
        {
            if (EditorApplication.timeSinceStartup < _nextTick) return;
            _nextTick = EditorApplication.timeSinceStartup + UpdateEverySec;

            var s = GetSettingsCached();
            if (!Session.IsRunning) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            var now = DateTime.UtcNow;
            var due = Session.NextRunUtc ?? now;
            if (now >= due)
            {
                if (BackupManager.IsBusy)
                {
                    BackupManager.QueuePendingRun();
                    ScheduleNext(now + TimeSpan.FromSeconds(1));
                }
                else
                {
                    // Schedule the next run BEFORE starting backup to avoid tight loop
                    ScheduleNext(now + GetInterval(s));
                    BackupManager.RunBackupNow(s, showToast: false, reason: "timer", showProgressUI: false);
                }
            }
        }

        [InitializeOnLoadMethod]
        static void HookPlaymodeShot()
        {
            EditorApplication.playModeStateChanged += (state) =>
            {
                var sNow = GetSettingsCached();
                if (state == PlayModeStateChange.ExitingEditMode && sNow.backupOnPlayEnter)
                {
                    bool forceZip = (sNow.zipPolicy == ZipPolicy.OnPlay);
                    BackupManager.RunBackupNow(sNow, showToast: false, reason: "play-enter", showProgressUI: false, forceZip: forceZip);
                }
            };
        }

        static void TryHookVRChat()
        {
            if (Session.HookedVRC) return;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t.FullName == null || t.FullName.IndexOf("VRC", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var ev = t.GetEvent("OnPreprocessAvatar", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (ev == null) continue;

                        Delegate del;
                        var et = ev.EventHandlerType;
                        var invoke = et.GetMethod("Invoke");
                        var pars = invoke.GetParameters();
                        if (pars.Length == 0)
                            del = Delegate.CreateDelegate(et, null, typeof(TimerService).GetMethod(nameof(OnVRC0), BindingFlags.NonPublic | BindingFlags.Static));
                        else if (pars.Length == 1)
                            del = Delegate.CreateDelegate(et, null, typeof(TimerService).GetMethod(nameof(OnVRC1), BindingFlags.NonPublic | BindingFlags.Static));
                        else
                            del = Delegate.CreateDelegate(et, null, typeof(TimerService).GetMethod(nameof(OnVRCPreprocessAvatar), BindingFlags.NonPublic | BindingFlags.Static));

                        ev.AddEventHandler(null, del);
                        Session.HookedVRC = true;
                        Log.Info("Hooked VRChat OnPreprocessAvatar on " + t.FullName);
                        return;
                    }
                }
            }
            catch (Exception ex) { Log.Warn("VRChat hook failed (fallback to timer/Play). " + ex.Message); }
        }

        static void OnVRC0() => BackupManager.RunBackupNow(GetSettingsCached(), showToast: false, reason: "vrchat-preprocess", showProgressUI: false);
        static void OnVRC1(object _) => BackupManager.RunBackupNow(GetSettingsCached(), showToast: false, reason: "vrchat-preprocess", showProgressUI: false);
        static void OnVRCPreprocessAvatar(object _a, object _b) => BackupManager.RunBackupNow(GetSettingsCached(), showToast: false, reason: "vrchat-preprocess", showProgressUI: false);
    }
}
#endif
