namespace Vigil.Core
{
    /// <summary>
    /// Unpacks one LZ4 block (the format Unity packs a bundle's block table with). Small on purpose: it
    /// only ever reads a block table of a few kilobytes, and returns null for anything that does not
    /// unpack to exactly the expected size, so a damaged table is refused rather than misread.
    /// </summary>
    internal static class Lz4Block
    {
        public static byte[] Decode(byte[] src, int size)
        {
            if (src == null || size < 0) return null;
            var dst = new byte[size];
            int s = 0, d = 0;
            while (s < src.Length)
            {
                int token = src[s++];
                int literals = token >> 4;
                if (literals == 15 && !ReadLength(src, ref s, ref literals)) return null;
                if (s + literals > src.Length || d + literals > size) return null;
                System.Array.Copy(src, s, dst, d, literals);
                s += literals;
                d += literals;
                if (s >= src.Length) break;

                if (s + 2 > src.Length) return null;
                int offset = src[s] | (src[s + 1] << 8);
                s += 2;
                if (offset == 0 || offset > d) return null;
                int match = token & 15;
                if (match == 15 && !ReadLength(src, ref s, ref match)) return null;
                match += 4;
                if (d + match > size) return null;
                // Byte by byte: a match may overlap the bytes it is copying.
                for (int i = 0; i < match; i++) dst[d + i] = dst[d - offset + i];
                d += match;
            }
            return d == size ? dst : null;
        }

        private static bool ReadLength(byte[] src, ref int s, ref int length)
        {
            int b;
            do
            {
                if (s >= src.Length) return false;
                b = src[s++];
                length += b;
            }
            while (b == 255);
            return true;
        }
    }
}
