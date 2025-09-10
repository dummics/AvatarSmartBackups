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
            var files = Directory.GetFiles(root, "*.controller", SearchOption.AllDirectories);
            foreach (var abs in files)
            {
                string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\", "/");
                if (CollectHelpers.PassesFolderFilters(rel, s)) yield return abs;
            }
        }
    }
}
#endif
