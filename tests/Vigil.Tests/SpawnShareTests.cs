using System;
using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Shared spawn logs. A player whose sharing sends the spawn log passes what their game spawned
    /// (and what mods cancelled) to the server once a minute; the server keeps everyone's and gives
    /// its admins one view: first how many spawns appeared inside bases and where, then a line per
    /// game that opens to its spawns, then what mods cancelled. A base is named by the piece whose
    /// base area it is and its builder; a spot outside bases by its distance and direction from the
    /// world's start, since the game's map shows no coordinates.
    /// </summary>
    public sealed class SpawnShareTests
    {
        private static readonly DateTime Noon = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        private static SpawnRecord Greyling(double at, bool inBase = true) => new SpawnRecord
        {
            At = at, Id = "1:" + at, Creature = "Greyling", Source = SpawnSource.Biome, Detail = "Meadows", Entry = "Greyling", NightOnly = true,
            InsideBase = inBase, BasePiece = inBase ? "Workbench" : null, BaseBuilderId = inBase ? 77 : 0, BaseBuilder = inBase ? "Bjorn" : null,
            BaseX = 100f, BaseZ = 200f, X = 104f, Z = 198f, Distance = 58f, Level = 1,
        };

        private static SpawnRecord Boar(double at) => new SpawnRecord
        {
            At = at, Id = "2:" + at, Creature = "Boar", Source = SpawnSource.Biome, Detail = "Meadows", Entry = "Boar", Level = 2,
            X = 0f, Z = -600f, Distance = 61f,
        };

        private static SpawnRecord Cancelled(double at, string creature, string by) => new SpawnRecord
        {
            At = at, Creature = creature, Source = SpawnSource.Biome, Detail = "Meadows", Entry = creature, Cancelled = true, CancelledBy = by,
        };

        // A batch as it arrives on the server from one player, the spawns made the given seconds before noon.
        private static List<SharedSpawn> Arrived(string player, params SpawnRecord[] records) =>
            SpawnShare.Decode(SpawnShare.Encode(records, 10000, Noon), player, SpawnShare.MaxRows);

        [Fact]
        public void A_batch_holds_what_was_spawned_or_cancelled_since_the_last_send_oldest_first_at_most_the_limit()
        {
            var spawned = new[] { Boar(10), Boar(30), Boar(50) };
            var cancelled = new[] { Cancelled(20, "Neck", "Seasonality"), Cancelled(40, "Neck", "Seasonality") };
            var due = SpawnShare.Due(spawned, cancelled, since: 15, max: 3);
            Assert.Equal(new double[] { 20, 30, 40 }, due.Select(r => r.At).ToArray());
            Assert.Empty(SpawnShare.Due(spawned, cancelled, since: 50, max: 10));
        }

        [Fact]
        public void A_batch_travels_to_the_server_and_reads_back_whole_with_the_time_on_the_players_clock()
        {
            var greyling = Greyling(9940);
            var back = Arrived("Bjorn", greyling).Single();
            Assert.Equal("Bjorn", back.Player);
            Assert.Equal(Noon.AddSeconds(-60), back.Utc);
            var r = back.Record;
            Assert.Equal("Greyling", r.Creature);
            Assert.Equal(SpawnSource.Biome, r.Source);
            Assert.Equal("Meadows", r.Detail);
            Assert.True(r.NightOnly && r.InsideBase && !r.Cancelled && !r.AllowedInBase);
            Assert.Equal("Workbench", r.BasePiece);
            Assert.Equal(77, r.BaseBuilderId);
            Assert.Equal("Bjorn", r.BaseBuilder);
            Assert.Equal(100f, r.BaseX);
            Assert.Equal(198f, r.Z);

            var cancelled = Arrived("Rayk", Cancelled(9990, "Neck", "Seasonality, Spawn That")).Single().Record;
            Assert.True(cancelled.Cancelled);
            Assert.Equal("Seasonality, Spawn That", cancelled.CancelledBy);
        }

        [Fact]
        public void Names_with_tabs_line_breaks_or_the_messages_separators_cannot_break_a_batch()
        {
            var odd = Boar(9990);
            odd.Creature = "Bad\tname\nwith;parts=";
            var back = Arrived("Rayk", odd, Boar(9995));
            Assert.Equal(2, back.Count);
            Assert.DoesNotContain('\t', back[0].Record.Creature);
            Assert.DoesNotContain('\n', back[0].Record.Creature);
        }

        [Fact]
        public void Lines_that_do_not_read_are_skipped_and_a_batch_is_held_to_its_limit()
        {
            string text = SpawnShare.Encode(new[] { Boar(9990) }, 10000, Noon) + "\nnot a spawn\n\t\t\n";
            Assert.Single(SpawnShare.Decode(text, "Rayk", 10));
            string many = SpawnShare.Encode(Enumerable.Range(0, 20).Select(i => Boar(9900 + i)), 10000, Noon);
            Assert.Equal(5, SpawnShare.Decode(many, "Rayk", 5).Count);
        }

        [Fact]
        public void A_base_is_named_by_its_piece_and_builder_and_by_a_player_when_the_builder_is_unknown()
        {
            Assert.Equal("Workbench by Bjorn", SpawnShare.Place(Greyling(1)));
            var unknown = Greyling(1);
            unknown.BaseBuilder = null;
            Assert.Equal("Workbench by a player", SpawnShare.Place(unknown));
            Assert.Equal("Workbench by Rayk", SpawnShare.Place(unknown, id => id == 77 ? "Rayk" : null));
        }

        [Fact]
        public void A_spot_outside_bases_reads_as_its_distance_and_direction_from_the_worlds_start()
        {
            Assert.Equal("1.8 km NE of the start", SpawnShare.FromStart(1273f, 1273f));
            Assert.Equal("600 m S of the start", SpawnShare.FromStart(0f, -600f));
            Assert.Equal("250 m W of the start", SpawnShare.FromStart(-248f, 10f));
            Assert.Equal("at the start", SpawnShare.FromStart(20f, -10f));
        }

        [Fact]
        public void The_merged_view_leads_with_how_many_spawns_appeared_inside_bases_and_at_how_many_places()
        {
            var merge = new SpawnMerge();
            merge.Add("Bjorn", Arrived("Bjorn", Greyling(9900), Greyling(9902), Boar(9950)), Noon);
            var view = merge.View(Noon, 3600);
            Assert.Equal(2, view.InsideBases);
            Assert.Equal("2 spawns inside bases in the last hour, at 1 place.", view.Verdict);

            var calm = new SpawnMerge();
            calm.Add("Rayk", Arrived("Rayk", Boar(9950)), Noon);
            Assert.Equal("No spawns inside bases in the last 6 hours.", calm.View(Noon, 6 * 3600).Verdict);
            Assert.Equal("No spawns inside bases this session.", calm.View(Noon, 0).Verdict);
        }

        [Fact]
        public void Spawns_inside_bases_are_grouped_by_place_busiest_first_with_their_creatures()
        {
            var merge = new SpawnMerge();
            var boarInBase = Boar(9960);
            boarInBase.InsideBase = true;
            boarInBase.BasePiece = "Workbench";
            boarInBase.BaseBuilderId = 77;
            boarInBase.BaseBuilder = "Bjorn";
            boarInBase.BaseX = 101f;
            boarInBase.BaseZ = 201f;
            var farm = Greyling(9990);
            farm.BasePiece = "Campfire";
            farm.BaseBuilder = "Rayk";
            farm.BaseBuilderId = 88;
            farm.BaseX = 900f;
            farm.BaseZ = -40f;
            merge.Add("Bjorn", Arrived("Bjorn", Greyling(9900), Greyling(9901), Greyling(9902), boarInBase, farm), Noon);
            var places = merge.View(Noon, 3600).Places;
            Assert.Equal(new[] { "Workbench by Bjorn", "Campfire by Rayk" }, places.Select(p => p.Label).ToArray());
            Assert.Equal("Greyling ×3 (night only), Boar", places[0].Creatures);
            Assert.Equal(4, places[0].Count);
            Assert.Equal("40 s", places[0].Ago);
            Assert.Equal(100f, places[0].X);
            Assert.Equal(200f, places[0].Z);
        }

        [Fact]
        public void Each_game_has_a_line_with_its_spawns_those_in_a_base_and_when_it_last_sent_most_in_bases_first()
        {
            var merge = new SpawnMerge();
            merge.Add("Alfhild", Arrived("Alfhild", Boar(9900), Boar(9910), Boar(9920)), Noon.AddMinutes(-4));
            merge.Add("Bjorn", Arrived("Bjorn", Greyling(9950), Boar(9960)), Noon.AddMinutes(-2));
            var games = merge.View(Noon, 3600).Games;
            Assert.Equal(new[] { "Bjorn", "Alfhild" }, games.Select(g => g.Player).ToArray());
            Assert.Equal(2, games[0].Spawns);
            Assert.Equal(1, games[0].InBase);
            Assert.Equal("2 min ago", games[0].LastSent);
            Assert.Equal(3, games[1].Spawns);
            Assert.Equal(0, games[1].InBase);
        }

        [Fact]
        public void Cancelled_spawns_are_counted_by_the_mods_that_can_stop_them_and_not_as_spawns()
        {
            var merge = new SpawnMerge();
            merge.Add("Bjorn", Arrived("Bjorn", Cancelled(9900, "Greyling", "Seasonality"), Cancelled(9910, "Neck", "Seasonality"),
                Cancelled(9920, "Greyling", "Seasonality"), Cancelled(9930, "Troll", null)), Noon);
            var view = merge.View(Noon, 3600);
            Assert.Equal(0, view.Games.Single().Spawns);
            Assert.Equal("Seasonality", view.Cancelled[0].Mod);
            Assert.Equal(3, view.Cancelled[0].Count);
            Assert.Equal("Greyling, Neck", view.Cancelled[0].Creatures);
            Assert.Equal("a mod's check", view.Cancelled[1].Mod);
        }

        [Fact]
        public void Spawns_older_than_the_window_are_left_out_and_the_session_holds_everything_kept()
        {
            var merge = new SpawnMerge();
            merge.Add("Bjorn", Arrived("Bjorn", Greyling(10000 - 7200), Greyling(9990)), Noon);
            Assert.Equal(1, merge.View(Noon, 3600).InsideBases);
            Assert.Equal(2, merge.View(Noon, 0).InsideBases);
        }

        [Fact]
        public void A_games_line_opens_to_its_spawns_newest_first_with_places_instead_of_distances()
        {
            var merge = new SpawnMerge();
            merge.Add("Bjorn", Arrived("Bjorn", Boar(9700), Greyling(9940), Greyling(9941)), Noon);
            merge.Add("Rayk", Arrived("Rayk", Boar(9990)), Noon);
            var rows = merge.Rows("Bjorn", Noon, 3600);
            Assert.Equal(2, rows.Count);
            Assert.Equal("Greyling ×2", rows[0].What);
            Assert.Equal("1 min", rows[0].When);
            Assert.Equal("inside a base: Workbench by Bjorn", rows[0].Where);
            Assert.True(rows[0].InsideBase);
            Assert.Equal("Boar, 1 star", rows[1].What);
            Assert.Equal("600 m S of the start", rows[1].Where);
        }

        [Fact]
        public void The_server_names_a_builder_it_has_seen_when_the_spawning_game_could_not()
        {
            var merge = new SpawnMerge();
            var g = Greyling(9990);
            g.BaseBuilder = null;
            merge.Add("Bjorn", Arrived("Bjorn", g), Noon);
            Assert.Equal("Workbench by a player", merge.View(Noon, 3600).Places.Single().Label);
            merge.Learn(77, "Sigrun");
            Assert.Equal("Workbench by Sigrun", merge.View(Noon, 3600).Places.Single().Label);
            Assert.Equal("Sigrun", merge.NameOf(77));
            Assert.Null(merge.NameOf(5));
        }

        [Fact]
        public void The_view_and_a_games_spawns_reach_the_admin_whole()
        {
            var merge = new SpawnMerge();
            merge.Add("Bjorn", Arrived("Bjorn", Greyling(9900), Cancelled(9910, "Neck", "Seasonality")), Noon);
            var view = merge.View(Noon, 3600);
            var back = SpawnView.Decode(KeyValues.Decode(KeyValues.Encode(view.Encode())));
            Assert.Equal(view.Verdict, back.Verdict);
            Assert.Equal(view.InsideBases, back.InsideBases);
            Assert.Equal(view.Places[0].Label, back.Places[0].Label);
            Assert.Equal(view.Places[0].Creatures, back.Places[0].Creatures);
            Assert.Equal(view.Places[0].X, back.Places[0].X);
            Assert.Equal(view.Games[0].Player, back.Games[0].Player);
            Assert.Equal(view.Games[0].InBase, back.Games[0].InBase);
            Assert.Equal(view.Cancelled[0].Creatures, back.Cancelled[0].Creatures);

            var rows = merge.Rows("Bjorn", Noon, 3600);
            var rowsBack = SpawnView.DecodeRows(KeyValues.Decode(KeyValues.Encode(SpawnView.EncodeRows(rows))));
            Assert.Equal(rows.Select(r => r.What + "|" + r.Where + "|" + r.InsideBase), rowsBack.Select(r => r.What + "|" + r.Where + "|" + r.InsideBase));
        }

        [Fact]
        public void The_server_keeps_at_most_its_limit_dropping_the_oldest()
        {
            var merge = new SpawnMerge();
            for (int batch = 0; batch < SpawnMerge.Max / 100 + 2; batch++)
                merge.Add("Rayk", Arrived("Rayk", Enumerable.Range(0, 100).Select(i => Boar(9000 + i)).ToArray()), Noon);
            Assert.Equal(SpawnMerge.Max, merge.Count);
        }

        [Fact]
        public void Sending_a_spawn_log_is_open_to_every_player_but_reading_everyones_is_for_admins()
        {
            Assert.True(Greeting.ForEveryone(Greeting.SpawnLog));
            Assert.False(Greeting.ForEveryone(SpawnView.Command));
        }

        [Fact]
        public void Your_own_spawn_log_names_the_base_a_spawn_appeared_in()
        {
            Assert.Equal("58 m, inside a base: Workbench by Bjorn", SpawnLog.Where(Greyling(1)));
            var allowed = Greyling(1);
            allowed.AllowedInBase = true;
            Assert.Equal("58 m, inside a base: Workbench by Bjorn (its entry allows that)", SpawnLog.Where(allowed));
        }
    }
}
