// Unity 2022.3+ – Editor only
// Avatar Smart Backup – background copy+zip, non-blocking progress, IO throttle, zip-on-change
// Log tag: [ Avatar Backup System ]

#if UNITY_EDITOR
namespace AvatarSmartBackup
{
    // Zip snapshot creation policy
    public enum ZipPolicy
    {
        OnChange, // create a snapshot only when files changed
        Idle,     // create a snapshot when no changes were detected
        OnPlay    // take a snapshot when entering Play Mode
    }
}
#endif
