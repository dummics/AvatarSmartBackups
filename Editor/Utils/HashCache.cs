#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Runtime cache for file hashes to reduce repeated disk reads.
    /// Entries are keyed by absolute path with size/mtime guards.
    /// </summary>
    internal static class HashCache
    {
        class Entry
        {
            public long size;
            public long ticks;
            public string md5 = "";
            public string sha256 = "";
        }

        static readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static readonly object _lock = new object();
        static int _hits, _misses;

        public static string GetOrCompute(string absPath)
        {
            return GetOrCompute(absPath, HashKind.MD5);
        }

        public static string GetOrCompute(string absPath, HashKind kind)
        {
            try
            {
                var fi = new FileInfo(absPath);
                long size = fi.Length;
                long ticks = fi.LastWriteTimeUtc.Ticks;

                lock (_lock)
                {
                    if (_cache.TryGetValue(absPath, out var cached))
                    {
                        if (cached.size == size && cached.ticks == ticks)
                        {
                            string val = kind == HashKind.MD5 ? cached.md5 : cached.sha256;
                            if (!string.IsNullOrEmpty(val))
                            {
                                _hits++;
                                return val;
                            }
                        }
                    }
                }

                string hash = kind == HashKind.MD5 ? FileUtilEx.MD5Of(absPath) : FileUtilEx.SHA256Of(absPath);

                lock (_lock)
                {
                    if (!_cache.TryGetValue(absPath, out var entry))
                    {
                        entry = new Entry();
                        _cache[absPath] = entry;
                    }

                    entry.size = size;
                    entry.ticks = ticks;
                    if (kind == HashKind.MD5) entry.md5 = hash; else entry.sha256 = hash;
                    _misses++;
                }

                return hash;
            }
            catch (Exception ex)
            {
                Log.Warn("HashCache: compute fallita per " + absPath + ": " + ex.Message);
                return string.Empty;
            }
        }

        public static (int hits, int misses, int entries) Stats()
        {
            lock (_lock) return (_hits, _misses, _cache.Count);
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _cache.Clear();
                _hits = _misses = 0;
            }
        }
    }
}
#endif

