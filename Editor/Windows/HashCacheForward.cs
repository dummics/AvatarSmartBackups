#if UNITY_EDITOR
using System;
using System.Reflection;

namespace AvatarSmartBackup
{
    internal static class HashCacheForward
    {
        static MethodInfo _mi;
        static bool _tried;

        static void Ensure()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                var t = Type.GetType("AvatarSmartBackup.HashCache, Assembly-CSharp-Editor", throwOnError:false)
                        ?? Type.GetType("AvatarSmartBackup.HashCache", throwOnError:false);
                if (t != null)
                    _mi = t.GetMethod("GetOrCompute", BindingFlags.Public|BindingFlags.Static);
            }
            catch { }
        }

        public static string Get(string abs)
        {
            Ensure();
            try
            {
                if (_mi != null)
                {
                    var val = _mi.Invoke(null, new object[]{abs}) as string;
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
            catch { }
            // Fallback diretto (no cache)
            try { return FileUtilEx.MD5Of(abs); } catch { return string.Empty; }
        }
    }
}
#endif
