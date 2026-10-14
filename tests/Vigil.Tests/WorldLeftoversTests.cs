using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// "Leftovers in the world": the server reads every object the world keeps and lists those whose
    /// prefab no installed mod provides, by prefab, most first, each with where they lie; and it reads
    /// the world's player history, telling the entries load tests left (simulated players named
    /// "NetProbe player N" or "Vigil player N") from real players, so an admin can remove them.
    /// </summary>
    public sealed class WorldLeftoversTests
    {
        private static readonly int OdinHorse = StableHash.Of("rae_OdinHorse");

        private static WorldScan Scan()
        {
            var scan = new WorldScan();
            for (int i = 0; i < 3; i++) scan.Read(42, 780f + i, 780f, exists: false);
            scan.Read(42, -3400f, 10f, exists: false);
            scan.Read(OdinHorse, 0f, -620f, exists: false);
            for (int i = 0; i < 10; i++) scan.Read(1000 + i, 5f, 5f, exists: true);
            scan.Read(0, 0f, 0f, exists: false);
            return scan;
        }

        [Fact]
        public void The_scan_counts_every_object_and_keeps_only_those_no_installed_mod_provides()
        {
            var scan = Scan();
            Assert.Equal(16, scan.Objects);
            Assert.Equal(5, scan.Leftovers);
            Assert.Equal(2, scan.Kinds().Count);
        }

        [Fact]
        public void Kinds_are_listed_most_first_named_from_the_known_list_else_by_number()
        {
            var kinds = Scan().Kinds();
            Assert.Equal(new[] { 42, OdinHorse }, kinds.Select(k => k.Hash).ToArray());
            Assert.Equal(4, kinds[0].Count);
            Assert.Equal("unknown prefab, hash 42", kinds[0].Name);
            Assert.Equal("rae_OdinHorse (OdinHorse, removed)", kinds[1].Name);
            Assert.Equal("620 m S of the start", kinds[1].Where);
        }

        [Fact]
        public void Where_names_up_to_two_places_most_first_with_their_counts_and_says_how_many_more()
        {
            Assert.Equal("2 places: 1.1 km NE of the start (3), 3.4 km W of the start (1)", Scan().Kinds()[0].Where);
            var spread = new WorldScan();
            foreach (var x in new[] { 0f, 1000f, 2000f, 3000f }) spread.Read(7, x, 0f, exists: false);
            spread.Read(7, 1000f, 10f, exists: false);
            Assert.Equal("4 places: 1.0 km E of the start (2), at the start (1), and 2 more", spread.Kinds()[0].Where);
        }

        [Fact]
        public void The_verdict_counts_the_objects_and_kinds_or_says_the_world_has_none()
        {
            Assert.Equal("5 objects of 2 kinds that no installed mod provides. The game warns about each whenever its area loads.", Scan().Verdict);
            var clean = new WorldScan();
            clean.Read(1, 0f, 0f, exists: true);
            Assert.Equal("No objects of removed mods in the world.", clean.Verdict);
            var one = new WorldScan();
            one.Read(9, 0f, 0f, exists: false);
            Assert.Equal("1 object of 1 kind that no installed mod provides. The game warns about it whenever its area loads.", one.Verdict);
        }

        [Fact]
        public void Load_test_bots_are_told_from_players_by_their_ids()
        {
            Assert.True(PlayerHistory.IsBot("Steam_Vigil player 3"));
            Assert.True(PlayerHistory.IsBot("Steam_NetProbe player 16"));
            Assert.True(PlayerHistory.IsBot("Vigil player 1"));
            Assert.False(PlayerHistory.IsBot("Steam_76561198056255791"));
            Assert.False(PlayerHistory.IsBot("Steam_Vigil player"));
            Assert.False(PlayerHistory.IsBot("Steam_Vigil player 3x"));
            Assert.False(PlayerHistory.IsBot("Steam_Not Vigil player 3"));
            Assert.False(PlayerHistory.IsBot(null));
        }

        [Fact]
        public void The_history_reads_as_players_and_bots_with_the_bots_numbers_as_ranges()
        {
            var ids = new List<string> { "Steam_1", "Steam_2", "Steam_3", "Steam_4" };
            ids.AddRange(Enumerable.Range(1, 16).Select(i => "Steam_NetProbe player " + i));
            ids.AddRange(Enumerable.Range(1, 16).Select(i => "Steam_Vigil player " + i));
            Assert.Equal("36 entries: 4 players and 32 load-test bots (NetProbe player 1–16, Vigil player 1–16)", PlayerHistory.Summary(ids));
            Assert.Equal(32, PlayerHistory.Bots(ids));

            var gaps = new[] { "Steam_1", "Steam_Vigil player 1", "Steam_Vigil player 2", "Steam_Vigil player 3", "Steam_Vigil player 5" };
            Assert.Equal("5 entries: 1 player and 4 load-test bots (Vigil player 1–3, 5)", PlayerHistory.Summary(gaps));
            Assert.Equal("2 entries, all players", PlayerHistory.Summary(new[] { "Steam_1", "Steam_2" }));
            Assert.Equal("1 entry: 1 load-test bot (Vigil player 7)", PlayerHistory.Summary(new[] { "Steam_Vigil player 7" }));
        }

        [Fact]
        public void A_scan_under_way_says_how_far_it_got_and_a_finished_one_what_it_read_and_when()
        {
            var running = new LeftoverReport { State = LeftoverReport.Scanning, Read = 120000, Total = 412380 };
            Assert.Equal("Reading the world: 120,000 of 412,380 objects", running.StateLine());
            var done = new LeftoverReport { State = LeftoverReport.Done, Read = 412380, Total = 412380, Seconds = 4.2, Ago = 190 };
            Assert.Equal("412,380 objects, read in 4 s, 3 min ago", done.StateLine());
            Assert.Equal("Not read yet: Scan reads every object the server keeps", new LeftoverReport().StateLine());
        }

        [Fact]
        public void The_report_reaches_the_admin_whole()
        {
            var scan = Scan();
            var report = new LeftoverReport
            {
                State = LeftoverReport.Done, Read = 16, Total = 16, Seconds = 0.5, Ago = 12, Verdict = scan.Verdict, Kinds = scan.Kinds(),
                History = "36 entries: 4 players and 32 load-test bots (NetProbe player 1–16, Vigil player 1–16)", Bots = 32, Note = "saved the world, then removed 4",
            };
            var back = LeftoverReport.Decode(KeyValues.Decode(KeyValues.Encode(report.Encode())));
            Assert.Equal(report.State, back.State);
            Assert.Equal(report.Total, back.Total);
            Assert.Equal(report.Verdict, back.Verdict);
            Assert.Equal(report.History, back.History);
            Assert.Equal(32, back.Bots);
            Assert.Equal(report.Note, back.Note);
            Assert.Equal(report.Kinds.Select(k => k.Hash + "|" + k.Name + "|" + k.Count + "|" + k.Where), back.Kinds.Select(k => k.Hash + "|" + k.Name + "|" + k.Count + "|" + k.Where));
        }

        [Fact]
        public void Every_object_of_a_kind_is_kept_for_removal_by_its_id()
        {
            var scan = new WorldScan();
            scan.Read(42, 0f, 0f, exists: false, id: "1:10");
            scan.Read(42, 1f, 0f, exists: false, id: "1:11");
            scan.Read(7, 1f, 0f, exists: false, id: "2:1");
            Assert.Equal(new[] { "1:10", "1:11" }, scan.IdsOf(42).Cast<string>().ToArray());
            Assert.Empty(scan.IdsOf(99));
        }
    }
}
