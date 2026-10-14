using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Every second of a load test is filed under its step; a step is judged on its steady part,
    /// after the first seconds in which the traffic is still building up.
    /// </summary>
    public sealed class LoadLedgerTests
    {
        [Fact]
        public void A_step_summary_gives_mean_peak_and_worst()
        {
            var l = new LoadLedger();
            for (int i = 0; i < 20; i++) l.Add(1, i, "frame", i + 1);
            var s = l.Get(1, "frame");
            Assert.Equal(20, s.Count);
            Assert.Equal(10.5, s.Mean, 3);
            Assert.Equal(20, s.Max);
            Assert.Equal(1, s.Min);
            Assert.Equal(19, s.P95);
            Assert.Equal(210, s.Sum);
        }

        [Fact]
        public void The_steady_part_leaves_out_the_first_seconds_of_a_step()
        {
            var l = new LoadLedger();
            l.Add(2, 0, "kbs", 900);
            l.Add(2, 1, "kbs", 900);
            l.Add(2, 2, "kbs", 900);
            l.Add(2, 3, "kbs", 100);
            l.Add(2, 4, "kbs", 100);
            Assert.Equal(100, l.Get(2, "kbs", skipSeconds: 3).Mean);
            Assert.Equal(580, l.Get(2, "kbs").Mean);
        }

        [Fact]
        public void A_short_step_is_judged_on_all_it_has_rather_than_nothing()
        {
            var l = new LoadLedger();
            l.Add(1, 0, "kbs", 50);
            l.Add(1, 1, "kbs", 60);
            Assert.Equal(55, l.Get(1, "kbs", skipSeconds: 3).Mean);
        }

        [Fact]
        public void Missing_values_are_not_counted()
        {
            var l = new LoadLedger();
            l.Add(0, 0, "rtt", double.NaN);
            l.Add(0, 1, "rtt", 30);
            Assert.Equal(1, l.Get(0, "rtt").Count);
            Assert.Equal(30, l.Get(0, "rtt").Mean);
        }

        [Fact]
        public void A_metric_never_measured_has_an_empty_summary()
        {
            var s = new LoadLedger().Get(4, "nothing");
            Assert.Equal(0, s.Count);
            Assert.True(double.IsNaN(s.Mean));
            Assert.True(double.IsNaN(s.P95));
        }

        [Fact]
        public void The_series_keeps_the_seconds_in_order()
        {
            var l = new LoadLedger();
            l.Add(3, 1, "stale", 4);
            l.Add(3, 0, "stale", 9);
            l.Add(3, 2, "stale", 1);
            Assert.Equal(new double[] { 9, 4, 1 }, l.Series(3, "stale").ToArray());
        }
    }
}
