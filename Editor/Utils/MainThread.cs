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

        public static void Invoke(Action a)
        {
            if (Thread.CurrentThread.ManagedThreadId == MainId) a();
            else EditorApplication.delayCall += () => a();
        }

        public static T InvokeBlocking<T>(Func<T> f)
        {
            if (Thread.CurrentThread.ManagedThreadId == MainId) return f();
            T result = default;
            var ev = new ManualResetEventSlim();
            EditorApplication.delayCall += () => { result = f(); ev.Set(); };
            ev.Wait();
            return result;
        }
    }
}
#endif

