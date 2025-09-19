#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AvatarSmartBackup
{
    internal readonly struct FileScanResult
    {
        public FileScanResult(string assetPath, string absolutePath, long size, long lastWriteUtcTicks, bool hasMeta)
        {
            AssetPath = assetPath;
            AbsolutePath = absolutePath;
            Size = size;
            LastWriteUtcTicks = lastWriteUtcTicks;
            HasMeta = hasMeta;
        }

        public string AssetPath { get; }
        public string AbsolutePath { get; }
        public long Size { get; }
        public long LastWriteUtcTicks { get; }
        public bool HasMeta { get; }
    }

    internal interface IFileScanner
    {
        IReadOnlyList<FileScanResult> Scan(BackupSettings settings, IEnumerable<string>? dirtyPaths = null);
    }

    internal sealed class FileScanner : IFileScanner
    {
        static readonly string[] HeavySkip = new[] { ".fbx", ".obj", ".blend" };
        static readonly HashSet<string> HeavySkipSet = new HashSet<string>(HeavySkip, StringComparer.OrdinalIgnoreCase);
        static readonly string[] VrcCommonNames =
        {
            "VRCExpressionsMenu.asset",
            "VRCExpressionParameters.asset",
            "Expressions Menu.asset",
            "Expression Parameters.asset"
        };

        public IReadOnlyList<FileScanResult> Scan(BackupSettings settings, IEnumerable<string> dirtyPaths = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var results = new List<FileScanResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string assetsRoot = Path.Combine(FileUtilEx.ProjectRoot, "Assets");
            if (!Directory.Exists(assetsRoot)) return results;

            var includeFolders = BuildIncludeFolders(settings);
            var extensionPatterns = BuildExtensionSet(settings);
            bool limitExtsToFolders = settings.extWithinIncludeFolders && includeFolders.Count > 0;
            long materialsMaxBytes = Math.Max(10, settings.materialsMaxKB) * 1024L;
            long dllMaxBytes = Math.Max(128, settings.dllsMaxKB) * 1024L;
            var selectionRules = SelectionFilter.BuildRules(settings);

            var dirty = NormalizeDirtyPaths(dirtyPaths, assetsRoot);
            if (dirty.Count > 0)
            {
                foreach (var abs in dirty)
                {
                    ProcessCandidate(abs, allowAny: true);
                }
            }
            else
            {
                foreach (var abs in EnumerateAssets(assetsRoot))
                {
                    ProcessCandidate(abs, allowAny: false);
                }
            }

            results.Sort((a, b) => string.CompareOrdinal(a.AssetPath, b.AssetPath));
            return results;

            void ProcessCandidate(string absPath, bool allowAny)
            {
                if (string.IsNullOrEmpty(absPath)) return;
                if (!File.Exists(absPath)) return;
                // Skip anything under a .git directory
                if (IsUnderDotGit(absPath)) return;

                string rel = FileUtilEx.MakeRelToProject(absPath).Replace('\\', '/');
                if (!rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return;
                if (!SelectionFilter.Allows(selectionRules, rel)) return;
                if (!CollectHelpers.PassesFolderFilters(rel, settings)) return;

                string ext = Path.GetExtension(rel);
                if (IsDisallowedExtension(ext)) return;

                var info = new FileInfo(absPath);
                if (!allowAny && !MatchesCategory(rel, ext, info, settings, materialsMaxBytes, dllMaxBytes, extensionPatterns, includeFolders, limitExtsToFolders))
                    return;

                if (seen.Contains(rel)) return;

                bool hasMeta = File.Exists(absPath + ".meta");
                results.Add(new FileScanResult(rel, absPath, info.Length, info.LastWriteTimeUtc.Ticks, hasMeta));
                seen.Add(rel);
            }
        }

        internal static bool IsDisallowedExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            if (string.Equals(ext, ".meta", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase)) return true;
            return HeavySkipSet.Contains(ext);
        }

        static bool MatchesCategory(string rel, string ext, FileInfo info, BackupSettings settings, long materialsMaxBytes, long dllMaxBytes, HashSet<string> extensionPatterns, List<string> includeFolders, bool limitExtsToFolders)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            if (string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase)) return false;

            if (ext.Equals(".anim", StringComparison.OrdinalIgnoreCase) && settings.incAnimationClips)
                return true;
            if (ext.Equals(".controller", StringComparison.OrdinalIgnoreCase) && settings.incAnimControllers)
                return true;
            if (ext.Equals(".unity", StringComparison.OrdinalIgnoreCase) && settings.incScenes)
                return true;
            if (ext.Equals(".mat", StringComparison.OrdinalIgnoreCase) && settings.incMaterials && info.Length <= materialsMaxBytes)
                return true;
            if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) && settings.incDlls && info.Length <= dllMaxBytes)
                return true;

            if (ext.Equals(".asset", StringComparison.OrdinalIgnoreCase) && settings.incVRCAssets)
            {
                string fileName = Path.GetFileName(rel);
                if (VrcCommonNames.Any(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)))
                    return true;
                if (fileName.IndexOf("VRCExpression", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            if (extensionPatterns.Count > 0 && extensionPatterns.Contains(ext))
            {
                if (!limitExtsToFolders)
                    return true;
                if (includeFolders.Any(folder => rel.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            return false;
        }

        static List<string> BuildIncludeFolders(BackupSettings settings)
        {
            var result = new List<string>();
            if (settings.includeFolders == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in settings.includeFolders)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string trimmed = raw.Trim().Replace('\\', '/');
                if (!trimmed.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!trimmed.EndsWith("/")) trimmed += "/";
                if (seen.Add(trimmed)) result.Add(trimmed);
            }
            return result;
        }

        static HashSet<string> BuildExtensionSet(BackupSettings settings)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (settings.includeFolders == null) return set;
            foreach (var raw in settings.includeFolders)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string trimmed = raw.Trim();
                if (trimmed.StartsWith("*")) trimmed = trimmed.Substring(1);
                if (!trimmed.StartsWith(".")) continue;
                if (HeavySkipSet.Contains(trimmed) || string.Equals(trimmed, ".cs", StringComparison.OrdinalIgnoreCase)) continue;
                set.Add(trimmed);
            }
            return set;
        }

        static List<string> NormalizeDirtyPaths(IEnumerable<string> dirtyPaths, string assetsRoot)
        {
            var result = new List<string>();
            if (dirtyPaths == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in dirtyPaths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string abs;
                try { abs = Path.GetFullPath(raw); }
                catch { continue; }

                if (!abs.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase)) continue;

                if (Directory.Exists(abs))
                {
                    foreach (var file in EnumerateAssets(abs))
                    {
                        string normalized = NormalizeCandidate(file);
                        if (normalized != null && seen.Add(normalized))
                            result.Add(normalized);
                    }
                }
                else if (File.Exists(abs))
                {
                    string normalized = NormalizeCandidate(abs);
                    if (normalized != null && seen.Add(normalized))
                        result.Add(normalized);
                }
            }
            return result;
        }

        static string NormalizeCandidate(string absPath)
        {
            if (string.IsNullOrEmpty(absPath)) return null;
            if (absPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                string basePath = absPath.Substring(0, absPath.Length - 5);
                if (!File.Exists(basePath)) return null;
                return basePath;
            }
            return absPath;
        }

        static IEnumerable<string> EnumerateAssets(string root)
        {
            if (string.IsNullOrEmpty(root)) yield break;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] subDirs = Array.Empty<string>();
                try { subDirs = Directory.GetDirectories(dir); }
                catch { }
                foreach (var sub in subDirs)
                {
                    try
                    {
                        var name = Path.GetFileName(sub);
                        if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
                            continue; // skip .git folders entirely
                        stack.Push(sub);
                    }
                    catch { stack.Push(sub); }
                }

                string[] files = Array.Empty<string>();
                try { files = Directory.GetFiles(dir); }
                catch { }
                foreach (var file in files)
                {
                    if (IsUnderDotGit(file)) continue;
                    yield return file;
                }
            }
        }

        static bool IsUnderDotGit(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            string marker = Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar;
            return p.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
#endif
