#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using SQLite;

namespace AvatarSmartBackup.Versioning
{
    /// <summary>
    /// Simple version tracking record - one entry per backup
    /// Designed for transparency and ease of use by VRChat creators
    /// </summary>
    [Table("backup_versions")]
    public class BackupVersion
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [NotNull]
        public string Timestamp { get; set; } // ISO format for easy parsing

        [NotNull]
        public string Description { get; set; } // "Auto backup", "Manual backup", etc.

        public int FileCount { get; set; }
        public long TotalSizeBytes { get; set; }

        [NotNull]
        public string BackupPath { get; set; } // Relative path from backup root

        public bool HasSnapshot { get; set; } // Whether a .zip snapshot was created
        public string SnapshotPath { get; set; } // Path to .zip file if exists

        // User-friendly properties
        public DateTime GetDateTime() => DateTime.Parse(Timestamp);
        public string GetDisplaySize() => FormatBytes(TotalSizeBytes);
        public string GetDisplayDescription() => $"{Description} - {FileCount} files";

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        }
    }

    /// <summary>
    /// Optional file tracking for advanced features (diff, selective restore)
    /// Kept simple for transparency
    /// </summary>
    [Table("backup_files")]
    public class BackupFile
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [NotNull]
        public int VersionId { get; set; } // Foreign key to BackupVersion

        [NotNull]
        public string RelativePath { get; set; } // Path relative to Assets/

        public string FileHash { get; set; } // For change detection
        public long FileSizeBytes { get; set; }
        public string LastModified { get; set; } // ISO format
    }

    /// <summary>
    /// Simple statistics for UI display
    /// </summary>
    public class SimpleVersionStats
    {
        public int TotalVersions { get; set; }
        public long TotalStorageBytes { get; set; }
        public DateTime? OldestVersion { get; set; }
        public DateTime? NewestVersion { get; set; }
        public string StorageDirectory { get; set; }
        
        public string DisplaySize => FormatBytes(TotalStorageBytes);
        public string DisplayVersions => $"{TotalVersions} version{(TotalVersions == 1 ? "" : "s")}";

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        }
    }
}
#endif