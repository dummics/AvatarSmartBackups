#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AvatarSmartBackup
{
    // Tracks file changes incrementally using FileSystemWatcher to avoid full scans
    internal static class IncrementalCollector
    {
        static readonly HashSet<string> Changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static FileSystemWatcher _watcher;

        static IncrementalCollector()
        {
            try
            {
                string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
                if (Directory.Exists(root))
                {
                    _watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                    };
                    _watcher.Changed += OnChange;
                    _watcher.Created += OnChange;
                    _watcher.Deleted += OnChange;
                    _watcher.Renamed += (s, e) =>
                    {
                        OnChange(s, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath), e.Name));
                        OnChange(s, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(e.OldFullPath), e.OldName));
                    };
                    _watcher.EnableRaisingEvents = true;
                }
            }
            catch { _watcher = null; }
        }

        static void OnChange(object sender, FileSystemEventArgs e)
        {
            lock (Changed) Changed.Add(e.FullPath);
        }

        public static IEnumerable<string> ConsumeChanges()
        {
            lock (Changed)
            {
                var arr = Changed.ToArray();
                Changed.Clear();
                return arr;
            }
        }
    }
}
#endif

