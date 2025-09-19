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
        public static string ProjectRoot
        {
            get
            {
                var parent = Directory.GetParent(Application.dataPath);
                if (parent == null)
                    throw new InvalidOperationException("Unable to determine Unity project root directory.");
                return parent.FullName;
            }
        }
        public static string ProjectName => new DirectoryInfo(ProjectRoot).Name;
        public static string BackupRoot => Path.Combine(ProjectRoot, $"{Sanitize(ProjectName)} Backups");
        public static string Sanitize(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }
        public static string MakeRelToProject(string? abs)
        {
            if (string.IsNullOrEmpty(abs)) return string.Empty;
            var r = ProjectRoot.Replace('\\', '/');
            var p = abs.Replace('\\', '/');
            return p.StartsWith(r + "/") ? p.Substring(r.Length + 1) : p;
        }
        public static string AssetToAbs(string ap) => Path.Combine(ProjectRoot, ap);

        public static string MD5Of(string file)
        {
            return ComputeHash(MD5.Create(), file);
        }

        public static string SHA256Of(string file)
        {
            return ComputeHash(SHA256.Create(), file);
        }

        static string ComputeHash(HashAlgorithm algorithm, string file)
        {
            using (algorithm)
            using (var stream = File.OpenRead(file))
            {
                var buffer = new byte[64 * 1024];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    algorithm.TransformBlock(buffer, 0, bytesRead, null, 0);
                    totalRead += bytesRead;

                    if (totalRead % (1024 * 1024) == 0)
                        System.Threading.Thread.Yield();
                }

                algorithm.TransformFinalBlock(buffer, 0, 0);
                return BytesToHex(algorithm.Hash);
            }
        }

        static string BytesToHex(byte[] hash)
        {
            if (hash == null || hash.Length == 0) return string.Empty;
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string TryReadGuidFromMeta(string? assetAbs)
        {
            if (string.IsNullOrEmpty(assetAbs)) return null;
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
            var finalDir = Path.GetDirectoryName(finalPath);
            if (string.IsNullOrEmpty(finalDir))
                throw new InvalidOperationException("Invalid destination path for atomic replace.");
            Directory.CreateDirectory(finalDir);
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


