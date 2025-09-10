#if UNITY_EDITOR
using System;

namespace AvatarSmartBackup
{
    [Serializable]
    public class ManifestEntry
    {
        public string guid;
        public string relPath;  // Assets/...
        public string md5;
        public long size;
        public long lastWriteUtcTicks;
    }
}
#endif
