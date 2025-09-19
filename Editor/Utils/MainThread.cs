#if UNITY_EDITOR
using System;
using System.Threading;
using UnityEditor;

namespace AvatarSmartBackup
{
    [InitializeOnLoad]
    internal static class MainThread
    {
        static readonly int MainId;

        static MainThread()
        {
            MainId = Thread.CurrentThread.ManagedThreadId;
        }

        public static void Invoke(Action? action)
        {
            if (action == null) return;
            if (Thread.CurrentThread.ManagedThreadId == MainId) action();
            else EditorApplication.delayCall += () => action();
        }

        public static T InvokeBlocking<T>(Func<T> func)
        {
            if (func == null) return default;
            if (Thread.CurrentThread.ManagedThreadId == MainId) return func();
            T result = default;
            var ev = new ManualResetEventSlim();
            EditorApplication.delayCall += () =>
            {
                result = func();
                ev.Set();
            };
            ev.Wait();
            return result;
        }
    }
}
#endif

