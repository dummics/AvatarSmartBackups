#if UNITY_EDITOR
using System.Collections.Generic;

namespace AvatarSmartBackup
{
    internal interface IBackupCollector
    {
        IEnumerable<string> CollectAbsolutePaths(BackupSettings s);
    }
}
#endif
