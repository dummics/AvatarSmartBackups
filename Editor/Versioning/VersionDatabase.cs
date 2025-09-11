#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarSmartBackup.Versioning
{
    // Temporary implementation - will be replaced with unity-sqlite-net
    internal class VersionDatabase : System.IDisposable
    {
        private readonly string _dbPath;
        public string DatabasePath => _dbPath;
        
        public VersionDatabase(string databasePath)
        {
            _dbPath = databasePath;
            UnityEngine.Debug.Log($"⚠️ Temporary VersionDatabase: {_dbPath}");
        }
        
        public bool CreateCommit(string commitId, string message, System.DateTime timestamp, string author = "AvatarSmartBackup", bool isAutomatic = true)
        {
            UnityEngine.Debug.Log($"⚠️ Temporary CreateCommit: {commitId}");
            return true;
        }
        
        public bool AddFileToCommit(string commitId, string filePath, string fileHash, long fileSize, string operation)
        {
            return true;
        }
        
        public System.Collections.Generic.List<CommitInfo> GetCommitHistory(int limit = 50)
        {
            return new System.Collections.Generic.List<CommitInfo>();
        }
        
        public System.Collections.Generic.List<FileInfo> GetCommitFiles(string commitId)
        {
            return new System.Collections.Generic.List<FileInfo>();
        }
        
        public int GetTotalTrackedFiles() => 0;
        public void SetSetting(string key, string value) { }
        public string GetSetting(string key, string defaultValue = null) => defaultValue;
        public void Dispose() { }
    }
    
    public class CommitInfo
    {
        public string CommitId { get; set; }
        public string Message { get; set; }
        public System.DateTime Timestamp { get; set; }
        public string Author { get; set; }
        public bool IsAutomatic { get; set; }
        public int FileCount { get; set; }
        public long Id => System.Math.Abs(CommitId?.GetHashCode() ?? 0);
        public long TotalSize { get; set; }
        
        // Display properties for UI
        public string Hash => CommitId?.Substring(0, System.Math.Min(8, CommitId.Length)) ?? "";
        public string DisplayName => $"{Hash} - {Message}";
        public string DisplayTime => Timestamp.ToString("MM/dd HH:mm");
        public string DisplaySize => FormatBytes(TotalSize);
        
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024} KB";
            return $"{bytes / (1024 * 1024)} MB";
        }
    }
    
    public class FileInfo
    {
        public string Path { get; set; }
        public string Hash { get; set; }
        public long Size { get; set; }
        public string Operation { get; set; }
    }
    
    public class VersionedFile
    {
        public string Path { get; set; }
        public string Hash { get; set; }
        public long Size { get; set; }
        public string Action { get; set; }
        public string DeltaPath { get; set; }
    }
}
#endif
