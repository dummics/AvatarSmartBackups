#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace AvatarSmartBackup
{
    internal static class FileUtilEx
    {
        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        public static string ProjectName => new DirectoryInfo(ProjectRoot).Name;
        public static string BackupRoot => Path.Combine(ProjectRoot, $"{Sanitize(ProjectName)} Backups");
        public static string Sanitize(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s.Trim(); }
        public static string MakeRelToProject(string abs) { var r = ProjectRoot.Replace('\\', '/'); var p = abs.Replace('\\', '/'); return p.StartsWith(r + "/") ? p.Substring(r.Length + 1) : p; }
        public static string AssetToAbs(string ap) => Path.Combine(ProjectRoot, ap);

        public static string MD5Of(string file)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(file);
            var hash = md5.ComputeHash(stream);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string TryReadGuidFromMeta(string assetAbs)
        {
            try
            {
                string meta = assetAbs + ".meta";
                if (!File.Exists(meta)) return null;
                foreach (var line in File.ReadLines(meta))
                {
                    if (line.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = line.Substring(5).Trim().Trim(':').Trim();
                        return val;
                    }
                }
            }
            catch { }
            return null;
        }

        public static void AtomicReplace(string tmp, string finalPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath));
            if (File.Exists(finalPath))
            {
                try { File.Replace(tmp, finalPath, null, true); }
                catch { File.Delete(finalPath); File.Move(tmp, finalPath); }
            }
            else File.Move(tmp, finalPath);
        }
    }
}
#endif

