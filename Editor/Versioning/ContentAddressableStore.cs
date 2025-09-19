#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace AvatarSmartBackup
{
    internal sealed class ContentAddressableStore
    {
        readonly string _root;
        readonly string _stagingRoot;

        public ContentAddressableStore(string? storeRoot = null)
        {
            _root = storeRoot ?? Path.Combine(FileUtilEx.BackupRoot, "Store");
            _stagingRoot = Path.Combine(_root, "tmp");
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_stagingRoot);
        }

        public string GetBlobPath(string? hash)
        {
            if (string.IsNullOrEmpty(hash) || hash.Length < 6)
                return string.Empty;
            string first = hash.Substring(0, 2);
            string second = hash.Substring(2, 2);
            return Path.Combine(_root, first, second, hash);
        }

        public bool Exists(string? hash)
        {
            string path = GetBlobPath(hash);
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        public StoredBlob StoreFile(string absPath, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(absPath)) throw new ArgumentNullException(nameof(absPath));
            if (!File.Exists(absPath)) throw new FileNotFoundException("Source file not found", absPath);

            string stagingPath = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N") + ".tmp");
            long written = 0;

            try
            {
                using var sha = SHA256.Create();
                using var src = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024, FileOptions.SequentialScan);
                using (var staging = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.SequentialScan))
                {
                    var buffer = new byte[256 * 1024];
                    int read;
                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        staging.Write(buffer, 0, read);
                        written += read;
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                }

                string hash = BytesToHex(sha.Hash);
                string finalPath = GetBlobPath(hash);
                if (string.IsNullOrEmpty(finalPath))
                    throw new InvalidOperationException("Invalid hash computed for CAS blob.");

                var finalDir = Path.GetDirectoryName(finalPath);
                if (string.IsNullOrEmpty(finalDir))
                    throw new InvalidOperationException("Invalid final path for CAS blob.");
                Directory.CreateDirectory(finalDir);
                if (File.Exists(finalPath))
                {
                    try { File.Delete(stagingPath); } catch { }
                    return new StoredBlob(hash, finalPath, alreadyExisted: true, writtenBytes: written);
                }

                FileUtilEx.AtomicReplace(stagingPath, finalPath);
                try
                {
                    var info = new FileInfo(finalPath); info.IsReadOnly = true;
                }
                catch { }

                return new StoredBlob(hash, finalPath, alreadyExisted: false, writtenBytes: written);
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { }
                throw new InvalidOperationException($"Failed to store blob for {absPath}: {ex.Message}", ex);
            }
        }

        public bool TryOpenRead(string? hash, out FileStream? stream)
        {
            string path = GetBlobPath(hash);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }
            stream = null;
            return false;
        }

        static string BytesToHex(byte[]? hash)
        {
            if (hash == null || hash.Length == 0) return string.Empty;
            char[] chars = new char[hash.Length * 2];
            int idx = 0;
            foreach (var b in hash)
            {
                chars[idx++] = GetHexNibble(b >> 4);
                chars[idx++] = GetHexNibble(b & 0xF);
            }
            return new string(chars);
        }

        static char GetHexNibble(int value)
        {
            return (char)(value < 10 ? '0' + value : 'a' + (value - 10));
        }
    }

    internal readonly struct StoredBlob
    {
        public StoredBlob(string hash, string path, bool alreadyExisted, long writtenBytes)
        {
            Hash = hash;
            Path = path;
            AlreadyExisted = alreadyExisted;
            Size = writtenBytes;
        }

        public string Hash { get; }
        public string Path { get; }
        public bool AlreadyExisted { get; }
        public long Size { get; }
    }
}
#endif
