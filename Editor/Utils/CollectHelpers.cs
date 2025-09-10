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
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return false;
            if (s.includeFolders != null && s.includeFolders.Count > 0)
            {
                bool any = s.includeFolders.Any(f =>
                {
                    var t = (f ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(t)) return false;
                    if (t.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                        return assetPath.StartsWith(t, StringComparison.OrdinalIgnoreCase);
                    // Treat entries like ".anim" or "*.anim" as extension filters
                    return MatchesExt(assetPath, t);
                });
                if (!any) return false;
            }
            if (s.excludeFolders != null && s.excludeFolders.Any(f =>
            {
                var t = (f ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(t)) return false;
                if (t.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    return assetPath.StartsWith(t, StringComparison.OrdinalIgnoreCase);
                return MatchesExt(assetPath, t);
            })) return false;
            return true;
        }
    }
}
#endif

