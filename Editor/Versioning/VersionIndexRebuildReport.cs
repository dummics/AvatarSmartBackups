#if UNITY_EDITOR
using System.Collections.Generic;

namespace AvatarSmartBackup
{
    public class VersionIndexRebuildReport
    {
        public List<VersionIndexRebuildEntry> changes = new List<VersionIndexRebuildEntry>();
        public List<VersionIndexIssueEntry> issues = new List<VersionIndexIssueEntry>();

        public bool HasChanges => changes != null && changes.Count > 0;
        public bool HasIssues => issues != null && issues.Count > 0;
    }

    public class VersionIndexRebuildEntry
    {
        public int versionId;
        public int oldCount;
        public int newCount;
        public long oldSize;
        public long newSize;
    }

    public class VersionIndexIssueEntry
    {
        public int versionId;
        public string message = "";
    }
}
#endif
