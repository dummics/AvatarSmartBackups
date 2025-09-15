#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace AvatarSmartBackup
{
    [Serializable]
    public class VersionManifestV2
    {
        public int schema = 2;
        public string versionId;
        public string parentId;
        public bool checkpoint;
        public string createdUtc;
        public string unityVersion;
        public List<VersionManifestEntryV2> entries = new List<VersionManifestEntryV2>();
    }

    [Serializable]
    public class VersionManifestEntryV2
    {
        public string relPath;
        public string hash;
        public long size;
        public long ticks;
        public string metaHash;
        public string legacyMd5;
    }
}
#endif
