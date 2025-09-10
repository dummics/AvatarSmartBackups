#if UNITY_EDITOR
using System.IO;
using System.Collections.Generic;

namespace AvatarSmartBackup
{
    internal class ControllersCollector : IBackupCollector
    {
        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incAnimControllers) yield break;
            string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            foreach (var abs in Directory.GetFiles(root, "*.controller", SearchOption.AllDirectories))
            {
                string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\", "/");
                if (CollectHelpers.PassesFolderFilters(rel, s)) yield return abs;
            }
        }
    }
}
#endif
