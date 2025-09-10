#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;

namespace AvatarSmartBackup
{
    internal class MaterialsCollector : IBackupCollector
    {
        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incMaterials) yield break;
            long maxBytes = Math.Max(10, s.materialsMaxKB) * 1024L;
            string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            var files = Directory.GetFiles(root, "*.mat", SearchOption.AllDirectories);
            foreach (var abs in files)
            {
                string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\", "/");
                if (!CollectHelpers.PassesFolderFilters(rel, s)) continue;
                var fi = new FileInfo(abs);
                if (fi.Length <= maxBytes) yield return abs;
            }
        }
    }
}
#endif
