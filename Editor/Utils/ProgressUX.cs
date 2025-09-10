#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    internal static class ProgressUX
    {
        // Wrapper sullo UnityEditor.Progress (non modale). Fallback a nessuna UI se assente.
        static Type _progressType;
        static MethodInfo _start, _report, _finish, _registerCancel, _remove;
        static bool _checked;

        static void Ensure()
        {
            if (_checked) return;
            _checked = true;
            _progressType = Type.GetType("UnityEditor.Progress, UnityEditor");
            if (_progressType == null) return;
            _start = _progressType.GetMethod("Start", new[] { typeof(string), typeof(string), typeof(UnityEditor.Progress.Options) });
            if (_start == null) _start = _progressType.GetMethod("Start", new[] { typeof(string), typeof(string) });
            _report = _progressType.GetMethod("Report", new[] { typeof(int), typeof(float), typeof(string) });
            _finish = _progressType.GetMethod("Finish", new[] { typeof(int) });
            // Try both signatures found across Unity versions: Func<bool> and Action
            _registerCancel = _progressType.GetMethod("RegisterCancelCallback", new[] { typeof(int), typeof(Func<bool>) })
                                ?? _progressType.GetMethod("RegisterCancelCallback", new[] { typeof(int), typeof(Action) });
            _remove = _progressType.GetMethod("Remove", new[] { typeof(int) });
        }

        public static int Start(string title, string desc, bool cancellable, Func<bool> onCancel = null)
        {
            Ensure();
            if (_progressType == null) return -1;
            try
            {
                return MainThread.InvokeBlocking(() =>
                {
                    int id;
                    if (_start != null && _start.GetParameters().Length == 3)
                    {
                        var opts = (UnityEditor.Progress.Options)Enum.Parse(typeof(UnityEditor.Progress.Options),
                            cancellable ? "Managed" : "None");
                        id = (int)_start.Invoke(null, new object[] { title, desc, opts });
                    }
                    else if (_start != null)
                    {
                        id = (int)_start.Invoke(null, new object[] { title, desc });
                    }
                    else { return -1; }
                    if (onCancel != null && _registerCancel != null)
                    {
                        var pars = _registerCancel.GetParameters();
                        if (pars.Length == 2 && pars[1].ParameterType == typeof(Action))
                        {
                            Action act = () => onCancel();
                            _registerCancel.Invoke(null, new object[] { id, act });
                        }
                        else
                        {
                            _registerCancel.Invoke(null, new object[] { id, onCancel });
                        }
                    }
                    return id;
                });
            }
            catch { return -1; }
        }

        public static void Report(int id, float p, string desc)
        {
            if (id < 0) return;
            Ensure();
            try { MainThread.Invoke(() => _report?.Invoke(null, new object[] { id, Mathf.Clamp01(p), desc })); }
            catch { /* ignore */ }
        }

        public static void Finish(int id)
        {
            if (id < 0) return;
            Ensure();
            try { MainThread.Invoke(() => _finish?.Invoke(null, new object[] { id })); }
            catch { /* ignore */ }
        }

        public static void Remove(int id)
        {
            if (id < 0) return;
            Ensure();
            try { MainThread.Invoke(() => _remove?.Invoke(null, new object[] { id })); }
            catch { /* ignore */ }
        }
    }
}
#endif

