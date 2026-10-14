using System;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// A finished load test shows in the panel: a verdict per direction, per step the share of the
    /// traffic that got through against the line the verdict draws, the step where it fell behind
    /// marked, the verdict's own reason, frame time quiet against busy, and every step as a table.
    /// </summary>
    public sealed class LoadResultTests
    {
        private static readonly LoadPlan Up = LoadPlan.For("up");
        private static readonly LoadPlan Players = LoadPlan.ForPlayers(8);
        private static readonly DateTime At = new DateTime(2026, 10, 13, 14, 12, 0);

        private static void FillUp(LoadLedger l, string stage, double recv = double.NaN, double stale = 0.2, double queue = 1)
        {
            var s = Up.Stages.First(x => x.Name == stage);
            for (int i = 0; i < 10; i++)
            {
                l.Add(s.Index, i, LoadMetrics.UpRecv, double.IsNaN(recv) ? s.RateKbps : recv);
                l.Add(s.Index, i, LoadMetrics.UpStale, stale);
                l.Add(s.Index, i, LoadMetrics.ServerFrame, 20);
                l.Add(s.Index, i, LoadMetrics.ServerFrameMax, 30);
                l.Add(s.Index, i, LoadMetrics.ClientFrame, 16);
                l.Add(s.Index, i, LoadMetrics.ClientQueue, queue);
                l.Add(s.Index, i, LoadMetrics.ClientFps, 60);
            }
        }

        private static LoadLedger CalmUp(params string[] except)
        {
            var l = new LoadLedger();
            foreach (var s in Up.Stages.Where(s => s.Kind != LoadPlan.Kind.Recovery && !except.Contains(s.Name))) FillUp(l, s.Name);
            return l;
        }

        private static LoadLedger FellBehindAt200()
        {
            var l = CalmUp("up 200 kB/s", "up 400 kB/s", "up 800 kB/s");
            FillUp(l, "up 200 kB/s", recv: 150, stale: 3, queue: 9);
            FillUp(l, "up 400 kB/s", recv: 152, stale: 6, queue: 9);
            FillUp(l, "up 800 kB/s", recv: 151, stale: 9, queue: 9);
            return l;
        }

        [Fact]
        public void The_step_that_fell_behind_is_marked_and_the_steps_before_it_kept_up()
        {
            var chart = LoadResult.Build(Up, FellBehindAt200(), null, At).Blocks.OfType<ColumnsBlock>().Single();
            Assert.Equal(new[] { "50", "100", "200", "400", "800" }, chart.Columns.Select(c => c.Label).ToArray());
            Assert.Equal(new[] { ResultTone.Good, ResultTone.Good, ResultTone.Caution, ResultTone.Quiet, ResultTone.Quiet }, chart.Columns.Select(c => c.Tone).ToArray());
            Assert.Equal(0.75, chart.Columns[2].Value, 3);
            Assert.Equal(LoadVerdict.KeptUpShare, chart.Threshold);
        }

        [Fact]
        public void The_verdict_says_how_far_the_direction_kept_up_and_where_it_gave()
        {
            var r = LoadResult.Build(Up, FellBehindAt200(), null, At);
            var verdict = r.Blocks.OfType<VerdictBlock>().Single();
            Assert.Equal(ResultTone.Caution, verdict.Tone);
            Assert.Contains("kept up through 100 kB/s", verdict.Text);
            Assert.Contains("75%", verdict.Text);
            Assert.Equal("upload fell behind", r.Badge);
            Assert.Equal(ResultTone.Caution, r.BadgeTone);
        }

        [Fact]
        public void What_held_it_back_is_the_verdicts_own_reason()
        {
            var l = FellBehindAt200();
            string judged = string.Join("\n", LoadVerdict.Judge(Up, l, null));
            var reason = LoadResult.Build(Up, l, null, At).Blocks.OfType<TextBlock>().First(b => b.Title == "What held the upload back");
            Assert.Contains("connection", reason.Text);
            Assert.Contains(reason.Text, judged);
        }

        [Fact]
        public void A_test_that_kept_up_at_every_step_reads_fine()
        {
            var r = LoadResult.Build(Up, CalmUp(), null, At);
            Assert.Equal(ResultTone.Good, r.Blocks.OfType<VerdictBlock>().Single().Tone);
            Assert.Contains("the highest step", r.Blocks.OfType<VerdictBlock>().Single().Text);
            Assert.Equal("kept up", r.Badge);
            Assert.DoesNotContain(r.Blocks.OfType<TextBlock>(), b => b.Title != null && b.Title.StartsWith("What held", StringComparison.Ordinal));
        }

        [Fact]
        public void A_test_stopped_early_says_so_first()
        {
            var r = LoadResult.Build(Up, CalmUp(), "another player joined", At);
            var first = r.Blocks.OfType<VerdictBlock>().First();
            Assert.Equal(ResultTone.Caution, first.Tone);
            Assert.Contains("Stopped early: another player joined", first.Text);
            Assert.Equal("stopped early", r.Badge);
        }

        [Fact]
        public void Every_step_is_in_the_table()
        {
            var table = LoadResult.Build(Up, CalmUp(), null, At).Blocks.OfType<TableBlock>().Single();
            Assert.Equal(6, table.Rows.Count);
            Assert.Equal("baseline", table.Rows[0][0]);
        }

        private static void FillPlayers(LoadLedger l, string stage, double frame = 33.3, double max = 40, double work = double.NaN, double stale = 0.3)
        {
            var s = Players.Stages.First(x => x.Name == stage);
            for (int i = 0; i < 10; i++)
            {
                l.Add(s.Index, i, LoadMetrics.ServerFrame, frame);
                l.Add(s.Index, i, LoadMetrics.ServerFrameMax, max);
                l.Add(s.Index, i, LoadMetrics.ServerWork, double.IsNaN(work) ? 4 + 1.5 * s.Players : work);
                l.Add(s.Index, i, LoadMetrics.DownStale, stale);
                l.Add(s.Index, i, LoadMetrics.Rtt, 50);
            }
        }

        private static LoadLedger CalmPlayers(params string[] except)
        {
            var l = new LoadLedger();
            foreach (var s in Players.Stages.Where(s => s.Kind != LoadPlan.Kind.Recovery && !except.Contains(s.Name))) FillPlayers(l, s.Name);
            return l;
        }

        [Fact]
        public void The_player_simulation_marks_where_the_server_gave()
        {
            var l = CalmPlayers("8 players");
            FillPlayers(l, "8 players", frame: 70, max: 160, work: 65);
            var r = LoadResult.BuildPlayers(Players, l, null, "stay", At);
            var chart = r.Blocks.OfType<ColumnsBlock>().Single();
            Assert.Equal(new[] { "1", "2", "4", "8" }, chart.Columns.Select(c => c.Label).ToArray());
            Assert.Equal(new[] { ResultTone.Good, ResultTone.Good, ResultTone.Good, ResultTone.Caution }, chart.Columns.Select(c => c.Tone).ToArray());
            Assert.Equal(65, chart.Columns[3].Value, 3);
            Assert.Equal(LoadVerdict.ServerBudgetMs, chart.Threshold);
            Assert.Equal("gave at 8 players", r.Badge);
            Assert.Contains("kept up with 4", r.Blocks.OfType<VerdictBlock>().Single().Text);
        }

        [Fact]
        public void A_player_simulation_that_kept_up_says_with_how_many()
        {
            var r = LoadResult.BuildPlayers(Players, CalmPlayers(), null, "stay", At);
            Assert.Equal("kept up with 8", r.Badge);
            Assert.Equal(ResultTone.Good, r.BadgeTone);
            Assert.Contains(r.Blocks.OfType<TextBlock>(), b => b.Text.Contains("1.50 ms of server work per frame", StringComparison.Ordinal));
        }
    }
}
