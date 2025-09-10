#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;

namespace AvatarSmartBackup
{
    internal class VRCAssetsCollector : IBackupCollector
    {
        static readonly string[] CommonNames =
        { "VRCExpressionsMenu.asset", "VRCExpressionParameters.asset", "Expressions Menu.asset", "Expression Parameters.asset" };

        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            if (!s.incVRCAssets) yield break;
            string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            foreach (var cand in CommonNames)
            {
                foreach (var p in Directory.GetFiles(root, cand, SearchOption.AllDirectories))
                {
                    var rel = FileUtilEx.MakeRelToProject(p).Replace("\\", "/");
                    if (CollectHelpers.PassesFolderFilters(rel, s)) yield return p;
                }
            }

            foreach (var p in Directory.GetFiles(root, "*.asset", SearchOption.AllDirectories))
            {
                string fn = Path.GetFileName(p);
                if (fn.IndexOf("VRCExpression", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var rel = FileUtilEx.MakeRelToProject(p).Replace("\\", "/");
                    if (CollectHelpers.PassesFolderFilters(rel, s)) yield return p;
                }
            }
        }
    }
}
#endif
