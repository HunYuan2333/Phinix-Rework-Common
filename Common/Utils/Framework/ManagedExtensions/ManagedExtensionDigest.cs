using System;
using System.Security.Cryptography;

namespace Utils.Framework.ManagedExtensions
{
    // Static inspection must not depend on filesystem inventory or installation state.
    internal static class ManagedExtensionDigest
    {
        internal static string Hash(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }
        internal static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
    }
}
