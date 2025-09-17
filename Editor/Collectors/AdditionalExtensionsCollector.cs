#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace AvatarSmartBackup
{
    internal class AdditionalExtensionsCollector : IBackupCollector
    {
        static readonly string[] HeavySkip = new[] { ".fbx", ".obj", ".blend" };
        public IEnumerable<string> CollectAbsolutePaths(BackupSettings s)
        {
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var includes = s?.includeFolders != null ? s.includeFolders.ToArray() : Array.Empty<string>();
            if (includes.Length > 0)
            {
                foreach (var e in includes)
                {
                    if (string.IsNullOrEmpty(e)) continue;
                    string t = e.Trim();
                    if (t.StartsWith("*")) t = t.Substring(1);
                    if (!t.StartsWith(".")) continue;
                    if (HeavySkip.Any(h => t.Equals(h, StringComparison.OrdinalIgnoreCase))) continue; // keep heavy types skipped
                    exts.Add(t);
                }
            }
            if (exts.Count == 0)
            {
                if (s?.AdvancedMode == true) Log.Info("AdditionalExtensionsCollector: no extension patterns found in includeFolders");
                yield break;
            }
            string root = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            // Decide search roots
            var folderRoots = new List<string>();
            if (s.extWithinIncludeFolders && s.includeFolders != null)
            {
                foreach (var f in includes)
                {
                    if (string.IsNullOrEmpty(f)) continue;
                    string t = f.Trim();
                    if (!t.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                    string abs = Path.Combine(FileUtilEx.ProjectRoot, t.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(abs)) folderRoots.Add(abs);
                }
            }
            if (folderRoots.Count == 0) folderRoots.Add(Path.Combine(FileUtilEx.ProjectRoot, "Assets"));

            foreach (var ext in exts)
            {
                string pattern = "*" + ext;
                foreach (var baseRoot in folderRoots)
                {
                    var matches = Directory.GetFiles(baseRoot, pattern, SearchOption.AllDirectories);
                    foreach (var abs in matches)
                    {
                        string rel = FileUtilEx.MakeRelToProject(abs).Replace("\\", "/");
                        if (!CollectHelpers.PassesFolderFilters(rel, s)) continue;
                        yield return abs;
                    }
                }
            }
        }
    }
}
#endif
