#if UNITY_EDITOR
using System;
using System.Linq;

namespace AvatarSmartBackup
{
    internal static class CollectHelpers
    {
        static bool MatchesExt(string assetPath, string pat)
        {
            if (string.IsNullOrEmpty(pat)) return false;
            pat = pat.Trim();
            if (pat.StartsWith("*")) pat = pat.Substring(1);
            if (!pat.StartsWith(".")) return false;
            return assetPath.EndsWith(pat, StringComparison.OrdinalIgnoreCase);
        }

        public static bool PassesFolderFilters(string assetPath, BackupSettings s)
        {
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                if (s?.AdvancedMode == true) Log.Info($"Filter: {assetPath} rejected (not under Assets/)");
                return false;
            }
            // Snapshot lists to avoid collection-modified exceptions when UI may change them concurrently
            var includes = s.includeFolders != null ? s.includeFolders.ToArray() : Array.Empty<string>();
            if (includes.Length > 0)
            {
                bool any = includes.Any(f =>
                {
                    var t = (f ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(t)) return false;
                    if (t.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                        return assetPath.StartsWith(t, StringComparison.OrdinalIgnoreCase);
                    // Treat entries like ".anim" or "*.anim" as extension filters
                    return MatchesExt(assetPath, t);
                });
                if (!any)
                {
                    if (s?.AdvancedMode == true) Log.Info($"Filter: {assetPath} rejected by includeFolders (no include matched)");
                    return false;
                }
            }

            var excludes = s.excludeFolders != null ? s.excludeFolders.ToArray() : Array.Empty<string>();
            if (excludes.Any(f =>
            {
                var t = (f ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(t)) return false;
                if (t.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    return assetPath.StartsWith(t, StringComparison.OrdinalIgnoreCase);
                return MatchesExt(assetPath, t);
            }))
            {
                if (s?.AdvancedMode == true) Log.Info($"Filter: {assetPath} rejected by excludeFolders");
                return false;
            }
            return true;
        }
    }
}
#endif

