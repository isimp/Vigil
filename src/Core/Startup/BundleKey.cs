using System;
using System.Globalization;
using System.Text;

namespace Vigil.Core
{
    /// <summary>
    /// Recognises a bundle again on a later start: its length, a hash of its first and of its last
    /// megabyte, and the Unity version (a copy is only good for the Unity that made it). LZMA output
    /// differs all the way to its end after any change, so a mod update gives a new key. Hashing two
    /// megabytes instead of the whole bundle keeps the check to a few milliseconds per load.
    /// </summary>
    internal static class BundleKey
    {
        private const int Sample = 1 << 20;
        private const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;

        public static string Of(Func<long, int, byte[]> readAt, long length, string unityVersion)
        {
            if (readAt == null || length <= 0) return null;
            int n = (int)Math.Min(length, Sample);
            ulong head = Fnv(readAt(0, n)), tail = Fnv(readAt(length - n, n));
            ulong unity = Fnv(Encoding.UTF8.GetBytes(unityVersion ?? ""));
            return string.Format(CultureInfo.InvariantCulture, "{0:x}-{1:x16}-{2:x16}-{3:x8}", length, head, tail, (uint)(unity ^ (unity >> 32)));
        }

        /// <summary>FNV-1a, 64 bits.</summary>
        public static ulong Fnv(byte[] data)
        {
            ulong h = Offset;
            if (data == null) return h;
            for (int i = 0; i < data.Length; i++)
            {
                h ^= data[i];
                h *= Prime;
            }
            return h;
        }
    }
}
