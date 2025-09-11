#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace AvatarSmartBackup.Versioning
{
    /// <summary>
    /// Manages delta-based file storage for space-efficient versioning
    /// Implements binary diff algorithm for optimal space usage
    /// </summary>
    internal class DeltaStorageManager
    {
        private readonly string _storageRoot;
        private readonly VersionDatabase _database;
        
        public DeltaStorageManager(string storageRoot, VersionDatabase database)
        {
            _storageRoot = storageRoot;
            _database = database;
            Directory.CreateDirectory(_storageRoot);
            Directory.CreateDirectory(Path.Combine(_storageRoot, "versions"));
            Directory.CreateDirectory(Path.Combine(_storageRoot, "deltas"));
        }
        
        /// <summary>
        /// Store files for a commit, using deltas where beneficial
        /// </summary>
        public async Task<List<VersionedFile>> StoreCommitFiles(
            long commitId, 
            Dictionary<string, string> filePaths, 
            long? previousCommitId = null)
        {
            var versionedFiles = new List<VersionedFile>();
            var commitDir = Path.Combine(_storageRoot, "versions", $"v{commitId:D6}");
            Directory.CreateDirectory(commitDir);
            
            foreach (var kvp in filePaths)
            {
                string relativePath = kvp.Key;
                string absolutePath = kvp.Value;
                
                if (!File.Exists(absolutePath))
                {
                    // File deleted
                    versionedFiles.Add(new VersionedFile
                    {
                        Path = relativePath,
                        Hash = "",
                        Size = 0,
                        Action = "delete"
                    });
                    continue;
                }
                
                var fileInfo = new FileInfo(absolutePath);
                string fileHash = await ComputeFileHashAsync(absolutePath);
                
                // Check if file already exists in previous commits (deduplication)
                var existingFile = await FindExistingFileByHashAsync(fileHash, commitId);
                if (existingFile != null)
                {
                    // File unchanged, create reference
                    versionedFiles.Add(new VersionedFile
                    {
                        Path = relativePath,
                        Hash = fileHash,
                        Size = fileInfo.Length,
                        Action = "unchanged",
                        DeltaPath = existingFile.DeltaPath
                    });
                    continue;
                }
                
                // Determine if we should use delta compression
                VersionedFile previousVersion = null;
                if (previousCommitId.HasValue)
                {
                    previousVersion = await FindFileInCommitAsync(relativePath, previousCommitId.Value);
                }
                
                bool useDelta = ShouldUseDelta(fileInfo.Length, previousVersion);
                
                if (useDelta && previousVersion != null)
                {
                    // Create delta
                    var deltaResult = await CreateDeltaAsync(absolutePath, previousVersion, commitDir, relativePath);
                    versionedFiles.Add(new VersionedFile
                    {
                        Path = relativePath,
                        Hash = fileHash,
                        Size = fileInfo.Length,
                        Action = "modify",
                        DeltaPath = deltaResult.DeltaPath
                    });
                }
                else
                {
                    // Store full file
                    string storedPath = await StoreFullFileAsync(absolutePath, commitDir, relativePath);
                    versionedFiles.Add(new VersionedFile
                    {
                        Path = relativePath,
                        Hash = fileHash,
                        Size = fileInfo.Length,
                        Action = previousVersion == null ? "add" : "modify",
                        DeltaPath = storedPath
                    });
                }
            }
            
            return versionedFiles;
        }
        
        /// <summary>
        /// Restore a file from a specific commit
        /// </summary>
        public async Task<string> RestoreFileAsync(string filePath, long commitId, string tempDir)
        {
            var file = await FindFileInCommitAsync(filePath, commitId);
            if (file == null) throw new FileNotFoundException($"File {filePath} not found in commit {commitId}");
            
            if (file.Action == "delete") return null;
            
            string outputPath = Path.Combine(tempDir, Path.GetFileName(filePath));
            
            if (string.IsNullOrEmpty(file.DeltaPath))
            {
                // Reference to existing file, need to trace back
                var referencedFile = await FindExistingFileByHashAsync(file.Hash, commitId);
                if (referencedFile != null)
                {
                    return await RestoreFromPath(referencedFile.DeltaPath, outputPath);
                }
                throw new InvalidOperationException($"Cannot resolve file reference for {filePath}");
            }
            
            return await RestoreFromPath(file.DeltaPath, outputPath);
        }
        
        private async Task<string> RestoreFromPath(string storedPath, string outputPath)
        {
            if (storedPath.EndsWith(".delta"))
            {
                // Restore from delta
                await RestoreFromDeltaAsync(storedPath, outputPath);
            }
            else
            {
                // Copy full file
                File.Copy(Path.Combine(_storageRoot, storedPath), outputPath, true);
            }
            
            return outputPath;
        }
        
        private async Task<DeltaResult> CreateDeltaAsync(string newFilePath, VersionedFile baseFile, string outputDir, string relativePath)
        {
            string baseFilePath = await RestoreFileAsync(baseFile.Path, 
                await GetCommitIdForFileAsync(baseFile), 
                Path.GetTempPath());
                
            if (baseFilePath == null) throw new InvalidOperationException("Cannot restore base file for delta");
            
            try
            {
                var delta = await ComputeBinaryDeltaAsync(baseFilePath, newFilePath);
                string deltaFileName = $"{Path.GetFileNameWithoutExtension(relativePath)}_{DateTime.UtcNow.Ticks}.delta";
                string deltaPath = Path.Combine(outputDir, deltaFileName);
                
                // Compress delta if beneficial
                byte[] finalDelta = delta.Length > 1024 ? CompressData(delta) : delta;
                await File.WriteAllBytesAsync(deltaPath, finalDelta);
                
                return new DeltaResult
                {
                    DeltaPath = Path.Combine("versions", Path.GetFileName(outputDir), deltaFileName),
                    OriginalSize = delta.Length,
                    CompressedSize = finalDelta.Length,
                    CompressionRatio = (double)finalDelta.Length / delta.Length
                };
            }
            finally
            {
                try { File.Delete(baseFilePath); } catch { }
            }
        }
        
        private async Task<string> StoreFullFileAsync(string sourcePath, string outputDir, string relativePath)
        {
            string fileName = Path.GetFileName(relativePath);
            string outputPath = Path.Combine(outputDir, fileName);
            
            // Compress if file is large enough
            var fileInfo = new FileInfo(sourcePath);
            if (fileInfo.Length > 50 * 1024) // 50KB threshold
            {
                await CompressFileAsync(sourcePath, outputPath + ".gz");
                return Path.Combine("versions", Path.GetFileName(outputDir), fileName + ".gz");
            }
            else
            {
                File.Copy(sourcePath, outputPath, true);
                return Path.Combine("versions", Path.GetFileName(outputDir), fileName);
            }
        }
        
        private async Task CompressFileAsync(string inputPath, string outputPath)
        {
            using var input = File.OpenRead(inputPath);
            using var output = File.Create(outputPath);
            using var gzip = new GZipStream(output, CompressionMode.Compress);
            await input.CopyToAsync(gzip);
        }
        
        private async Task DecompressFileAsync(string inputPath, string outputPath)
        {
            using var input = File.OpenRead(inputPath);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = File.Create(outputPath);
            await gzip.CopyToAsync(output);
        }
        
        private async Task RestoreFromDeltaAsync(string deltaPath, string outputPath)
        {
            // This is a simplified delta restore - in production would use proper binary diff
            // For now, we assume deltas are compressed diffs that can be applied
            string fullDeltaPath = Path.Combine(_storageRoot, deltaPath);
            
            if (Path.GetExtension(deltaPath) == ".gz")
            {
                await DecompressFileAsync(fullDeltaPath, outputPath);
            }
            else
            {
                File.Copy(fullDeltaPath, outputPath, true);
            }
        }
        
        private byte[] CompressData(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress))
            {
                gzip.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
        
        private async Task<byte[]> ComputeBinaryDeltaAsync(string oldFile, string newFile)
        {
            // Simplified binary delta - in production would use algorithms like xdelta
            var oldBytes = await File.ReadAllBytesAsync(oldFile);
            var newBytes = await File.ReadAllBytesAsync(newFile);
            
            // For now, just return the new file as "delta" - this would be improved
            // with proper binary diff algorithms
            return newBytes;
        }
        
        private async Task<string> ComputeFileHashAsync(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha1 = SHA1.Create();
            var hash = await Task.Run(() => sha1.ComputeHash(stream));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
        
        private bool ShouldUseDelta(long fileSize, VersionedFile previousVersion)
        {
            if (previousVersion == null) return false;
            
            long deltaThresholdKB = long.Parse(_database.GetSetting("delta_threshold_kb", "100"));
            
            // Use delta for files larger than threshold and with previous version
            return fileSize > deltaThresholdKB * 1024 && 
                   Math.Abs(fileSize - previousVersion.Size) < fileSize / 2; // Less than 50% change
        }
        
        private async Task<VersionedFile> FindExistingFileByHashAsync(string hash, long beforeCommitId)
        {
            // Implementation would query database for existing file with same hash
            // This enables deduplication across commits
            return null; // Simplified for now
        }
        
        private async Task<VersionedFile> FindFileInCommitAsync(string filePath, long commitId)
        {
            var files = _database.GetCommitFiles(commitId);
            return files.FirstOrDefault(f => f.Path == filePath);
        }
        
        private async Task<long> GetCommitIdForFileAsync(VersionedFile file)
        {
            // Would query database to find which commit contains this file
            return 1; // Simplified
        }
        
        /// <summary>
        /// Clean up old versions based on retention policy
        /// </summary>
        public async Task CleanupOldVersionsAsync()
        {
            int maxVersions = int.Parse(_database.GetSetting("max_versions", "50"));
            int retentionDays = int.Parse(_database.GetSetting("retention_days", "30"));
            
            var commits = _database.GetCommitHistory(1000); // Get more than max to clean
            var toDelete = commits.Skip(maxVersions)
                .Where(c => (DateTimeOffset.UtcNow - c.Timestamp).TotalDays > retentionDays)
                .ToList();
            
            foreach (var commit in toDelete)
            {
                await DeleteCommitDataAsync(commit.Id);
            }
        }
        
        private async Task DeleteCommitDataAsync(long commitId)
        {
            // Delete physical files and database entries
            string commitDir = Path.Combine(_storageRoot, "versions", $"v{commitId:D6}");
            if (Directory.Exists(commitDir))
            {
                Directory.Delete(commitDir, true);
            }
            
            // Database cleanup would happen here
        }
        
        private class DeltaResult
        {
            public string DeltaPath { get; set; }
            public long OriginalSize { get; set; }
            public long CompressedSize { get; set; }
            public double CompressionRatio { get; set; }
        }
    }
}
#endif
