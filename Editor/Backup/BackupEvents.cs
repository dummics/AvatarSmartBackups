#if UNITY_EDITOR
using System;

namespace AvatarSmartBackup
{
    public readonly struct BackupRunSummary
    {
        public readonly bool Success;
        public readonly string Reason;
        public readonly bool HasChanges;
        public readonly bool VersionCreated;
        public readonly int? CreatedVersionId;
        public readonly bool CreatedVersionIsCheckpoint;
        public readonly long TotalBytes;
        public readonly DateTime CompletedUtc;

        public BackupRunSummary(bool success, string reason, bool hasChanges, bool versionCreated,
            int? createdVersionId, bool createdVersionIsCheckpoint, long totalBytes, DateTime completedUtc)
        {
            Success = success;
            Reason = reason ?? string.Empty;
            HasChanges = hasChanges;
            VersionCreated = versionCreated;
            CreatedVersionId = createdVersionId;
            CreatedVersionIsCheckpoint = createdVersionIsCheckpoint;
            TotalBytes = totalBytes;
            CompletedUtc = completedUtc;
        }
    }

    internal static class BackupEvents
    {
        public static event Action<BackupRunSummary> BackupCompleted;

        public static void RaiseCompleted(in BackupRunSummary summary)
        {
            BackupCompleted?.Invoke(summary);
        }
    }
}
#endif

