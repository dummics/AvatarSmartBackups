using AvatarSmartBackup;

namespace AvatarSmartBackups.Tests;

public class BackupSettingsTests
{
    [Fact]
    public void EnsureVersioningDefaults_ResetsInvalidPolicy()
    {
        var settings = new BackupSettings
        {
            versioningPolicy = (VersioningPolicy)999,
            manualCheckpointFrequency = 0,
            forceFullCheckpointEveryN = 0
        };

        settings.EnsureVersioningDefaults();

        Assert.Equal(VersioningPolicy.Balanced, settings.versioningPolicy);
        Assert.Equal(5, settings.manualCheckpointFrequency);
    }

    [Fact]
    public void EnsureVersioningDefaults_PromotesBalancedWhenForceFullEnabled()
    {
        var settings = new BackupSettings
        {
            versioningPolicy = VersioningPolicy.Balanced,
            manualCheckpointFrequency = 0,
            forceFullCheckpointEveryN = 4
        };

        settings.EnsureVersioningDefaults();

        Assert.Equal(VersioningPolicy.Manual, settings.versioningPolicy);
        Assert.Equal(4, settings.manualCheckpointFrequency);
    }

    [Fact]
    public void EnsureVersioningDefaults_ManualPolicyWithLowFrequency_UsesDefaultPreset()
    {
        var settings = new BackupSettings
        {
            versioningPolicy = VersioningPolicy.Manual,
            manualCheckpointFrequency = 0,
            forceFullCheckpointEveryN = 0
        };

        settings.EnsureVersioningDefaults();

        Assert.Equal(VersioningPolicy.Manual, settings.versioningPolicy);
        Assert.Equal(5, settings.manualCheckpointFrequency);
    }

    [Fact]
    public void EnsureVersioningDefaults_ManualPolicyWithLegacyForceFull_ClampsToMinimum()
    {
        var settings = new BackupSettings
        {
            versioningPolicy = VersioningPolicy.Manual,
            manualCheckpointFrequency = 0,
            forceFullCheckpointEveryN = 2
        };

        settings.EnsureVersioningDefaults();

        Assert.Equal(VersioningPolicy.Manual, settings.versioningPolicy);
        Assert.Equal(3, settings.manualCheckpointFrequency);
    }

    [Theory]
    [InlineData(VersioningPolicy.Balanced, 0, 0)]
    [InlineData(VersioningPolicy.Frequent, 0, 3)]
    [InlineData(VersioningPolicy.Manual, 7, 7)]
    [InlineData(VersioningPolicy.Manual, 0, 1)]
    public void GetCheckpointInterval_ReturnsExpectedValues(VersioningPolicy policy, int manualFrequency, int expected)
    {
        var settings = new BackupSettings
        {
            versioningPolicy = policy,
            manualCheckpointFrequency = manualFrequency
        };

        var interval = settings.GetCheckpointInterval();

        Assert.Equal(expected, interval);
    }

    [Theory]
    [InlineData(VersioningPolicy.Balanced, 0, 0)]
    [InlineData(VersioningPolicy.Frequent, 0, 3)]
    [InlineData(VersioningPolicy.Manual, 5, 5)]
    public void SyncLegacyCheckpointInterval_AlignsWithInterval(VersioningPolicy policy, int manualFrequency, int expected)
    {
        var settings = new BackupSettings
        {
            versioningPolicy = policy,
            manualCheckpointFrequency = manualFrequency
        };

        settings.SyncLegacyCheckpointInterval();

        Assert.Equal(expected, settings.forceFullCheckpointEveryN);
    }
}
