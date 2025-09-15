#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Runtime-only cache per hash MD5 dei file del progetto per ridurre letture ripetute.
    /// Non viene serializzato; invalidazione basata su size + lastWriteUtc.
    /// </summary>
    internal static class HashCache
    {
        class Entry { public long size; public long ticks; public string md5; }
        static readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static readonly object _lock = new object();
        static int _hits, _misses;

        public static string GetOrCompute(string absPath)
        {
            try
            {
                var fi = new FileInfo(absPath);
                long size = fi.Length;
                long ticks = fi.LastWriteTimeUtc.Ticks;
                lock (_lock)
                {
                    if (_cache.TryGetValue(absPath, out var e))
                    {
                        if (e.size == size && e.ticks == ticks && !string.IsNullOrEmpty(e.md5)) { _hits++; return e.md5; }
                    }
                }
                // compute fuori lock per non bloccare
                string md5 = FileUtilEx.MD5Of(absPath);
                lock (_lock)
                {
                    _cache[absPath] = new Entry { size = size, ticks = ticks, md5 = md5 };
                    _misses++;
                }
                return md5;
            }
            catch (Exception ex)
            {
                Log.Warn("HashCache: compute fallita per "+absPath+": "+ex.Message);
                return string.Empty;
            }
        }

        public static (int hits,int misses,int entries) Stats()
        {
            lock (_lock) return (_hits,_misses,_cache.Count);
        }

        public static void Clear()
        {
            lock (_lock) { _cache.Clear(); _hits=_misses=0; }
        }
    }
}
#endif
