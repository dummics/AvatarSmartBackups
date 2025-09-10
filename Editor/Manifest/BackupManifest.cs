#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace AvatarSmartBackup
{
    [Serializable]
    public class BackupManifest
    {
        public string projectName;
        public string unityVersion;
        public string createdUtc;
        public List<ManifestEntry> entries = new List<ManifestEntry>();
    }
}
#endif
