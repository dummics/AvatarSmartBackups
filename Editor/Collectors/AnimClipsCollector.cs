#if UNITY_EDITOR
using System.IO;
using System.Collections.Generic;

namespace AvatarSmartBackup
{
    internal class AnimClipsCollector : IBackupCollector
    {
        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incAnimationClips) yield break;
            string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            foreach (var abs in Directory.GetFiles(root, "*.anim", SearchOption.AllDirectories))
            {
                string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\", "/");
                if (CollectHelpers.PassesFolderFilters(rel, s)) yield return abs;
            }
        }
    }
}
#endif
