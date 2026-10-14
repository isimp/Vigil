using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Vigil.Core
{
    /// <summary>
    /// The texts an assembly's code carries: every string written in its code sits once in the
    /// file's string heap ("#US"), so reading that heap gives them all without loading or running
    /// anything. The file is read in place: its headers lead to the heap, and only the heap is read
    /// whole. Used to find the names mods store values under (<see cref="ValueNames"/>).
    /// </summary>
    internal static class AssemblyTexts
    {
        private const int CliHeaderDirectory = 14;
        private const uint MetadataSignature = 0x424A5342;

        /// <summary>The texts, in the order the heap holds them; null for a file that is not a .NET assembly or is cut short.</summary>
        public static List<string> Read(Stream file)
        {
            try
            {
                return ReadHeap(new BinaryReader(file, Encoding.ASCII, leaveOpen: true));
            }
            catch (EndOfStreamException) { return null; }
            catch (IOException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        private static List<string> ReadHeap(BinaryReader r)
        {
            var file = r.BaseStream;
            if (file.Length < 0x40) return null;
            file.Position = 0;
            if (r.ReadUInt16() != 0x5A4D) return null; // "MZ"
            file.Position = 0x3C;
            long pe = r.ReadUInt32();
            if (pe + 24 > file.Length) return null;
            file.Position = pe;
            if (r.ReadUInt32() != 0x00004550) return null; // "PE\0\0"
            r.ReadUInt16();
            int sections = r.ReadUInt16();
            file.Position = pe + 20;
            int optionalSize = r.ReadUInt16();
            long optional = pe + 24;
            file.Position = optional;
            ushort magic = r.ReadUInt16();
            long directories = optional + (magic == 0x20B ? 112 : 96);
            file.Position = directories + (CliHeaderDirectory * 8);
            uint cliRva = r.ReadUInt32();
            if (cliRva == 0) return null;

            long sectionTable = optional + optionalSize;
            long Offset(uint rva)
            {
                for (int i = 0; i < sections; i++)
                {
                    file.Position = sectionTable + (i * 40) + 8;
                    uint virtualSize = r.ReadUInt32(), virtualAddress = r.ReadUInt32(), rawSize = r.ReadUInt32(), rawPointer = r.ReadUInt32();
                    if (rva >= virtualAddress && rva < virtualAddress + Math.Max(virtualSize, rawSize)) return (long)rva - virtualAddress + rawPointer;
                }

                throw new EndOfStreamException("no section holds the address");
            }

            file.Position = Offset(cliRva) + 8;
            uint metadataRva = r.ReadUInt32();
            long metadata = Offset(metadataRva);
            file.Position = metadata;
            if (r.ReadUInt32() != MetadataSignature) return null;
            file.Position = metadata + 12;
            int versionLength = r.ReadInt32();
            if (versionLength < 0 || versionLength > 256) return null;
            file.Position = metadata + 16 + versionLength + 2;
            int streams = r.ReadUInt16();
            long heap = -1;
            int heapSize = 0;
            for (int i = 0; i < streams; i++)
            {
                uint offset = r.ReadUInt32(), size = r.ReadUInt32();
                var name = new StringBuilder();
                for (byte b = r.ReadByte(); b != 0; b = r.ReadByte()) name.Append((char)b);
                file.Position = (file.Position + 3) & ~3L;
                if (name.ToString() == "#US")
                {
                    heap = metadata + offset;
                    heapSize = (int)size;
                }
            }

            var texts = new List<string>();
            if (heap < 0) return texts;
            if (heap + heapSize > file.Length) return null;
            file.Position = heap;
            byte[] bytes = r.ReadBytes(heapSize);
            if (bytes.Length < heapSize) return null;

            // A blob each: its length (one, two or four bytes), the text in UTF-16, one byte more.
            int at = 1;
            while (at < bytes.Length)
            {
                int first = bytes[at], length;
                if ((first & 0x80) == 0)
                {
                    length = first;
                    at += 1;
                }
                else if ((first & 0xC0) == 0x80 && at + 1 < bytes.Length)
                {
                    length = ((first & 0x3F) << 8) | bytes[at + 1];
                    at += 2;
                }
                else if ((first & 0xE0) == 0xC0 && at + 3 < bytes.Length)
                {
                    length = ((first & 0x1F) << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];
                    at += 4;
                }
                else
                {
                    break;
                }

                if (length == 0) continue;
                if (at + length > bytes.Length) break;
                texts.Add(Encoding.Unicode.GetString(bytes, at, length - 1));
                at += length;
            }

            return texts;
        }
    }
}
