#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    internal static class SnapshotCreator
    {
        static string CurrentDir => Path.Combine(FileUtilEx.BackupRoot, "Current");
        static string ArchiveDir => Path.Combine(FileUtilEx.BackupRoot, "Archive");

        static int EffectiveZipMBps(BackupSettings s)
        {
            if (s.autoThrottle && s.lastMeasuredMBps > 0f)
                return Math.Max(1, (int)(s.lastMeasuredMBps * 0.5f));
            return s.zipMaxMBps;
        }

        static string HumanMB(long bytes)
        {
            if (bytes <= 0) return "0 MB";
            double mb = bytes / (1024.0 * 1024.0);
            if (mb < 1024.0) return mb.ToString("0.0") + " MB";
            double gb = mb / 1024.0;
            return gb.ToString("0.00") + " GB";
        }

        static string MakeRelTo(string p, string root)
        {
            var pu = new Uri(Path.GetFullPath(p));
            var ru = new Uri(Path.GetFullPath(root) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(ru.MakeRelativeUri(pu).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        internal static bool ShouldCreateZip(BackupSettings s, bool hadChanges)
        {
            if (s.keepSnapshots <= 1) return false;          // niente snapshot richiesti
            switch (s.zipPolicy)
            {
                case ZipPolicy.OnChange:
                    return hadChanges;                        // zip solo se ci sono state changes
                case ZipPolicy.Idle:
                    return !hadChanges;                       // zip quando non ci sono changes
                case ZipPolicy.Manual:
                default:
                    return false;                             // manual policy: never auto-create
            }
        }

        internal static Task CreateZipAsync(BackupSettings s, CancellationToken ct, CancellationTokenSource ctsForUi, bool showUI)
        {
            return Task.Run(() =>
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string zipPath = Path.Combine(ArchiveDir, $"{FileUtilEx.Sanitize(FileUtilEx.ProjectName)}_{stamp}.zip");
                Directory.CreateDirectory(ArchiveDir);
                int progId = showUI ? ProgressUX.Start("Avatar Smart Backup", "Creating archive…", cancellable: true, onCancel: () => { ctsForUi.Cancel(); return true; }) : -1;
                try
                {
                    // Zip manuale con progress + throttle, scrittura atomica
                    string tmp = zipPath + ".tmp";
                    if (File.Exists(tmp)) File.Delete(tmp);
                    var files = Directory.GetFiles(CurrentDir, "*", SearchOption.AllDirectories)
                                         .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                                         .ToArray();

                    // stima dimensione per progress
                    long totalBytes = files.Sum(f => new FileInfo(f).Length);
                    long written = 0;

                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
                    {
                        foreach (var abs in files)
                        {
                            ct.ThrowIfCancellationRequested();
                            string rel = MakeRelTo(abs, CurrentDir).Replace("\\", "/");
                            var entry = zip.CreateEntry(rel, s.zipFastest ? System.IO.Compression.CompressionLevel.Fastest : System.IO.Compression.CompressionLevel.Optimal);
                            using var entryStream = entry.Open();
                            using var src = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            IOThrottle.CopyStreamThrottled(src, entryStream, bufferBytes: 2 * 1024 * 1024, maxMBps: EffectiveZipMBps(s), ct: ct);
                            written += src.Length;
                            float p = totalBytes > 0 ? (float)written / totalBytes : 1f;
                            ProgressUX.Report(progId, p, $"Zip… {rel} ({HumanMB(written)}/{HumanMB(totalBytes)})");
                        }
                    }
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    File.Move(tmp, zipPath);

                    // retention
                    if (s.keepSnapshots > 1)
                    {
                        DateTime ParseZipStamp(string path)
                        {
                            try
                            {
                                string name = Path.GetFileNameWithoutExtension(path);
                                string prefix = FileUtilEx.Sanitize(FileUtilEx.ProjectName) + "_";
                                int idx = name.LastIndexOf('_');
                                if (idx >= 0 && name.StartsWith(prefix, StringComparison.Ordinal))
                                {
                                    string ts = name.Substring(prefix.Length);
                                    if (DateTime.TryParseExact(ts, "yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var dt))
                                        return dt.ToUniversalTime();
                                }
                            }
                            catch { }
                            return new FileInfo(path).LastWriteTimeUtc;
                        }

                        var zips = Directory.GetFiles(ArchiveDir, "*.zip", SearchOption.TopDirectoryOnly)
                                            .OrderByDescending(f => ParseZipStamp(f))
                                            .ToList();
                        for (int i = s.keepSnapshots; i < zips.Count; i++)
                            try { File.Delete(zips[i]); } catch { }
                    }
                    Log.Info($"Created snapshot: {Path.GetFileName(zipPath)}", "Snapshot created successfully");
                }

                catch (Exception ex)
                {
                    Log.Error($"Snapshot creation failed: {ex.Message}", "Snapshot creation failed (see log file for details)", ex);
                }
                finally
                {
                    ProgressUX.Finish(progId);
                    try
                    {
                        string tmp = zipPath + ".tmp";
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                    catch { }
                }
            }, ct);
        }

        public static void CreateSnapshotNow(BackupSettings s)
        {
            try
            {
                string srcRoot = CurrentDir;
                if (!Directory.Exists(srcRoot)) { EditorUtility.DisplayDialog("Snapshot", "No Current/ backup found. Run a backup first.", "OK"); return; }
                var ok = Path.Combine(srcRoot, "backup.ok");
                if (!File.Exists(ok)) { EditorUtility.DisplayDialog("Snapshot", "Backup in progress or not complete. Try again after it finishes.", "OK"); return; }
                var cts = new CancellationTokenSource();
                _ = CreateZipAsync(s, cts.Token, cts, showUI: true);
            }
            catch (Exception ex)
            {
                Log.Error("Snapshot error: " + ex.Message, "Snapshot error: " + ex.Message);
            }
        }

        public static async Task RunManualSnapshotAsync(BackupSettings s)
        {
            // Anti-spam: cooldown defined in settings
            if (!BackupManager.TryClaimManualSnapshot(TimeSpan.FromSeconds(s.manualSnapshotCooldownSeconds)))
            {
                Log.Warn($"Manual snapshot request ignored due to cooldown ({s.manualSnapshotCooldownSeconds}s remaining)", 
                        "Snapshot creation rate limited");
                return;
            }

            if (BackupManager.IsBusy)
            {
                Log.Warn("Manual snapshot request ignored: backup operation is currently running", 
                        "Cannot create snapshot during backup");
                return;
            }

            try
            {
                string srcRoot = CurrentDir;
                if (!Directory.Exists(srcRoot)) { EditorUtility.DisplayDialog("Snapshot", "No Current/ backup found. Run a backup first.", "OK"); return; }
                var ok = Path.Combine(srcRoot, "backup.ok");
                if (!File.Exists(ok)) { EditorUtility.DisplayDialog("Snapshot", "Backup in progress or not complete. Try again after it finishes.", "OK"); return; }
                var cts = new CancellationTokenSource();
                await CreateZipAsync(s, cts.Token, cts, showUI: true);
            }
            catch (Exception ex)
            {
                Log.Error($"Manual snapshot operation failed: {ex.Message}", "Snapshot creation failed", ex);
            }
        }
    }
}
#endif
