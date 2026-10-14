using System;
using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// "Busiest objects" watches every loaded object around the player for a few seconds and lists
    /// the ones whose changes take the most to send, by object, by type and by the game that runs
    /// them, so the player does not have to aim at the culprit. Objects that came into view or went
    /// meanwhile are counted apart, since their start or end is not known.
    /// </summary>
    public sealed class BusiestObjectsTests
    {
        private static BusiestObjects.Report Watch()
        {
            var start = new Dictionary<string, BusiestObjects.Start>
            {
                { "boar1", new BusiestObjects.Start(100, 0, 0, 0) },
                { "boar2", new BusiestObjects.Start(10, 5, 0, 5) },
                { "smelter", new BusiestObjects.Start(7, 20, 0, 0) },
                { "wall", new BusiestObjects.Start(3, 30, 0, 0) },
                { "gone", new BusiestObjects.Start(1, 40, 0, 0) },
            };
            var end = new List<BusiestObjects.Seen>
            {
                new BusiestObjects.Seen("boar1", "Boar", "D'Arcy", 700, 200, 12, 4, 0, 0),
                new BusiestObjects.Seen("boar2", "Boar", "D'Arcy", 60, 200, 15, 5, 0, 5),
                new BusiestObjects.Seen("smelter", "Smelter", "You", 17, 400, 20, 20, 0, 0),
                new BusiestObjects.Seen("wall", "Wood wall", "You", 3, 90, 30, 30, 0, 0),
                new BusiestObjects.Seen("new", "Deer", "Mason", 9, 150, 50, 50, 0, 0),
            };
            return BusiestObjects.Rank(start, end, 10);
        }

        [Fact]
        public void The_objects_that_take_the_most_to_send_come_first()
        {
            var report = Watch();
            Assert.Equal(new[] { "boar1", "boar2", "smelter" }, report.Objects.Select(o => o.Id).ToArray());
            Assert.Equal(60, report.Objects[0].PerSecond, 3);
            Assert.Equal(4000, report.Objects[0].BytesPerSecond, 3);
            Assert.True(report.Objects[0].Moved);
            Assert.False(report.Objects[2].Moved);
        }

        [Fact]
        public void A_small_object_that_changes_often_ranks_below_a_large_one_that_takes_more_to_send()
        {
            var start = new Dictionary<string, BusiestObjects.Start>
            {
                { "torch", new BusiestObjects.Start(100, 0, 0, 0) },
                { "smelter", new BusiestObjects.Start(7, 0, 0, 0) },
            };
            var end = new List<BusiestObjects.Seen>
            {
                new BusiestObjects.Seen("torch", "Torch", "You", 400, 10, 3, 0, 0, 0),
                new BusiestObjects.Seen("smelter", "Smelter", "You", 17, 400, 3, 0, 0, 0),
            };
            var report = BusiestObjects.Rank(start, end, 10);
            Assert.Equal(new[] { "smelter", "torch" }, report.Objects.Select(o => o.Id).ToArray());
        }

        [Fact]
        public void Objects_that_did_not_change_are_watched_but_not_listed()
        {
            var report = Watch();
            Assert.Equal(4, report.Watched);
            Assert.Equal(3, report.Changed);
            Assert.DoesNotContain(report.Objects, o => o.Id == "wall");
        }

        [Fact]
        public void Objects_that_came_into_view_or_went_meanwhile_are_counted_apart()
        {
            var report = Watch();
            Assert.Equal(2, report.CameOrWent);
            Assert.DoesNotContain(report.Objects, o => o.Id == "new" || o.Id == "gone");
        }

        [Fact]
        public void Types_and_games_add_up_their_objects()
        {
            var report = Watch();
            Assert.Equal("Boar", report.Types[0].Name);
            Assert.Equal(2, report.Types[0].Objects);
            Assert.Equal(4000 + 1000, report.Types[0].BytesPerSecond, 3);
            Assert.Equal("D'Arcy", report.Games[0].Name);
            Assert.Equal("You", report.Games[1].Name);
            Assert.Equal(5000 + 400, report.TotalBytesPerSecond, 3);
        }

        [Fact]
        public void The_result_names_the_heaviest_type_and_the_game_that_runs_most_of_it()
        {
            var r = BusiestResult.Build(DateTime.Now, Watch(), 64);
            var verdict = r.Blocks.OfType<VerdictBlock>().Single();
            Assert.Equal("In 10 s within 64 m, 3 of 4 objects changed: about 5.3 kB/s if every change went out. Most of it is Boar (2 objects, 4.9 kB/s), run by D'Arcy's game.", verdict.Text);
            Assert.Equal("about 5.3 kB/s", r.Badge);
            var table = r.Blocks.OfType<TableBlock>().Single();
            Assert.Equal(3, table.Rows.Count);
            Assert.Equal("Boar", table.Rows[0][0]);
        }

        [Fact]
        public void A_quiet_place_says_nothing_changed()
        {
            var start = new Dictionary<string, BusiestObjects.Start> { { "wall", new BusiestObjects.Start(3, 0, 0, 0) } };
            var end = new List<BusiestObjects.Seen> { new BusiestObjects.Seen("wall", "Wood wall", "You", 3, 90, 2, 0, 0, 0) };
            var r = BusiestResult.Build(DateTime.Now, BusiestObjects.Rank(start, end, 10), 64);
            var verdict = r.Blocks.OfType<VerdictBlock>().Single();
            Assert.Equal(ResultTone.Good, verdict.Tone);
            Assert.Equal("Nothing of the 1 object within 64 m changed in 10 s.", verdict.Text);
            Assert.Equal("nothing changed", r.Badge);
            Assert.Equal(ResultTone.Good, r.BadgeTone);
        }
    }
}
