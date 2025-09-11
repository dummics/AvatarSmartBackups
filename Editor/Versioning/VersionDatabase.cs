#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AvatarSmartBackup.Versioning
{
    /// <summary>
    /// SQLite-based version database for Git-like backup system
    /// Manages commits, file deltas, and metadata efficiently
    /// </summary>
    internal class VersionDatabase : IDisposable
    {
        private readonly string _dbPath;
        private SQLiteConnection _connection;
        
        public string DatabasePath => _dbPath;
        
        public VersionDatabase(string databasePath)
        {
            _dbPath = databasePath;
            Initialize();
        }
        
        private void Initialize()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath));
            _connection = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
            _connection.Open();
            CreateTables();
        }
        
        private void CreateTables()
        {
            var commands = new[]
            {
                // Commits table - like Git commits
                @"CREATE TABLE IF NOT EXISTS commits (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    hash TEXT UNIQUE NOT NULL,
                    timestamp INTEGER NOT NULL,
                    message TEXT NOT NULL,
                    file_count INTEGER DEFAULT 0,
                    total_size INTEGER DEFAULT 0,
                    parent_id INTEGER,
                    FOREIGN KEY(parent_id) REFERENCES commits(id)
                )",
                
                // Files table - tracked files per commit
                @"CREATE TABLE IF NOT EXISTS files (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    commit_id INTEGER NOT NULL,
                    path TEXT NOT NULL,
                    hash TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    action TEXT NOT NULL, -- 'add', 'modify', 'delete'
                    delta_path TEXT, -- path to delta file if not full copy
                    FOREIGN KEY(commit_id) REFERENCES commits(id),
                    UNIQUE(commit_id, path)
                )",
                
                // Deltas table - binary diffs for space efficiency
                @"CREATE TABLE IF NOT EXISTS deltas (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_id INTEGER NOT NULL,
                    base_commit INTEGER NOT NULL,
                    delta_size INTEGER NOT NULL,
                    compression_ratio REAL DEFAULT 1.0,
                    FOREIGN KEY(file_id) REFERENCES files(id),
                    FOREIGN KEY(base_commit) REFERENCES commits(id)
                )",
                
                // Settings table - system configuration
                @"CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                )",
                
                // Indexes for performance
                "CREATE INDEX IF NOT EXISTS idx_commits_timestamp ON commits(timestamp)",
                "CREATE INDEX IF NOT EXISTS idx_files_commit ON files(commit_id)",
                "CREATE INDEX IF NOT EXISTS idx_files_path ON files(path)",
                "CREATE INDEX IF NOT EXISTS idx_files_hash ON files(hash)"
            };
            
            foreach (var cmd in commands)
            {
                using var command = new SQLiteCommand(cmd, _connection);
                command.ExecuteNonQuery();
            }
            
            // Initialize default settings
            SetSettingIfNotExists("max_versions", "50");
            SetSettingIfNotExists("max_disk_mb", "2048");
            SetSettingIfNotExists("retention_days", "30");
            SetSettingIfNotExists("delta_threshold_kb", "100");
        }
        
        private void SetSettingIfNotExists(string key, string defaultValue)
        {
            using var cmd = new SQLiteCommand("INSERT OR IGNORE INTO settings (key, value) VALUES (@key, @value)", _connection);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", defaultValue);
            cmd.ExecuteNonQuery();
        }
        
        public string GetSetting(string key, string defaultValue = null)
        {
            using var cmd = new SQLiteCommand("SELECT value FROM settings WHERE key = @key", _connection);
            cmd.Parameters.AddWithValue("@key", key);
            var result = cmd.ExecuteScalar();
            return result?.ToString() ?? defaultValue;
        }
        
        public void SetSetting(string key, string value)
        {
            using var cmd = new SQLiteCommand("INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)", _connection);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();
        }
        
        /// <summary>
        /// Create a new commit with file list
        /// </summary>
        public long CreateCommit(string message, List<VersionedFile> files, long? parentId = null)
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                // Create commit hash from timestamp + file count
                string hash = GenerateCommitHash(DateTime.UtcNow, files.Count);
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                
                using var commitCmd = new SQLiteCommand(@"
                    INSERT INTO commits (hash, timestamp, message, file_count, total_size, parent_id) 
                    VALUES (@hash, @timestamp, @message, @fileCount, @totalSize, @parentId)", _connection);
                    
                commitCmd.Parameters.AddWithValue("@hash", hash);
                commitCmd.Parameters.AddWithValue("@timestamp", timestamp);
                commitCmd.Parameters.AddWithValue("@message", message);
                commitCmd.Parameters.AddWithValue("@fileCount", files.Count);
                commitCmd.Parameters.AddWithValue("@totalSize", System.Linq.Enumerable.Sum(files, f => f.Size));
                commitCmd.Parameters.AddWithValue("@parentId", parentId);
                commitCmd.ExecuteNonQuery();
                
                long commitId = _connection.LastInsertRowId;
                
                // Add files to commit
                foreach (var file in files)
                {
                    using var fileCmd = new SQLiteCommand(@"
                        INSERT INTO files (commit_id, path, hash, size, action, delta_path) 
                        VALUES (@commitId, @path, @hash, @size, @action, @deltaPath)", _connection);
                        
                    fileCmd.Parameters.AddWithValue("@commitId", commitId);
                    fileCmd.Parameters.AddWithValue("@path", file.Path);
                    fileCmd.Parameters.AddWithValue("@hash", file.Hash);
                    fileCmd.Parameters.AddWithValue("@size", file.Size);
                    fileCmd.Parameters.AddWithValue("@action", file.Action);
                    fileCmd.Parameters.AddWithValue("@deltaPath", file.DeltaPath);
                    fileCmd.ExecuteNonQuery();
                }
                
                transaction.Commit();
                return commitId;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        
        /// <summary>
        /// Get commit history (timeline)
        /// </summary>
        public List<CommitInfo> GetCommitHistory(int limit = 50)
        {
            var commits = new List<CommitInfo>();
            
            using var cmd = new SQLiteCommand(@"
                SELECT id, hash, timestamp, message, file_count, total_size, parent_id 
                FROM commits 
                ORDER BY timestamp DESC 
                LIMIT @limit", _connection);
            cmd.Parameters.AddWithValue("@limit", limit);
            
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                commits.Add(new CommitInfo
                {
                    Id = reader.GetInt64("id"),
                    Hash = reader.GetString("hash"),
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64("timestamp")),
                    Message = reader.GetString("message"),
                    FileCount = reader.GetInt32("file_count"),
                    TotalSize = reader.GetInt64("total_size"),
                    ParentId = reader.IsDBNull("parent_id") ? null : reader.GetInt64("parent_id")
                });
            }
            
            return commits;
        }
        
        /// <summary>
        /// Get files for a specific commit
        /// </summary>
        public List<VersionedFile> GetCommitFiles(long commitId)
        {
            var files = new List<VersionedFile>();
            
            using var cmd = new SQLiteCommand(@"
                SELECT path, hash, size, action, delta_path 
                FROM files 
                WHERE commit_id = @commitId 
                ORDER BY path", _connection);
            cmd.Parameters.AddWithValue("@commitId", commitId);
            
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                files.Add(new VersionedFile
                {
                    Path = reader.GetString("path"),
                    Hash = reader.GetString("hash"),
                    Size = reader.GetInt64("size"),
                    Action = reader.GetString("action"),
                    DeltaPath = reader.IsDBNull("delta_path") ? null : reader.GetString("delta_path")
                });
            }
            
            return files;
        }
        
        private string GenerateCommitHash(DateTime timestamp, int fileCount)
        {
            var data = $"{timestamp:yyyyMMddHHmmss}_{fileCount}_{Guid.NewGuid():N}";
            return System.Security.Cryptography.SHA1.Create()
                .ComputeHash(System.Text.Encoding.UTF8.GetBytes(data))
                .Take(8).Select(b => b.ToString("x2")).Aggregate((a, b) => a + b);
        }
        
        public int GetTotalTrackedFiles()
        {
            using var command = new SQLiteCommand("SELECT COUNT(DISTINCT path) FROM files", _connection);
            var result = command.ExecuteScalar();
            return result != null ? Convert.ToInt32(result) : 0;
        }
        
        public void Dispose()
        {
            _connection?.Close();
            _connection?.Dispose();
        }
    }
    
    /// <summary>
    /// Commit information for timeline display
    /// </summary>
    public class CommitInfo
    {
        public long Id { get; set; }
        public string Hash { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public string Message { get; set; }
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
        public long? ParentId { get; set; }
        
        public string DisplayName => $"{Hash[..8]} - {Message}";
        public string DisplayTime => Timestamp.ToLocalTime().ToString("MM/dd HH:mm");
        public string DisplaySize => FormatBytes(TotalSize);
        
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024} KB";
            return $"{bytes / (1024 * 1024)} MB";
        }
    }
    
    /// <summary>
    /// File information in version system
    /// </summary>
    public class VersionedFile
    {
        public string Path { get; set; }
        public string Hash { get; set; }
        public long Size { get; set; }
        public string Action { get; set; } // "add", "modify", "delete"
        public string DeltaPath { get; set; } // Path to delta file if applicable
    }
}

// Extension methods for LINQ support
namespace System.Linq
{
    public static class LinqExtensions
    {
        public static long Sum<T>(this IEnumerable<T> source, Func<T, long> selector)
        {
            return source.Select(selector).Aggregate(0L, (acc, val) => acc + val);
        }
    }
}

#endif
