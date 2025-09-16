#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace AvatarSmartBackup
{
    internal static class SelectionFilter
    {
        internal struct Rule
        {
            public string Path;
            public bool IsDirectory;
        }

        public static List<Rule> BuildRules(BackupSettings settings)
        {
            var result = new List<Rule>();
            if (settings?.trackedRoots == null || settings.trackedRoots.Count == 0)
                return result;

            foreach (var raw in settings.trackedRoots)
            {
                var norm = NormalizeRawPath(raw);
                if (string.IsNullOrEmpty(norm))
                    continue;
                bool isDir = norm.EndsWith("/", StringComparison.OrdinalIgnoreCase);
                string path = isDir ? norm : norm;
                if (isDir && path.Length <= "Assets/".Length)
                    continue;
                if (!isDir && !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (isDir && !path.EndsWith("/"))
                    path += "/";
                if (!result.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new Rule { Path = path, IsDirectory = isDir });
            }
            return result;
        }

        public static List<string> NormalizeForStorage(IEnumerable<string> entries)
        {
            var result = new List<string>();
            if (entries == null) return result;
            foreach (var raw in entries)
            {
                var norm = NormalizeRawPath(raw);
                if (string.IsNullOrEmpty(norm))
                    continue;
                if (!result.Any(existing => string.Equals(existing, norm, StringComparison.OrdinalIgnoreCase)))
                    result.Add(norm);
            }
            return result;
        }

        static string NormalizeRawPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            string trimmed = raw.Trim();
            trimmed = trimmed.Replace('\\', '/');
            if (Path.IsPathRooted(trimmed))
            {
                trimmed = FileUtilEx.MakeRelToProject(trimmed);
            }
            if (!trimmed.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return null;
            if (trimmed.EndsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                while (trimmed.EndsWith("/"))
                    trimmed = trimmed.Substring(0, trimmed.Length - 1);
                return trimmed + "/";
            }
            return trimmed;
        }

        public static bool Allows(List<Rule> rules, string relPath)
        {
            if (rules == null || rules.Count == 0)
                return true;
            foreach (var rule in rules)
            {
                if (rule.IsDirectory)
                {
                    if (relPath.StartsWith(rule.Path, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (string.Equals(rule.Path, relPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
#endif
