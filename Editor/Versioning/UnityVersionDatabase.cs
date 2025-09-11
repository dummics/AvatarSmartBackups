#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Mono.Data.Sqlite;
using UnityEngine;

namespace AvatarSmartBackup.Versioning
{
    /// <summary>
    /// Unity-compatible SQLite version database using Mono.Data.Sqlite
    /// More stable than System.Data.SQLite in Unity environment
    /// </summary>
    internal class UnityVersionDatabase : IDisposable
    {
        private readonly string _dbPath;
        private SqliteConnection _connection;
        
        public string DatabasePath => _dbPath;
        
        public UnityVersionDatabase(string databasePath)
        {
            _dbPath = databasePath;
            Initialize();
        }
        
        private void Initialize()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath));
            _connection = new SqliteConnection($"URI=file:{_dbPath}");
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
                
                // Settings table - system configuration
                @"CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                )",
                
                // Indexes for performance
                "CREATE INDEX IF NOT EXISTS idx_commits_timestamp ON commits(timestamp)",
                "CREATE INDEX IF NOT EXISTS idx_files_commit_id ON files(commit_id)",
                "CREATE INDEX IF NOT EXISTS idx_files_path ON files(path)",
                "CREATE INDEX IF NOT EXISTS idx_files_hash ON files(hash)"
            };
            
            foreach (var sql in commands)
            {
                using var cmd = new SqliteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
            
            // Set initial settings
            SetSetting("version", "2.0");
            SetSetting("created", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        }
        
        public string GetSetting(string key, string defaultValue = null)
        {
            using var cmd = new SqliteCommand("SELECT value FROM settings WHERE key = @key", _connection);
            cmd.Parameters.AddWithValue("@key", key);
            var result = cmd.ExecuteScalar();
            return result?.ToString() ?? defaultValue;
        }
        
        public void SetSetting(string key, string value)
        {
            using var cmd = new SqliteCommand("INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)", _connection);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();
        }
        
        public long CreateCommit(string message, List<VersionedFile> files, long? parentId = null)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var hash = $"{timestamp:x}_{message.GetHashCode():x}";
            
            using var commitCmd = new SqliteCommand(@"
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
                using var fileCmd = new SqliteCommand(@"
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
            
            return commitId;
        }
        
        public List<CommitInfo> GetCommitHistory(int limit = 100)
        {
            var commits = new List<CommitInfo>();
            
            using var cmd = new SqliteCommand(@"
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
                    Id = reader.GetInt64(0),
                    Hash = reader.GetString(1),
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                    Message = reader.GetString(3),
                    FileCount = reader.GetInt32(4),
                    TotalSize = reader.GetInt64(5),
                    ParentId = reader.IsDBNull(6) ? null : reader.GetInt64(6)
                });
            }
            
            return commits;
        }
        
        public List<VersionedFile> GetCommitFiles(long commitId)
        {
            var files = new List<VersionedFile>();
            
            using var cmd = new SqliteCommand(@"
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
                    Path = reader.GetString(0),
                    Hash = reader.GetString(1),
                    Size = reader.GetInt64(2),
                    Action = reader.GetString(3),
                    DeltaPath = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }
            
            return files;
        }
        
        public long GetTotalTrackedFiles()
        {
            using var cmd = new SqliteCommand("SELECT COUNT(DISTINCT path) FROM files", _connection);
            var result = cmd.ExecuteScalar();
            return Convert.ToInt64(result);
        }
        
        public void Dispose()
        {
            _connection?.Close();
            _connection?.Dispose();
        }
    }
}
#endif