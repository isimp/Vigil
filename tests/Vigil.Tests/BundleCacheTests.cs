using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The bundle cache: a mod's asset bundle packed with LZMA has to be unpacked whole every time the
    /// game starts, so the first load writes a fast-loading LZ4 copy and later loads read that copy
    /// instead. These tests cover which bundles qualify, how a bundle is recognised again, which load
    /// takes which way, and what the cache keeps between starts.
    /// </summary>
    public sealed class BundleCacheTests
    {
        private const int Lzma = 1, Lz4Hc = 3, None = 0;

        // ------------------------------------------------------------------ test bundles

        /// <summary>A Unity bundle as Unity writes one: header, block table (LZ4, literal-only, which is valid LZ4), data.</summary>
        private static byte[] Bundle(int[] blockKinds, long dataBytes = 2_000_000, int version = 8, bool tableAtEnd = false, string unity = "6000.0.75f1")
        {
            var table = new MemoryStream();
            table.Write(new byte[16], 0, 16);
            BE(table, blockKinds.Length);
            foreach (int kind in blockKinds) { BE(table, 131072); BE(table, (int)(dataBytes / blockKinds.Length)); BE16(table, (short)(kind | 0x40)); }
            BE(table, 1);
            BE64(table, 0); BE64(table, 131072L * blockKinds.Length); BE(table, 4); Cstr(table, "CAB-test");
            byte[] raw = table.ToArray();
            byte[] packed = Lz4LiteralsOnly(raw);

            var head = new MemoryStream();
            Cstr(head, "UnityFS"); BE(head, version); Cstr(head, "5.x.x"); Cstr(head, unity);
            long sizeAt = head.Position; BE64(head, 0);
            BE(head, packed.Length); BE(head, raw.Length); BE(head, Lz4Hc | (tableAtEnd ? 0x80 : 0) | 0x40);
            if (version >= 7) while (head.Position % 16 != 0) head.WriteByte(0);
            if (!tableAtEnd) head.Write(packed, 0, packed.Length);
            var data = new byte[dataBytes];
            new Random(7).NextBytes(data);
            head.Write(data, 0, data.Length);
            if (tableAtEnd) head.Write(packed, 0, packed.Length);
            byte[] all = head.ToArray();
            byte[] size = BitConverter.GetBytes(all.LongLength); Array.Reverse(size);
            Array.Copy(size, 0, all, sizeAt, 8);
            return all;
        }

        private static void BE(Stream s, int v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); s.Write(b, 0, 4); }
        private static void BE16(Stream s, short v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); s.Write(b, 0, 2); }
        private static void BE64(Stream s, long v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); s.Write(b, 0, 8); }
        private static void Cstr(Stream s, string v) { var b = Encoding.ASCII.GetBytes(v); s.Write(b, 0, b.Length); s.WriteByte(0); }

        private static byte[] Lz4LiteralsOnly(byte[] raw)
        {
            var o = new MemoryStream();
            int n = raw.Length;
            o.WriteByte((byte)(Math.Min(n, 15) << 4));
            if (n >= 15) { int rest = n - 15; while (rest >= 255) { o.WriteByte(255); rest -= 255; } o.WriteByte((byte)rest); }
            o.Write(raw, 0, n);
            return o.ToArray();
        }

        private static Func<long, int, byte[]> Reader(byte[] b) => (at, count) =>
        {
            var r = new byte[Math.Max(0, Math.Min(count, b.Length - (int)at))];
            Array.Copy(b, at, r, 0, r.Length);
            return r;
        };

        private static BundleShape Shape(byte[] b) => BundleFormat.Read(Reader(b), b.LongLength);

        // ------------------------------------------------------------------ LZ4

        // Written by the reference LZ4 encoder in high-compression mode, so it holds matches and long runs.
        private const string Lz4HcSample = "ff03566967696c2062756e646c652074657374201200ff44f635000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f4341422d14006f616263646566140010506263646566";

        [Fact]
        public void The_block_table_unpacks_as_the_reference_LZ4_encoder_packed_it()
        {
            byte[] plain = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Vigil bundle test ", 20)))
                .Concat(Enumerable.Range(0, 64).Select(i => (byte)i))
                .Concat(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("CAB-0123456789abcdef", 3)))).ToArray();
            byte[] packed = Convert.FromHexString(Lz4HcSample);
            Assert.Equal(plain, Lz4Block.Decode(packed, plain.Length));
        }

        [Fact]
        public void A_damaged_block_table_is_refused_rather_than_misread()
        {
            byte[] packed = Convert.FromHexString(Lz4HcSample);
            Assert.Null(Lz4Block.Decode(packed.Take(20).ToArray(), 484));
            Assert.Null(Lz4Block.Decode(packed, 10));
        }

        // ------------------------------------------------------------------ which bundles

        [Fact]
        public void A_bundle_whose_blocks_are_LZMA_qualifies_for_a_fast_copy()
        {
            var s = Shape(Bundle(new[] { Lzma, Lzma }));
            Assert.True(s.UnityFs);
            Assert.Equal(2, s.LzmaBlocks);
            Assert.True(s.WorthACopy);
            Assert.Equal("6000.0.75f1", s.Revision);
        }

        [Fact]
        public void A_bundle_already_in_LZ4_or_uncompressed_is_left_alone()
        {
            Assert.False(Shape(Bundle(new[] { Lz4Hc, Lz4Hc })).WorthACopy);
            Assert.False(Shape(Bundle(new[] { None })).WorthACopy);
        }

        [Fact]
        public void A_bundle_with_one_LZMA_block_among_others_still_qualifies()
        {
            Assert.True(Shape(Bundle(new[] { Lz4Hc, Lzma })).WorthACopy);
        }

        [Fact]
        public void The_block_table_is_found_at_the_end_and_in_older_formats_too()
        {
            Assert.True(Shape(Bundle(new[] { Lzma }, tableAtEnd: true)).WorthACopy);
            Assert.True(Shape(Bundle(new[] { Lzma }, version: 6)).WorthACopy);
        }

        [Fact]
        public void Data_that_is_not_a_Unity_bundle_is_left_alone()
        {
            var junk = new byte[4096];
            new Random(1).NextBytes(junk);
            var s = Shape(junk);
            Assert.False(s.UnityFs);
            Assert.False(s.WorthACopy);
            var cut = Bundle(new[] { Lzma }).Take(40).ToArray();
            Assert.False(Shape(cut).WorthACopy);
        }

        // ------------------------------------------------------------------ recognising a bundle again

        [Fact]
        public void The_same_bundle_gets_the_same_key_on_every_start()
        {
            byte[] b = Bundle(new[] { Lzma });
            Assert.Equal(BundleKey.Of(Reader(b), b.LongLength, "6000.0.75f1"), BundleKey.Of(Reader((byte[])b.Clone()), b.LongLength, "6000.0.75f1"));
        }

        [Fact]
        public void A_changed_byte_near_the_end_gives_a_new_key()
        {
            byte[] b = Bundle(new[] { Lzma });
            byte[] c = (byte[])b.Clone();
            c[c.Length - 100] ^= 0xFF;
            Assert.NotEqual(BundleKey.Of(Reader(b), b.LongLength, "6000.0.75f1"), BundleKey.Of(Reader(c), c.LongLength, "6000.0.75f1"));
        }

        [Fact]
        public void Another_Unity_version_gives_a_new_key()
        {
            byte[] b = Bundle(new[] { Lzma });
            Assert.NotEqual(BundleKey.Of(Reader(b), b.LongLength, "6000.0.75f1"), BundleKey.Of(Reader(b), b.LongLength, "6000.0.76f1"));
        }

        [Fact]
        public void A_key_is_a_plain_file_name()
        {
            byte[] b = Bundle(new[] { Lzma });
            string key = BundleKey.Of(Reader(b), b.LongLength, "6000.0.75f1");
            Assert.Matches("^[0-9a-f-]+$", key);
        }

        // ------------------------------------------------------------------ which way a load goes

        private static BundleChoice Choose(BundleShape s, uint crc = 0, bool seekable = true, bool ready = false, bool blocked = false, bool converting = false, bool on = true, long length = 2_000_000)
            => BundleCache.Choose(s, length, crc, seekable, ready, blocked, converting, on);

        [Fact]
        public void A_bundle_with_a_ready_copy_loads_from_the_copy()
        {
            Assert.Equal(BundleChoice.Copy, Choose(Shape(Bundle(new[] { Lzma })), ready: true));
        }

        [Fact]
        public void A_bundle_seen_the_first_time_loads_as_usual_and_gets_a_copy_made()
        {
            Assert.Equal(BundleChoice.UsualAndConvert, Choose(Shape(Bundle(new[] { Lzma }))));
        }

        [Fact]
        public void A_copy_is_made_once_a_session_even_if_the_bundle_loads_again()
        {
            Assert.Equal(BundleChoice.Usual, Choose(Shape(Bundle(new[] { Lzma })), converting: true));
        }

        [Fact]
        public void A_load_that_asks_for_a_checksum_is_never_redirected()
        {
            Assert.Equal(BundleChoice.Usual, Choose(Shape(Bundle(new[] { Lzma })), crc: 12345, ready: true));
        }

        [Fact]
        public void A_bundle_whose_copy_failed_once_always_loads_as_usual()
        {
            Assert.Equal(BundleChoice.Usual, Choose(Shape(Bundle(new[] { Lzma })), ready: true, blocked: true));
        }

        [Fact]
        public void A_stream_that_cannot_be_read_twice_loads_as_usual()
        {
            Assert.Equal(BundleChoice.Usual, Choose(Shape(Bundle(new[] { Lzma })), seekable: false, ready: true));
        }

        [Fact]
        public void With_the_cache_switched_off_every_load_goes_as_usual()
        {
            Assert.Equal(BundleChoice.Usual, Choose(Shape(Bundle(new[] { Lzma })), ready: true, on: false));
        }

        [Fact]
        public void A_small_bundle_is_not_worth_a_copy()
        {
            Assert.Equal(BundleChoice.Usual, Choose(Shape(Bundle(new[] { Lzma })), length: 200_000));
        }

        [Fact]
        public void The_cache_is_on_in_a_players_game_and_off_on_a_dedicated_server_unless_switched_on_there()
        {
            Assert.True(BundleCache.On(server: false, playerSwitch: true, serverSwitch: false));
            Assert.False(BundleCache.On(server: true, playerSwitch: true, serverSwitch: false));
            Assert.True(BundleCache.On(server: true, playerSwitch: false, serverSwitch: true));
            Assert.False(BundleCache.On(server: false, playerSwitch: false, serverSwitch: true));
            Assert.True(BundleCache.IsDedicatedServer("valheim_server"));
            Assert.True(BundleCache.IsDedicatedServer("valheim_server.x86_64"));
            Assert.False(BundleCache.IsDedicatedServer("valheim"));
        }

        // ------------------------------------------------------------------ what the cache keeps

        private static readonly DateTime Now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void The_index_survives_the_round_trip_through_its_file()
        {
            var e = new BundleEntry("k1", Now, 75_000_000, 120_000_000, BundleEntry.Ready, "12:abc", "Balrond furniture");
            var back = BundleCacheIndex.Parse(BundleCacheIndex.Format(new[] { e })).Single();
            Assert.Equal("k1", back.Key);
            Assert.Equal(Now.Date, back.LastUsed.Date);
            Assert.Equal(75_000_000, back.PackedBytes);
            Assert.Equal(120_000_000, back.CopyBytes);
            Assert.Equal(BundleEntry.Ready, back.State);
            Assert.Equal("12:abc", back.Assets);
            Assert.Equal("Balrond furniture", back.Plugin);
            Assert.Empty(BundleCacheIndex.Parse("garbage line\n\n"));
        }

        private static BundleHousekeeping Keep(IEnumerable<BundleEntry> entries, IEnumerable<string> files, bool reset = false, int days = 30)
            => BundleCache.Housekeep(entries.ToList(), files.ToList(), Now, days, reset);

        [Fact]
        public void Copies_used_within_the_keep_days_stay()
        {
            var e = new BundleEntry("k1", Now.AddDays(-29), 1, 1, BundleEntry.Ready, "", "");
            var h = Keep(new[] { e }, new[] { "k1.bundle" });
            Assert.Empty(h.Delete);
            Assert.Single(h.Keep);
        }

        [Fact]
        public void Copies_unused_for_longer_than_the_keep_days_go()
        {
            var e = new BundleEntry("k1", Now.AddDays(-31), 1, 1, BundleEntry.Ready, "", "");
            var h = Keep(new[] { e }, new[] { "k1.bundle" });
            Assert.Equal(new[] { "k1.bundle" }, h.Delete);
            Assert.Empty(h.Keep);
        }

        [Fact]
        public void Half_made_copies_and_their_sources_from_an_earlier_start_go()
        {
            var h = Keep(Array.Empty<BundleEntry>(), new[] { "k2.tmp", "k2.src" });
            Assert.Equal(new[] { "k2.src", "k2.tmp" }, h.Delete.OrderBy(x => x).ToArray());
        }

        [Fact]
        public void A_copy_the_index_does_not_know_goes()
        {
            var h = Keep(Array.Empty<BundleEntry>(), new[] { "k3.bundle" });
            Assert.Equal(new[] { "k3.bundle" }, h.Delete);
        }

        [Fact]
        public void An_entry_whose_copy_is_missing_is_dropped_but_a_blocked_one_is_remembered()
        {
            var ready = new BundleEntry("k4", Now, 1, 1, BundleEntry.Ready, "", "");
            var blocked = new BundleEntry("k5", Now, 1, 0, BundleEntry.Blocked, "", "");
            var h = Keep(new[] { ready, blocked }, Array.Empty<string>());
            Assert.Equal(new[] { "k5" }, h.Keep.Select(e => e.Key).ToArray());
        }

        [Fact]
        public void A_reset_empties_the_whole_cache_before_the_first_load()
        {
            var e = new BundleEntry("k1", Now, 1, 1, BundleEntry.Ready, "", "");
            var h = Keep(new[] { e }, new[] { "k1.bundle", "k9.src", "index.tsv", "reset" }, reset: true);
            Assert.True(h.Reset);
            Assert.Empty(h.Keep);
            Assert.Equal(new[] { "index.tsv", "k1.bundle", "k9.src", "reset" }, h.Delete.OrderBy(x => x).ToArray());
        }

        [Fact]
        public void A_copy_holds_the_same_assets_as_its_original_whatever_their_order()
        {
            string a = BundleCache.AssetsSignature(new[] { "assets/b.prefab", "assets/a.prefab" });
            string b = BundleCache.AssetsSignature(new[] { "assets/a.prefab", "assets/b.prefab" });
            string c = BundleCache.AssetsSignature(new[] { "assets/a.prefab" });
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
            Assert.StartsWith("2:", a);
        }

        [Fact]
        public void The_cache_row_counts_copies_and_size()
        {
            var entries = new[]
            {
                new BundleEntry("k1", Now, 75_000_000, 120_000_000, BundleEntry.Ready, "", ""),
                new BundleEntry("k2", Now, 20_000_000, 40_000_000, BundleEntry.Ready, "", ""),
                new BundleEntry("k3", Now, 5_000_000, 0, BundleEntry.Blocked, "", ""),
            };
            Assert.Equal("2 fast copies, 160 MB on disk, 1 bundle that loads the usual way", BundleCache.DiskLine(entries));
            Assert.Equal("no fast copies yet", BundleCache.DiskLine(Array.Empty<BundleEntry>()));
        }
    }
}
