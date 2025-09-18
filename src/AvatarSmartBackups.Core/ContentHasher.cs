using System;
using System.IO;
using System.Security.Cryptography;

namespace AvatarSmartBackup
{
    /// <summary>
    /// Utility class for computing file hashes (MD5 and SHA256).
    /// Core logic extracted from HashCache for DLL compilation.
    /// </summary>
    public static class ContentHasher
    {
        /// <summary>
        /// Computes the MD5 hash of a file.
        /// </summary>
        /// <param name="filePath">Absolute path to the file.</param>
        /// <returns>MD5 hash as hexadecimal string.</returns>
        public static string ComputeMD5(string filePath)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Computes the SHA256 hash of a file.
        /// </summary>
        /// <param name="filePath">Absolute path to the file.</param>
        /// <returns>SHA256 hash as hexadecimal string.</returns>
        public static string ComputeSHA256(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Computes the hash of a file based on the specified kind.
        /// </summary>
        /// <param name="filePath">Absolute path to the file.</param>
        /// <param name="kind">Hash algorithm to use.</param>
        /// <returns>Hash as hexadecimal string.</returns>
        public static string ComputeHash(string filePath, HashKind kind)
        {
            return kind == HashKind.MD5 ? ComputeMD5(filePath) : ComputeSHA256(filePath);
        }
    }

    /// <summary>
    /// Enum for hash kinds.
    /// </summary>
    public enum HashKind
    {
        MD5,
        SHA256
    }
}