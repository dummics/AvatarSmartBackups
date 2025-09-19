#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace AvatarSmartBackup
{
    [Serializable]
    public class VersionCategoryStat
    {
        public string category = "";
        public int count;
        public long bytes;

        public VersionCategoryStat() { }

        public VersionCategoryStat(string category, int count, long bytes)
        {
            this.category = category;
            this.count = count;
            this.bytes = bytes;
        }
    }

    [Serializable]
    public class VersionDeltaEntry
    {
        public string relPath = "";
        public long size;
        public string hash = "";
        public string category = "";
        public bool isNew;
    }

    [Serializable]
    public class VersionDeltaMetadata
    {
        public int versionId;
        public bool isCheckpoint;
        public int parentVersionId;
        public int checkpointVersionId;
        public long changedBytes;
        public long removedBytes;
        public int changedFileCount;
        public int removedFileCount;
        public List<VersionDeltaEntry> changedEntries = new List<VersionDeltaEntry>();
        public List<string> removedEntries = new List<string>();
        public List<VersionCategoryStat> categoryBreakdown = new List<VersionCategoryStat>();
    }
}
#endif

