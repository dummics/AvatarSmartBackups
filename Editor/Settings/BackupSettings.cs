#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace AvatarSmartBackup
{

    public enum VersioningPolicy
    {
        Balanced = 0,
        Frequent = 1,
        Manual = 2
    }

    [Serializable]
    public class BackupSettings
    {
        public bool autoRunOnLoad = true;
        public int intervalMinutes = 10;
        public VersioningPolicy versioningPolicy = VersioningPolicy.Balanced;
        public bool advancedMode = false;
        public bool debugMode = false;
        public bool intervalInSeconds = false;
        public int keepSnapshots = 3;
        public bool backupOnPlayEnter = true;

        public bool incVRCAssets = true;
        public bool incAnimControllers = true;
        public bool incAnimationClips = true;
        public bool incScenes = true;
        public bool incMaterials = true;
        public long materialsMaxKB = 1024;
        public bool incDlls = true;
        public long dllsMaxKB = 2048;

        // UI presets for size limits (dropdown + custom)
        // 0: 256 KB, 1: 512 KB, 2: 1024 KB, 3: 2048 KB, 4: 4096 KB, 5: Custom
        public int materialsSizePresetIndex = 2;
        public int dllSizePresetIndex = 3;

        // Folders
        public List<string> includeFolders = new List<string>() { "Assets/" };
        public List<string> excludeFolders = new List<string>() { "Assets/StreamingAssets", "Packages" };

        public List<string> trackedRoots = new List<string>();
        public bool selectionLocked = false;

        // ZIP / PERFORMANCE
        public ZipPolicy zipPolicy = ZipPolicy.OnChange;
        public int idleDelaySeconds = 10;             // Seconds of inactivity before zipping when Idle policy
        public bool zipFastest = true;                 // Fastest vs Optimal
        public bool autoThrottle = true;               // Automatic throttling (recommended)
        public int copyMaxMBps = 100;                  // Conservative manual cap (MB/s). 0 = unlimited
        public int zipMaxMBps = 50;                    // Conservative manual cap (MB/s). 0 = unlimited  
        public float lastMeasuredMBps = 0f;            // Result of last IO benchmark
        public long lastBenchmarkTicks = 0;            // UTC ticks of last benchmark
        public long lastBackupBytes = 0;                // Size of last backup data
        public int maxParallelThreads = Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 4)); // Conservative: half cores, max 4

        public bool saveScenesBeforeBackup = false;    // Avoid blocking by default

        public bool diskSpaceProtection = true;
        public bool blockBackupOnLowSpace = true;
        public long diskWarningFreeMB = 2048;
        public long diskCriticalFreeMB = 1024;
        public float diskWarningFreePercent = 0.10f;
        public float diskCriticalFreePercent = 0.05f;
        public long diskPreBackupBufferMB = 512;

        public bool filtersDirty = false;
        public long filtersChangedTicks = 0;

        public bool easyMode = false;
        public bool onboardingCompleted = false;

        public bool showAdvanced = false;
        public bool showDebugTools = false;
        public bool useProjectSettings = false;
        
        // Advanced-only options
        public int manualCheckpointFrequency = 5;
        public int forceFullCheckpointEveryN = 0;
        public bool showRebuildTool = false;
        public bool enableDebugLogging = false;        // Detailed logging to file (advanced mode only)

        // Cooldowns and anti-spam (seconds)
        // Reasonable defaults that work automatically - exposed only in advanced mode for power users
        public int manualSnapshotCooldownSeconds = 30;   // Longer default to prevent accidental spam
        public int manualBenchmarkCooldownSeconds = 60;  // Longer to avoid repeated disk stress
        public int minManualBackupIntervalSeconds = 120; // 2 minutes minimum for manual backups
        // Extension scoping: extensions (e.g., .prefab) from Folders & Types apply only within included folders when enabled
        public bool extWithinIncludeFolders = true;

        public bool AdvancedMode
        {
            get => advancedMode;
            set
            {
                advancedMode = value;
                if (!advancedMode) debugMode = false;
            }
        }

        public bool DiagnosticsEnabled => advancedMode && debugMode;

    // UI state (non critico, serializzato con settings)
    public int _activeTab = 0;              // 0 = Backup, 1 = Versions
    public bool _uiTabInitialized = false;  // evita reset ad ogni domain reload


        public void EnsureVersioningDefaults()
        {
            if (!Enum.IsDefined(typeof(VersioningPolicy), versioningPolicy))
                versioningPolicy = VersioningPolicy.Balanced;

            if (manualCheckpointFrequency < 1)
                manualCheckpointFrequency = Math.Max(3, forceFullCheckpointEveryN > 0 ? forceFullCheckpointEveryN : 5);

            if (versioningPolicy == VersioningPolicy.Balanced && forceFullCheckpointEveryN > 0)
            {
                versioningPolicy = VersioningPolicy.Manual;
                manualCheckpointFrequency = Math.Max(1, forceFullCheckpointEveryN);
            }

            if (versioningPolicy == VersioningPolicy.Manual && manualCheckpointFrequency < 1)
                manualCheckpointFrequency = 3;
        }

        public int GetCheckpointInterval()
        {
            switch (versioningPolicy)
            {
                case VersioningPolicy.Frequent:
                    return 3;
                case VersioningPolicy.Manual:
                    return Math.Max(1, manualCheckpointFrequency);
                default:
                    return 0;
            }
        }

        public void SyncLegacyCheckpointInterval()
        {
            forceFullCheckpointEveryN = GetCheckpointInterval();
        }
    }
}
#endif

