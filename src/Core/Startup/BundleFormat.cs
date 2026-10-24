using System;
using System.Text;

namespace Vigil.Core
{
    /// <summary>What a Unity bundle's header and block table say about it.</summary>
    internal sealed class BundleShape
    {
        public bool UnityFs { get; set; }

        public int Version { get; set; }

        public string Revision { get; set; } = "";

        public int Blocks { get; set; }

        public int LzmaBlocks { get; set; }

        public long PackedBytes { get; set; }

        public long UnpackedBytes { get; set; }

        /// <summary>Why the bundle could not be read; null when it could.</summary>
        public string Problem { get; set; }

        /// <summary>A bundle Unity has to unpack whole on every load: any of its blocks packed with LZMA.</summary>
        public bool WorthACopy => UnityFs && Problem == null && LzmaBlocks > 0;
    }

    /// <summary>
    /// Reads a Unity bundle's header and block table ("UnityFS" format 6 to 8). The header names the
    /// table's packing and whether it sits at the end; each block names its own packing. Unity packs
    /// the table with LZ4 even in a bundle whose blocks are LZMA, so the blocks are what tells.
    /// </summary>
    internal static class BundleFormat
    {
        private const int Lzma = 1, Lz4 = 2, Lz4Hc = 3, TableAtEnd = 0x80, KindMask = 0x3F;

        /// <param name="readAt">Bytes at an offset (fewer at the end of the data).</param>
        /// <param name="length">The bundle's whole length.</param>
        public static BundleShape Read(Func<long, int, byte[]> readAt, long length)
        {
            var shape = new BundleShape();
            if (readAt == null || length < 64) { shape.Problem = "too short"; return shape; }
            try
            {
                byte[] head = readAt(0, (int)Math.Min(length, 1024));
                if (head.Length < 32 || Encoding.ASCII.GetString(head, 0, 7) != "UnityFS" || head[7] != 0) { shape.Problem = "not a Unity bundle"; return shape; }
                shape.UnityFs = true;
                int p = 8;
                shape.Version = (int)U32(head, ref p);
                CString(head, ref p);
                shape.Revision = CString(head, ref p);
                long declared = (long)U64(head, ref p);
                int packedTable = (int)U32(head, ref p), table = (int)U32(head, ref p), flags = (int)U32(head, ref p);
                if (declared != length) { shape.Problem = "its size does not match"; return shape; }
                if (shape.Version >= 7) p = (p + 15) / 16 * 16;
                if (packedTable <= 0 || table <= 0 || packedTable > length) { shape.Problem = "no block table"; return shape; }

                byte[] packed = (flags & TableAtEnd) != 0 ? readAt(length - packedTable, packedTable) : readAt(p, packedTable);
                byte[] info;
                switch (flags & KindMask)
                {
                    case 0: info = packed; break;
                    case Lz4:
                    case Lz4Hc: info = Lz4Block.Decode(packed, table); break;
                    default: shape.Problem = "its block table is packed with LZMA"; return shape;
                }
                if (info == null || info.Length < 20) { shape.Problem = "its block table does not unpack"; return shape; }

                int q = 16;
                int count = (int)U32(info, ref q);
                if (count < 0 || 20 + (long)count * 10 > info.Length) { shape.Problem = "its block table is damaged"; return shape; }
                shape.Blocks = count;
                for (int i = 0; i < count; i++)
                {
                    long unpacked = U32(info, ref q), packedBlock = U32(info, ref q);
                    int kind = (info[q] << 8 | info[q + 1]) & KindMask;
                    q += 2;
                    if (kind == Lzma) shape.LzmaBlocks++;
                    shape.PackedBytes += packedBlock;
                    shape.UnpackedBytes += unpacked;
                }
                return shape;
            }
            catch (Exception e)
            {
                shape.Problem = e.GetType().Name;
                return shape;
            }
        }

        private static uint U32(byte[] b, ref int p)
        {
            uint v = (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]);
            p += 4;
            return v;
        }

        private static ulong U64(byte[] b, ref int p) => (ulong)U32(b, ref p) << 32 | U32(b, ref p);

        private static string CString(byte[] b, ref int p)
        {
            int end = Array.IndexOf(b, (byte)0, p);
            if (end < 0) throw new FormatException("unterminated header text");
            string s = Encoding.ASCII.GetString(b, p, end - p);
            p = end + 1;
            return s;
        }
    }
}
