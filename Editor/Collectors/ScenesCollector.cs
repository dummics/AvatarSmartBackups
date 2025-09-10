#if UNITY_EDITOR
using System.IO;
using System.Collections.Generic;

namespace AvatarSmartBackup
{
    internal class ScenesCollector : IBackupCollector
    {
        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incScenes) yield break;
            string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            var files = Directory.GetFiles(root, "*.unity", SearchOption.AllDirectories);
            foreach (var abs in files)
            {
                string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\", "/");
                if (CollectHelpers.PassesFolderFilters(rel, s)) yield return abs;
            }
        }
    }
}
#endif
