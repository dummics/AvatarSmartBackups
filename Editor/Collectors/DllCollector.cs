#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;

namespace AvatarSmartBackup
{
    internal class DllCollector : IBackupCollector
    {
        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incDlls) yield break;
            long maxBytes = Math.Max(128, s.dllsMaxKB) * 1024L;
            string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            foreach (var p in Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories))
            {
                string rel = FileUtilEx.MakeRelToProject(p).Replace("\\", "/");
                if (!CollectHelpers.PassesFolderFilters(rel, s)) continue;
                var fi = new FileInfo(p);
                if (fi.Length <= maxBytes) yield return p;
            }
        }
    }
}
#endif
